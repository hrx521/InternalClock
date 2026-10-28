using System;
using System.Diagnostics;
using System.Threading.Tasks;
using InternalClock;

namespace InternalClock.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                TestArguments();
                TestFormula().GetAwaiter().GetResult();
                TestReplacesAnchor().GetAwaiter().GetResult();
                TestFailureKeepsAnchor().GetAwaiter().GetResult();
                TestNullTask().GetAwaiter().GetResult();
                TestReentrancy().GetAwaiter().GetResult();
                TestStartOnce().GetAwaiter().GetResult();
                TestConstructorDoesNotBlock().GetAwaiter().GetResult();
                TestStartupFailure().GetAwaiter().GetResult();
                TestInterval().GetAwaiter().GetResult();
                TestTimerFailureKeepsAnchor().GetAwaiter().GetResult();
                TestZeroIntervalDoesNotRepeat().GetAwaiter().GetResult();
                TestDispose().GetAwaiter().GetResult();
                Console.WriteLine("ALL PASSED");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED");
                Console.WriteLine(ex);
                return 1;
            }
        }

        private static void TestArguments()
        {
            try
            {
                SoftwareClock ignored = new SoftwareClock(null, false, TimeSpan.Zero);
                ignored.Dispose();
                throw new Exception("空委托应被拒绝");
            }
            catch (ArgumentNullException)
            {
            }

            ExpectRange(TimeSpan.FromTicks(-1));
            ExpectRange(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond / 2));
            ExpectRange(TimeSpan.FromMilliseconds(int.MaxValue).Add(TimeSpan.FromSeconds(1)));
            Console.WriteLine("arguments ok");
        }

        private static void ExpectRange(TimeSpan interval)
        {
            try
            {
                SoftwareClock ignored = new SoftwareClock(
                    delegate { return Task.FromResult(DateTimeOffset.UtcNow); },
                    false,
                    interval);
                ignored.Dispose();
                throw new Exception("非法间隔应被拒绝：" + interval);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        private static async Task TestFormula()
        {
            DateTime external = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
            SoftwareClock clock = new SoftwareClock(
                delegate { return Task.FromResult(new DateTimeOffset(external)); },
                false,
                TimeSpan.Zero);

            DateTimeOffset ignored;
            if (clock.TryGetUtcNow(out ignored) || clock.IsCalibrated)
                throw new Exception("校准前不应有读数");
            try
            {
                DateTimeOffset unread = clock.UtcNow;
                throw new Exception("校准前 UtcNow 应抛出，实际 " + unread.ToString("o"));
            }
            catch (InvalidOperationException)
            {
            }

            await clock.CalibrateAsync().ConfigureAwait(false);
            DateTimeOffset first = clock.UtcNow;
            if ((first - new DateTimeOffset(external)).Duration() > TimeSpan.FromSeconds(1))
                throw new Exception("校准后应贴近委托返回的时间：" + first.ToString("o"));

            Stopwatch watch = Stopwatch.StartNew();
            await Task.Delay(300).ConfigureAwait(false);
            DateTimeOffset second = clock.UtcNow;
            watch.Stop();
            TimeSpan advanced = second - first;
            if ((advanced - watch.Elapsed).Duration() > TimeSpan.FromMilliseconds(150))
                throw new Exception("与 Stopwatch 不一致：" + advanced.TotalMilliseconds + " vs " + watch.Elapsed.TotalMilliseconds);

            clock.Dispose();
            Console.WriteLine("formula ok, advancedMs=" + advanced.TotalMilliseconds.ToString("0"));
        }

        private static async Task TestReplacesAnchor()
        {
            DateTime first = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
            DateTime second = first.AddDays(-1);
            bool useSecond = false;
            SoftwareClock clock = new SoftwareClock(
                delegate { return Task.FromResult(new DateTimeOffset(useSecond ? second : first)); },
                false,
                TimeSpan.Zero);

            await clock.CalibrateAsync().ConfigureAwait(false);
            await Task.Delay(1200).ConfigureAwait(false);
            useSecond = true;
            await clock.CalibrateAsync().ConfigureAwait(false);

            TimeSpan gap = (clock.UtcNow - new DateTimeOffset(second)).Duration();
            if (gap > TimeSpan.FromMilliseconds(500))
                throw new Exception("再次校准没有换成新原点：" + gap.TotalMilliseconds + " ms");
            if (clock.CalibrationCount != 2)
                throw new Exception("成功次数应为 2");

            clock.Dispose();
            Console.WriteLine("replace anchor ok");
        }

        private static async Task TestFailureKeepsAnchor()
        {
            bool fail = false;
            DateTime external = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
            SoftwareClock clock = new SoftwareClock(
                delegate
                {
                    if (fail)
                        throw new InvalidOperationException("模拟失败");
                    return Task.FromResult(new DateTimeOffset(external));
                },
                false,
                TimeSpan.Zero);

            await clock.CalibrateAsync().ConfigureAwait(false);
            DateTimeOffset before = clock.UtcNow;
            fail = true;
            try
            {
                await clock.CalibrateAsync().ConfigureAwait(false);
                throw new Exception("失败的校准应该抛出");
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message == "失败的校准应该抛出")
                    throw;
            }

            await Task.Delay(100).ConfigureAwait(false);
            if (clock.UtcNow < before)
                throw new Exception("失败后时钟倒退");
            if (clock.CalibrationCount != 1)
                throw new Exception("失败不应增加成功次数");
            if (clock.LastError == null)
                throw new Exception("失败应记入 LastError");

            fail = false;
            await clock.CalibrateAsync().ConfigureAwait(false);
            if (clock.LastError != null)
                throw new Exception("成功后应清除 LastError");
            if (clock.CalibrationCount != 2)
                throw new Exception("随后的成功应计入次数");

            clock.Dispose();
            Console.WriteLine("failure keeps anchor ok");
        }

        private static async Task TestNullTask()
        {
            SoftwareClock clock = new SoftwareClock(
                delegate { return null; },
                false,
                TimeSpan.Zero);

            try
            {
                await clock.CalibrateAsync().ConfigureAwait(false);
                throw new Exception("空任务应失败");
            }
            catch (NullReferenceException)
            {
            }

            if (clock.IsCalibrated || clock.LastError == null)
                throw new Exception("空任务不应留下原点");

            clock.Dispose();
            Console.WriteLine("null task ok");
        }

        private static async Task TestReentrancy()
        {
            DateTime external = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
            SoftwareClock clock = null;
            clock = new SoftwareClock(
                delegate
                {
                    clock.CalibrateAsync().GetAwaiter().GetResult();
                    return Task.FromResult(new DateTimeOffset(external));
                },
                false,
                TimeSpan.Zero);

            Task calibrating = clock.CalibrateAsync();
            Task finished = await Task.WhenAny(calibrating, Task.Delay(2000)).ConfigureAwait(false);
            if (finished != calibrating)
                throw new Exception("委托内再次校准发生死锁");

            try
            {
                await calibrating.ConfigureAwait(false);
                throw new Exception("委托内再次校准应失败");
            }
            catch (InvalidOperationException)
            {
            }

            if (clock.IsCalibrated)
                throw new Exception("重入失败不应留下原点");

            clock.Dispose();
            Console.WriteLine("reentrancy ok");
        }

        private static async Task TestStartOnce()
        {
            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>();
            SoftwareClock clock = new SoftwareClock(
                delegate
                {
                    done.TrySetResult(true);
                    return Task.FromResult(DateTimeOffset.UtcNow);
                },
                true,
                TimeSpan.Zero);

            Task finished = await Task.WhenAny(done.Task, Task.Delay(3000)).ConfigureAwait(false);
            if (finished != done.Task)
                throw new Exception("启动校准没有发生");

            await WaitUntil(delegate { return clock.IsCalibrated; }, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (clock.CalibrationCount != 1)
                throw new Exception("只应校准一次，实际 " + clock.CalibrationCount);

            await Task.Delay(400).ConfigureAwait(false);
            if (clock.CalibrationCount != 1)
                throw new Exception("间隔为 0 时不应再次校准");

            clock.Dispose();
            Console.WriteLine("start once ok");
        }

        private static async Task TestConstructorDoesNotBlock()
        {
            TaskCompletionSource<bool> entered = new TaskCompletionSource<bool>();
            TaskCompletionSource<bool> release = new TaskCompletionSource<bool>();
            SoftwareClock clock = null;
            DateTime external = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);

            Task creating = Task.Run(delegate
            {
                clock = new SoftwareClock(
                    async delegate
                    {
                        entered.TrySetResult(true);
                        await release.Task.ConfigureAwait(false);
                        return new DateTimeOffset(external);
                    },
                    true,
                    TimeSpan.Zero);
            });

            Task created = await Task.WhenAny(creating, Task.Delay(2000)).ConfigureAwait(false);
            if (created != creating)
                throw new Exception("构造函数被校准挡住");
            await creating.ConfigureAwait(false);

            Task sawEnter = await Task.WhenAny(entered.Task, Task.Delay(3000)).ConfigureAwait(false);
            if (sawEnter != entered.Task)
                throw new Exception("启动校准没有开始");

            release.TrySetResult(true);
            await WaitUntil(delegate { return clock.IsCalibrated; }, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if ((clock.UtcNow - new DateTimeOffset(external)).Duration() > TimeSpan.FromSeconds(1))
                throw new Exception("启动校准的原点不对");

            clock.Dispose();
            Console.WriteLine("constructor does not block ok");
        }

        private static async Task TestStartupFailure()
        {
            SoftwareClock clock = new SoftwareClock(
                delegate { throw new InvalidOperationException("启动失败"); },
                true,
                TimeSpan.Zero);

            await WaitUntil(delegate { return clock.LastError != null; }, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (clock.IsCalibrated || clock.CalibrationCount != 0)
                throw new Exception("启动失败不应留下原点");

            clock.Dispose();
            Console.WriteLine("startup failure ok");
        }

        private static async Task TestInterval()
        {
            SoftwareClock clock = new SoftwareClock(
                delegate { return Task.FromResult(DateTimeOffset.UtcNow); },
                false,
                TimeSpan.FromMilliseconds(200));

            await WaitUntil(delegate { return clock.CalibrationCount >= 2; }, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            long count = clock.CalibrationCount;
            clock.Dispose();
            await Task.Delay(500).ConfigureAwait(false);
            if (clock.CalibrationCount > count + 1)
                throw new Exception("Dispose 之后仍在自动校准");

            Console.WriteLine("interval ok, count=" + count);
        }

        private static async Task TestTimerFailureKeepsAnchor()
        {
            int calls = 0;
            DateTime external = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
            SoftwareClock clock = new SoftwareClock(
                delegate
                {
                    int n = System.Threading.Interlocked.Increment(ref calls);
                    if (n == 1)
                        return Task.FromResult(new DateTimeOffset(external));
                    throw new InvalidOperationException("后续失败");
                },
                false,
                TimeSpan.FromMilliseconds(200));

            await WaitUntil(
                delegate { return clock.CalibrationCount == 1 && clock.LastError != null; },
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            DateTimeOffset value = clock.UtcNow;
            if ((value - new DateTimeOffset(external)).Duration() > TimeSpan.FromSeconds(3))
                throw new Exception("自动校准失败后原点丢了");
            await Task.Delay(150).ConfigureAwait(false);
            if (clock.UtcNow < value)
                throw new Exception("自动校准失败后时钟倒退");

            clock.Dispose();
            Console.WriteLine("timer failure ok");
        }

        private static async Task TestZeroIntervalDoesNotRepeat()
        {
            SoftwareClock clock = new SoftwareClock(
                delegate { return Task.FromResult(DateTimeOffset.UtcNow); },
                false,
                TimeSpan.Zero);

            await clock.CalibrateAsync().ConfigureAwait(false);
            await Task.Delay(400).ConfigureAwait(false);
            if (clock.CalibrationCount != 1)
                throw new Exception("未要求自动校准，次数应为 1");

            clock.Dispose();
            Console.WriteLine("zero interval ok");
        }

        private static async Task TestDispose()
        {
            DateTime external = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
            SoftwareClock clock = new SoftwareClock(
                delegate { return Task.FromResult(new DateTimeOffset(external)); },
                false,
                TimeSpan.FromMilliseconds(200));

            await clock.CalibrateAsync().ConfigureAwait(false);
            clock.Dispose();
            clock.Dispose();
            if (!clock.IsCalibrated)
                throw new Exception("释放后应仍能识别已校准");
            DateTimeOffset value = clock.UtcNow;

            try
            {
                await clock.CalibrateAsync().ConfigureAwait(false);
                throw new Exception("释放后不应再校准");
            }
            catch (ObjectDisposedException)
            {
            }

            if (clock.UtcNow < value)
                throw new Exception("释放后读数倒退");

            Console.WriteLine("dispose ok");
        }

        private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed > timeout)
                    throw new TimeoutException("等待超时");
                await Task.Delay(20).ConfigureAwait(false);
            }
        }
    }
}
