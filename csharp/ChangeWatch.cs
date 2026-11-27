using System.Diagnostics;
using System.Runtime.InteropServices;
using TwinCAT.Ads;

namespace AdsReadDemo;

/// <summary>模式 C / D 的結果。延遲為 null 代表沒有可信的量測 (見 LatencyNote)。</summary>
internal sealed record ChangeResult(
    string Name,
    double Seconds,
    int Requests,
    int Notifications,
    int Changes,
    int Missed,
    double? AvgLatencyMs,
    double? MaxLatencyMs,
    double CpuPercent,
    string? LatencyNote)
{
    public double RequestsPerSec => Requests / Seconds;

    public double NotificationsPerSec => Notifications / Seconds;
}

/// <summary>
/// 模式 C (每 PollMs 讀一次 MAIN.stTrigger) 與模式 D (OnChange notification)。
/// 兩個模式看同一個變數、跑同樣秒數，量：C# 送出的 ADS 請求數、PLC 送來的通知數、
/// 看到幾次變化、「PLC 改值 → C# 看到」的延遲、這個程序的 CPU%。
/// </summary>
internal sealed class ChangeWatch
{
    private readonly AdsClient _client;
    private readonly Options _opt;

    public ChangeWatch(AdsClient client, Options opt)
    {
        _client = client;
        _opt = opt;
    }

    /// <summary>模式 C：固定間隔輪詢。</summary>
    public ChangeResult RunPolling()
    {
        uint handle = _client.CreateVariableHandle(PlcSymbols.Trigger);
        try
        {
            using var latency = LatencyProbe.Create(_client, _opt);
            using var timer = TimerResolution.Request1Ms();
            var tracker = new ChangeTracker(latency);
            int requests = 0;
            latency.Begin(); // 時鐘校正 / 啟動觸發在計時之前，不算進量測
            var cpu = CpuMeter.Start();
            var sw = Stopwatch.StartNew();
            var progress = new Progress(Text.ModeCName);
            long periodTicks = Stopwatch.Frequency * _opt.PollMs / 1000;
            long next = Stopwatch.GetTimestamp();
            while (sw.Elapsed.TotalSeconds < _opt.Seconds)
            {
                var read = _client.ReadAsResult(handle, PlcSymbols.TriggerSize);
                requests++;
                read.ThrowOnError();
                tracker.Observe(read.Data.Span, Stopwatch.GetTimestamp(), DateTime.UtcNow.ToFileTimeUtc());
                progress.Tick(sw.Elapsed, requests, 0, tracker);

                next += periodTicks;
                long wait = next - Stopwatch.GetTimestamp();
                if (wait > 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency));
                }
                else
                {
                    next = Stopwatch.GetTimestamp(); // 讀太慢趕不上就不補，避免連發
                }
            }
            double seconds = sw.Elapsed.TotalSeconds;
            double cpuPct = cpu.Percent();
            latency.End();
            progress.Done();
            return tracker.ToResult(Text.ModeCName, seconds, requests, 0, cpuPct, latency.Note);
        }
        finally
        {
            _client.DeleteVariableHandle(handle);
        }
    }

    /// <summary>模式 D：AddDeviceNotification (OnChange)，值變了 PLC 才送。</summary>
    public ChangeResult RunNotification()
    {
        using var latency = LatencyProbe.Create(_client, _opt);
        var tracker = new ChangeTracker(latency);
        int notifications = 0;
        uint notifyHandle = 0;
        object gate = new();

        void OnNotification(object? sender, AdsNotificationEventArgs e)
        {
            if (e.Handle != notifyHandle) return;
            long ts = Stopwatch.GetTimestamp();
            long fileTime = DateTime.UtcNow.ToFileTimeUtc();
            lock (gate)
            {
                notifications++;
                tracker.Observe(e.Data.Span, ts, fileTime);
            }
        }

        var settings = new NotificationSettings(AdsTransMode.OnChange, _opt.NotifyCycleMs, _opt.NotifyMaxDelayMs);
        latency.Begin(); // 時鐘校正 / 啟動觸發在計時之前，不算進量測
        var cpu = CpuMeter.Start();
        var sw = Stopwatch.StartNew();
        var progress = new Progress(Text.ModeDName);
        int requests = 0;
        _client.AdsNotification += OnNotification;
        try
        {
            lock (gate)
            {
                notifyHandle = _client.AddDeviceNotification(PlcSymbols.Trigger, PlcSymbols.TriggerSize, settings, null);
                requests++;
            }
            while (sw.Elapsed.TotalSeconds < _opt.Seconds)
            {
                Thread.Sleep(100);
                lock (gate)
                {
                    progress.Tick(sw.Elapsed, requests, notifications, tracker);
                }
            }
        }
        finally
        {
            if (notifyHandle != 0)
            {
                _client.DeleteDeviceNotification(notifyHandle);
                requests++;
            }
            _client.AdsNotification -= OnNotification;
        }
        double seconds = sw.Elapsed.TotalSeconds;
        double cpuPct = cpu.Percent();
        latency.End();
        progress.Done();
        lock (gate)
        {
            return tracker.ToResult(Text.ModeDName, seconds, requests, notifications, cpuPct, latency.Note);
        }
    }

    /// <summary>記錄 nTrigger 的變化與延遲。第一筆只當基準，不算變化。</summary>
    private sealed class ChangeTracker
    {
        private readonly LatencyProbe _latency;
        private readonly List<double> _latenciesMs = new();
        private uint? _last;

        public ChangeTracker(LatencyProbe latency) => _latency = latency;

        public int Changes { get; private set; }

        public int Missed { get; private set; }

        public double? LastLatencyMs { get; private set; }

        public void Observe(ReadOnlySpan<byte> data, long stopwatchTs, long hostFileTime)
        {
            var (count, plcTime) = PlcSymbols.ParseTrigger(data);
            if (_last is null)
            {
                _last = count;
                return;
            }
            if (count == _last) return;
            uint step = count - _last.Value;
            if (step > 1) Missed += (int)(step - 1);
            _last = count;
            Changes++;
            double? ms = _latency.LatencyMs(plcTime, stopwatchTs, hostFileTime);
            if (ms is double v)
            {
                _latenciesMs.Add(v);
                LastLatencyMs = v;
            }
        }

        public ChangeResult ToResult(string name, double seconds, int requests, int notifications, double cpuPct, string? note) =>
            new(name, seconds, requests, notifications, Changes, Missed,
                _latenciesMs.Count > 0 && note is null ? _latenciesMs.Average() : null,
                _latenciesMs.Count > 0 && note is null ? _latenciesMs.Max() : null,
                cpuPct,
                note ?? (_latenciesMs.Count == 0 ? Text.NoChangeSeen : null));
    }

    /// <summary>每秒更新一行進度 (錄影用)。</summary>
    private sealed class Progress
    {
        private readonly string _name;
        private int _lastSecond = -1;

        public Progress(string name) => _name = name;

        public void Tick(TimeSpan elapsed, int requests, int notifications, ChangeTracker tracker)
        {
            int s = (int)elapsed.TotalSeconds;
            if (s == _lastSecond) return;
            _lastSecond = s;
            Console.Write("\r" + Text.ProgressLine(_name, s, requests, notifications, tracker.Changes, tracker.LastLatencyMs) + "   ");
        }

        public void Done() => Console.WriteLine();
    }
}

