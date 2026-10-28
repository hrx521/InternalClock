# InternalClock

netstandard2.0 不依赖客户端本机时钟的进程内软件时钟。远程时间由调用方的委托提供，之后用 `Stopwatch` 推算，不读本机墙钟。

```csharp
var clock = new SoftwareClock(
    getUtc: ReadUtcFromSqlAsync,
    calibrateOnStart: true,
    calibrateInterval: TimeSpan.FromHours(6));

DateTimeOffset utc = clock.UtcNow;
```

`calibrateInterval` 传 `TimeSpan.Zero` 时不自动校准。需要时调用 `await clock.CalibrateAsync()`。不要在界面线程上同步等待这次校准。

当前时间 = 上次委托返回的 UTC + 此后 `Stopwatch` 走过的时间。计数器在拿到委托结果的那一刻重新记下。失败时保留上一次成功的原点，异常在 `LastError`。

委托要返回 UTC。`DateTimeOffset` 带偏移时会折成 UTC。委托内部不要再调用 `CalibrateAsync`。委托自己负责超时；它若不返回，后续校准会一直等它。


比如：可以将下面代码放在您项目的初始化逻辑里，确保在使用 `server_time` 前已经初始化了 `_clock`。

```csharp
public static DateTime server_time 
{
    get
    {
        bool isOk =_clock.TryGetBeijingTime(out DateTimeOffset beijingTime);
        if(isOk)
            return beijingTime.DateTime;
        throw new Exception("获取服务器时间失败，请检查网络连接和服务器状态");
    }
}
private static readonly SoftwareClock _clock = new SoftwareClock(ReadUtcFromSqlAsync, calibrateOnStart: true, calibrateInterval: TimeSpan.FromHours(2));

/// <summary>
/// 获取远程服务器UTC时间，用于校准系统内部时钟
/// </summary>
/// <returns></returns>
/// <exception cref="InvalidOperationException"></exception>
public static async Task<DateTimeOffset> ReadUtcFromSqlAsync()
{
    //等待连接字符串就绪
    while (string.IsNullOrEmpty(constr))
        await Task.Delay(500).ConfigureAwait(false);

    using (SqlConnection connection = new SqlConnection(constr))
    using (SqlCommand command = new SqlCommand("SELECT SYSUTCDATETIME();", connection))
    {
        command.CommandTimeout = 5;
        await connection.OpenAsync().ConfigureAwait(false);
        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        object value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        if (value == null || value == DBNull.Value)
            throw new InvalidOperationException("数据库没有返回时间。");
        DateTime utc = DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc);
        long totalTicksRequestResponse = stopWatch.Elapsed.Ticks;
        long halfTickRequestResponse = totalTicksRequestResponse / 2;
        TimeSpan half = TimeSpan.FromTicks(halfTickRequestResponse);// 取请求响应时间的一半，作为网络延迟的估计进行补偿
        utc = utc.Add(half);
        return new DateTimeOffset(utc);
    }
}
```
