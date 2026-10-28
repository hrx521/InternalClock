using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InternalClock
{
    /// <summary>
    /// 进程内软件时钟。
    /// 当前时间 = 上次委托返回的 UTC + 此后 Stopwatch 走过的时间。
    /// 不读取本机墙钟。Windows 的性能计数器已包含待机、休眠和连接待机。
    /// </summary>
    public sealed class SoftwareClock : IDisposable
    {
        private static readonly double TimestampToTicks = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

        private readonly Func<Task<DateTimeOffset>> _getUtc;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly AsyncLocal<int> _reentrancy = new AsyncLocal<int>();

        private Timer _timer;
        private volatile Anchor _anchor;
        private volatile Exception _lastError;
        private int _disposed;
        private long _calibrationCount;
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        /// <summary>
        /// 创建时钟。构造函数不等待网络；若 <paramref name="calibrateOnStart"/> 为 true，会在后台立刻校准一次。
        /// </summary>
        /// <param name="getUtc">
        /// 返回 UTC 时刻的委托。<see cref="DateTimeOffset"/> 带有偏移时会按该偏移折成 UTC。
        /// 委托自己要有超时。不要在委托里再次调用本时钟的校准方法，否则抛出 <see cref="InvalidOperationException"/>。
        /// </param>
        /// <param name="calibrateOnStart">为 true 时，创建后立刻在后台校准一次。</param>
        /// <param name="calibrateInterval">
        /// 自动校准间隔，至少 1 毫秒。传 <see cref="TimeSpan.Zero"/> 则不自动校准。
        /// 第一次自动校准在一个间隔之后，不会和 <paramref name="calibrateOnStart"/> 叠在同一刻。
        /// </param>
        /// <exception cref="ArgumentNullException">委托为 null。</exception>
        /// <exception cref="ArgumentOutOfRangeException">间隔为负、小于 1 毫秒，或大于约 24.9 天。</exception>
        public SoftwareClock(Func<Task<DateTimeOffset>> getUtc, bool calibrateOnStart, TimeSpan calibrateInterval)
        {
            if (getUtc == null)
                throw new ArgumentNullException(nameof(getUtc));
            if (calibrateInterval < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(calibrateInterval), "校准间隔不能为负。传 TimeSpan.Zero 表示不自动校准。");
            if (calibrateInterval > TimeSpan.Zero && calibrateInterval.TotalMilliseconds < 1d)
                throw new ArgumentOutOfRangeException(nameof(calibrateInterval), "自动校准间隔至少为 1 毫秒。传 TimeSpan.Zero 表示不自动校准。");
            if (calibrateInterval > TimeSpan.Zero && calibrateInterval.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(calibrateInterval), "校准间隔必须小于约 24.9 天。");

            _getUtc = getUtc;

            if (calibrateInterval > TimeSpan.Zero)
                _timer = new Timer(OnTimer, null, calibrateInterval, calibrateInterval);

            if (calibrateOnStart)
            {
                Task fire = Task.Run(() => CalibrateAsync());
                fire.ContinueWith(
                    delegate(Task finished) { Exception ignored = finished.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
        }
        public bool TryGetBeijingTime(out DateTimeOffset beijing)
        {
            DateTimeOffset utc;
            if (!this.TryGetUtcNow(out utc))
            {
                beijing = default(DateTimeOffset);
                return false;
            }
            beijing = utc.ToOffset(BeijingOffset);
            return true;
        }
        /// <summary>至少成功校准过一次。</summary>
        public bool IsCalibrated
        {
            get { return _anchor != null; }
        }

        /// <summary>最近一次校准失败的异常。成功后清空。自动校准失败只记在这里，不向外抛。</summary>
        public Exception LastError
        {
            get { return _lastError; }
        }

        /// <summary>成功校准的次数。失败不计入。</summary>
        public long CalibrationCount
        {
            get { return Interlocked.Read(ref _calibrationCount); }
        }

        /// <summary>上次校准委托返回的 UTC，不是推算出来的当前时间。尚未成功时为 null。</summary>
        public DateTimeOffset? LastSyncedUtc
        {
            get
            {
                Anchor anchor = _anchor;
                if (anchor == null)
                    return null;
                return new DateTimeOffset(anchor.ExternalUtc, TimeSpan.Zero);
            }
        }

        /// <summary>
        /// 推算的当前 UTC。尚未成功校准过时抛出 <see cref="InvalidOperationException"/>。
        /// 已释放的时钟仍可读取最后一次成功的时间。
        /// </summary>
        public DateTimeOffset UtcNow
        {
            get
            {
                DateTimeOffset value;
                if (!TryGetUtcNow(out value))
                {
                    if (_anchor == null)
                        throw new InvalidOperationException("时钟尚未完成第一次校准。");
                    throw new InvalidOperationException("当前时间超出可表示范围。");
                }

                return value;
            }
        }

        /// <summary>尚未成功校准，或结果超出 <see cref="DateTime"/> 范围时返回 false。</summary>
        public bool TryGetUtcNow(out DateTimeOffset utc)
        {
            Anchor anchor = _anchor;
            if (anchor == null)
            {
                utc = default(DateTimeOffset);
                return false;
            }

            TimeSpan elapsed = ElapsedSince(anchor.Timestamp);
            try
            {
                utc = new DateTimeOffset(anchor.ExternalUtc.Add(elapsed), TimeSpan.Zero);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                utc = default(DateTimeOffset);
                return false;
            }
        }

        /// <summary>
        /// 立刻再调用一次委托。完成后以这次结果和当时的性能计数器为新原点。
        /// 失败时保留上一次成功的原点，异常记入 <see cref="LastError"/> 并原样抛出。
        /// 不要在界面线程上同步等待这个任务。
        /// </summary>
        /// <exception cref="ObjectDisposedException">时钟已释放。</exception>
        /// <exception cref="InvalidOperationException">从获取时间的委托内部再次调用了校准。</exception>
        public async Task CalibrateAsync()
        {
            if (_reentrancy.Value > 0)
                throw new InvalidOperationException("不能在获取时间的委托内部再次校准。");
            if (Volatile.Read(ref _disposed) == 1)
                throw new ObjectDisposedException(nameof(SoftwareClock));

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await ApplySampleAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>停止以后的自动校准。已经校准出的时间仍可读取。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            Timer timer = _timer;
            _timer = null;
            if (timer != null)
                timer.Dispose();
        }

        private void OnTimer(object state)
        {
            if (Volatile.Read(ref _disposed) == 1)
                return;
            if (!_gate.Wait(0))
                return;

            try
            {
                if (Volatile.Read(ref _disposed) == 1)
                    return;
                ApplySampleAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _disposed) != 1)
                    _lastError = ex;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task ApplySampleAsync()
        {
            if (Volatile.Read(ref _disposed) == 1)
                throw new ObjectDisposedException(nameof(SoftwareClock));

            _reentrancy.Value = _reentrancy.Value + 1;
            DateTimeOffset sample;
            long timestamp;
            try
            {
                sample = await _getUtc().ConfigureAwait(false);
                timestamp = Stopwatch.GetTimestamp();
            }
            catch (Exception ex)
            {
                _lastError = ex;
                throw;
            }
            finally
            {
                _reentrancy.Value = _reentrancy.Value - 1;
            }

            if (Volatile.Read(ref _disposed) == 1)
                throw new ObjectDisposedException(nameof(SoftwareClock));

            _anchor = new Anchor(sample.UtcDateTime, timestamp);
            _lastError = null;
            Interlocked.Increment(ref _calibrationCount);
        }

        private static TimeSpan ElapsedSince(long timestamp)
        {
            long delta = Stopwatch.GetTimestamp() - timestamp;
            if (delta <= 0)
                return TimeSpan.Zero;

            double ticks = delta * TimestampToTicks;
            if (ticks >= TimeSpan.MaxValue.Ticks)
                return TimeSpan.MaxValue;
            return TimeSpan.FromTicks((long)ticks);
        }

        private sealed class Anchor
        {
            public readonly DateTime ExternalUtc;
            public readonly long Timestamp;

            public Anchor(DateTime externalUtc, long timestamp)
            {
                ExternalUtc = DateTime.SpecifyKind(externalUtc, DateTimeKind.Utc);
                Timestamp = timestamp;
            }
        }
    }
}