/// <summary>
/// 算「PLC 改值 → C# 看到」的延遲。
/// Plc：host 收到的 Windows UTC 時間 − PLC 寫進 stTrigger 的 F_GetSystemTime()；前後各檢查一次兩個時鐘的差，
///      差太多 (UmRT 的 PLC 時間可能不跟 Windows 走) 就不給數字。
/// Host：C# 自己寫 MAIN.nHostRequest 觸發，延遲 = 看到變化的 Stopwatch − 寫入前的 Stopwatch，不依賴 PLC 時鐘；
///      含一次 ADS 寫入的時間，所以是上限。
/// </summary>
internal abstract class LatencyProbe : IDisposable
{
    public string? Note { get; protected set; }

    public static LatencyProbe Create(AdsClient client, Options opt) => opt.Trigger switch
    {
        TriggerSource.Host => new HostTriggerProbe(client, opt),
        _ => new PlcClockProbe(client, opt),
    };

    public abstract void Begin();

    public abstract void End();

    public abstract double? LatencyMs(long plcFileTime, long stopwatchTs, long hostFileTime);

    public virtual void Dispose()
    {
    }
}

internal sealed class PlcClockProbe : LatencyProbe
{
    private const int Samples = 20;
    private readonly AdsClient _client;
    private readonly Options _opt;
    private double _offsetBeginMs;

    public PlcClockProbe(AdsClient client, Options opt)
    {
        _client = client;
        _opt = opt;
    }

    public override void Begin() => _offsetBeginMs = MeasureOffsetMs();

    public override void End()
    {
        double offsetEndMs = MeasureOffsetMs();
        Console.WriteLine(Text.ClockOffset(_offsetBeginMs, offsetEndMs));
        // 時鐘一致時，差值 = 讀取時間 − PLC cycle 開始時間，落在 0 ~ 1 個 cycle (+ 讀取來回)
        bool ok = Within(_offsetBeginMs) && Within(offsetEndMs);
        if (!ok) Note = Text.ClockNotTrusted(_opt.MaxClockDriftMs);
    }

    private bool Within(double offsetMs) => offsetMs > -1.0 && offsetMs < _opt.MaxClockDriftMs;

    public override double? LatencyMs(long plcFileTime, long stopwatchTs, long hostFileTime) =>
        (hostFileTime - plcFileTime) / 10_000.0;

    /// <summary>讀 MAIN.nPlcTimeNow 多次，取來回時間最短那筆的 (host 中點 − PLC 時間)。</summary>
    private double MeasureOffsetMs()
    {
        uint handle = _client.CreateVariableHandle(PlcSymbols.PlcTimeNow);
        try
        {
            double best = double.NaN;
            long bestRtt = long.MaxValue;
            for (int i = 0; i < Samples; i++)
            {
                long t0 = DateTime.UtcNow.ToFileTimeUtc();
                ulong plc = (ulong)_client.ReadAny(handle, typeof(ulong));
                long t1 = DateTime.UtcNow.ToFileTimeUtc();
                if (t1 - t0 < bestRtt)
                {
                    bestRtt = t1 - t0;
                    best = ((t0 + t1) / 2.0 - plc) / 10_000.0;
                }
            }
            return best;
        }
        finally
        {
            _client.DeleteVariableHandle(handle);
        }
    }
}

internal sealed class HostTriggerProbe : LatencyProbe
{
    private readonly AdsClient _client;
    private readonly Options _opt;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private bool _autoBefore;
    private long _pendingWriteTs;

    public HostTriggerProbe(AdsClient client, Options opt)
    {
        _client = client;
        _opt = opt;
    }

    public override void Begin()
    {
        _autoBefore = (bool)_client.ReadValue(PlcSymbols.AutoTrigger, typeof(bool));
        _client.WriteValue(PlcSymbols.AutoTrigger, false);
        _thread = new Thread(Loop) { IsBackground = true, Name = "HostTrigger" };
        _thread.Start();
    }

    public override void End()
    {
        if (_thread is null) return;
        _stop.Cancel();
        _thread.Join();
        _thread = null;
        _client.WriteValue(PlcSymbols.AutoTrigger, _autoBefore); // 還原 PLC 自動觸發
    }

    public override double? LatencyMs(long plcFileTime, long stopwatchTs, long hostFileTime)
    {
        long written = Interlocked.Exchange(ref _pendingWriteTs, 0);
        if (written == 0) return null; // 不是 C# 觸發的變化 (例如切換前 PLC 自己 +1)
        return (stopwatchTs - written) * 1000.0 / Stopwatch.Frequency;
    }

    // 另開執行緒寫觸發，不佔用模式 C 的輪詢迴圈；寫入次數不算進「讀」的 ADS 請求
    private void Loop()
    {
        uint handle = _client.CreateVariableHandle(PlcSymbols.HostRequest);
        try
        {
            uint request = (uint)_client.ReadAny(handle, typeof(uint));
            while (!_stop.Token.WaitHandle.WaitOne(_opt.HostTriggerMs))
            {
                request++;
                Interlocked.Exchange(ref _pendingWriteTs, Stopwatch.GetTimestamp());
                _client.WriteAny(handle, request);
            }
        }
        finally
        {
            _client.DeleteVariableHandle(handle);
        }
    }

    // 例外中斷時也要停掉觸發執行緒並還原 bAutoTrigger
    public override void Dispose()
    {
        try
        {
            End();
        }
        finally
        {
            _stop.Dispose();
        }
    }
}

/// <summary>這個程序在一段時間內用掉的 CPU，換算成整台電腦的 % (同工作管理員)。</summary>
internal sealed class CpuMeter
{
    private readonly TimeSpan _cpu0;
    private readonly long _ts0;

    private CpuMeter()
    {
        _cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        _ts0 = Stopwatch.GetTimestamp();
    }

    public static CpuMeter Start() => new();

    public double Percent()
    {
        double cpuSec = (Process.GetCurrentProcess().TotalProcessorTime - _cpu0).TotalSeconds;
        double wallSec = (double)(Stopwatch.GetTimestamp() - _ts0) / Stopwatch.Frequency;
        return wallSec <= 0 ? 0 : cpuSec / wallSec / Environment.ProcessorCount * 100.0;
    }
}

/// <summary>Windows 預設 timer 解析度約 15.6 ms，Thread.Sleep(10) 實際會睡 15 ms 以上；輪詢期間調成 1 ms。</summary>
internal sealed class TimerResolution : IDisposable
{
    private readonly bool _set;

    private TimerResolution(bool set) => _set = set;

    public static TimerResolution Request1Ms() =>
        new(OperatingSystem.IsWindows() && timeBeginPeriod(1) == 0);

    public void Dispose()
    {
        if (_set) timeEndPeriod(1);
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);
}
