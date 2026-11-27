using System.Text;

namespace AdsReadDemo;

/// <summary>console 輸出語言 (--lang zh|en)。</summary>
internal enum Lang
{
    Zh,
    En,
}

/// <summary>
/// 程式印出的所有文字，中英文集中在這裡；呼叫點只用 Text.Xxx，不判斷語言。
/// </summary>
internal static class Text
{
    /// <summary>目前語言；Options.Parse 依 --lang 設定，預設中文。</summary>
    public static Lang Lang { get; set; } = Lang.Zh;

    private static string P(string zh, string en) => Lang == Lang.En ? en : zh;

    // ---------- console 編碼 ----------

    /// <summary>把 console 輸出改成 UTF-8，cp950 的終端機不用先 chcp 65001；結束時還原。</summary>
    public static void UseUtf8Console()
    {
        try
        {
            var outEnc = Console.OutputEncoding;
            Console.OutputEncoding = Encoding.UTF8;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => TryRestore(() => Console.OutputEncoding = outEnc);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // 沒有 console (輸出被導向)：維持預設編碼
        }
    }

    private static void TryRestore(Action restore)
    {
        try { restore(); } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }
    }

    // ---------- 命令列參數 ----------

    public static string MissingValue(string arg) => P($"{arg} 缺少值", $"{arg} needs a value");

    public static string NotANumber(string arg, string value) => P($"{arg} 要是整數，收到 {value}", $"{arg} must be an integer, got {value}");

    public static string UnknownArg(string arg) => P($"未知參數 {arg}，用 --help 看說明", $"Unknown argument {arg}, see --help");

    public static string BadLang(string value) => P($"--lang 只接受 zh 或 en，收到 {value}", $"--lang accepts zh or en, got {value}");

    public static string BadMode(string value) => P($"--mode 只接受 a / b / c / d / all，收到 {value}", $"--mode accepts a / b / c / d / all, got {value}");

    public static string BadTrigger(string value) => P($"--trigger 只接受 plc 或 host，收到 {value}", $"--trigger accepts plc or host, got {value}");

    public static string OutOfRange(string arg, string range) => P($"{arg} 超出範圍，要 {range}", $"{arg} out of range, must be {range}");

    public static string Help => P("""
        AdsReadDemo — 同一批 PLC 變數，四種 ADS 讀法並排比較

          --mode <a|b|c|d|all>        預設 all (依序跑 A → B → C → D，最後印對照表)
                                        a 普通寫法：迴圈逐一讀 MAIN.aValues[i]
                                        b Sum Read：一次請求讀回一批 (SumHandleReadAnyType)
                                        c 輪詢：每 --poll-ms 讀一次 MAIN.stTrigger
                                        d Notification：AddDeviceNotification (OnChange)
          --netid <AmsNetId>          預設 AmsNetId.Local (127.0.0.1.1.1)；UmRT 有自己的 AmsNetId 時要指定
          --port <n>                  預設 851
          --count <n>                 A / B 讀幾個元素，1..1000，預設 1000
          --repeat <n>                A / B 各跑幾輪，預設 5
          --sum-chunk <n>             一個 Sum command 帶幾個子命令，預設 500 (InfoSys 建議上限)
          --seconds <s>               C / D 各跑幾秒，預設 10
          --poll-ms <ms>              C 的輪詢間隔，預設 10
          --notify-cycle-ms <ms>      D 的 NotificationSettings.CycleTime，預設 10
          --notify-max-delay-ms <ms>  D 的 NotificationSettings.MaxDelay，預設 0
          --trigger <plc|host>        誰讓 nTrigger +1，預設 plc
                                        plc  PLC 每 tTriggerPeriod (預設 2 s) 自動 +1；延遲用 PLC 時間戳算
                                        host C# 每 --host-trigger-ms 寫 MAIN.nHostRequest；延遲只用 C# 端時鐘算
          --host-trigger-ms <ms>      預設 2000
          --max-clock-drift-ms <ms>   plc 觸發時 PLC 與 Windows 時鐘差的容許值，預設 20
          --lang <zh|en>              輸出語言，預設 zh
        """, """
        AdsReadDemo — four ways to read the same PLC variables over ADS, side by side

          --mode <a|b|c|d|all>        default all (runs A → B → C → D, then prints a comparison table)
                                        a plain: loop and read MAIN.aValues[i] one by one
                                        b Sum Read: read a batch in one request (SumHandleReadAnyType)
                                        c polling: read MAIN.stTrigger every --poll-ms
                                        d notification: AddDeviceNotification (OnChange)
          --netid <AmsNetId>          default AmsNetId.Local (127.0.0.1.1.1); pass the UmRT AmsNetId if it has its own
          --port <n>                  default 851
          --count <n>                 elements read by A / B, 1..1000, default 1000
          --repeat <n>                passes of A / B, default 5
          --sum-chunk <n>             sub-commands per Sum command, default 500 (InfoSys recommendation)
          --seconds <s>               run time of C / D, default 10
          --poll-ms <ms>              polling interval of C, default 10
          --notify-cycle-ms <ms>      NotificationSettings.CycleTime of D, default 10
          --notify-max-delay-ms <ms>  NotificationSettings.MaxDelay of D, default 0
          --trigger <plc|host>        who increments nTrigger, default plc
                                        plc  the PLC every tTriggerPeriod (default 2 s); latency from the PLC timestamp
                                        host C# writes MAIN.nHostRequest every --host-trigger-ms; latency from the C# clock only
          --host-trigger-ms <ms>      default 2000
          --max-clock-drift-ms <ms>   allowed PLC vs Windows clock difference with --trigger plc, default 20
          --lang <zh|en>              output language, default zh
        """);

    // ---------- 連線 ----------

    public static string Connected(object netId, int port, object adsState) =>
        P($"連線 {netId}:{port}，ADS state = {adsState}", $"Connected to {netId}:{port}, ADS state = {adsState}");

    public static string NotRunning(object adsState) => P(
        $"PLC 不在 Run (ADS state = {adsState})，值不會變，結果沒有意義",
        $"PLC is not in Run (ADS state = {adsState}); values will not change and the results are meaningless");

    public static string AdsError(object code, string message) => P($"ADS 錯誤：{code} {message}", $"ADS error: {code} {message}");

    public static string AdsErrorHint => P(
        "連不上：確認 PLC 已 Activate + Run；UmRT 要用 --netid 指定它的 AmsNetId (否則常見 ADS error 6 / 7)",
        "Cannot connect: make sure the PLC is activated and running; for UmRT pass its AmsNetId with --netid (otherwise ADS error 6 / 7 is common)");

    public static string PlcInfo(int periodMs, uint cycleCount) => P(
        $"PLC：tTriggerPeriod = {periodMs} ms，nCycleCount = {cycleCount}",
        $"PLC: tTriggerPeriod = {periodMs} ms, nCycleCount = {cycleCount}");

    // ---------- 模式名稱與標題 ----------

    public static string ModeAName => P("A 普通寫法 (逐一讀)", "A plain (one by one)");

    public static string ModeBName => P("B Sum Read", "B Sum Read");

    public static string ModeCName => P("C 輪詢", "C polling");

    public static string ModeDName => P("D Notification", "D notification");

    public static string ModeATitle(int count) => P(
        $"== {ModeAName}：迴圈 {count} 次，每次 ReadAny 一個 MAIN.aValues[i]",
        $"== {ModeAName}: loop {count} times, ReadAny one MAIN.aValues[i] each");

    public static string ModeBTitle(int count, int chunk) => P(
        $"== {ModeBName}：{count} 個打包成 Sum command (每包最多 {chunk} 個)",
        $"== {ModeBName}: {count} values packed into Sum commands (up to {chunk} per command)");

    public static string ModeCTitle(int pollMs, int seconds) => P(
        $"== {ModeCName}：每 {pollMs} ms 讀一次 MAIN.stTrigger，跑 {seconds} s",
        $"== {ModeCName}: read MAIN.stTrigger every {pollMs} ms for {seconds} s");

    public static string ModeDTitle(int cycleMs, int maxDelayMs, int seconds) => P(
        $"== {ModeDName}：OnChange，CycleTime {cycleMs} ms，MaxDelay {maxDelayMs} ms，跑 {seconds} s",
        $"== {ModeDName}: OnChange, CycleTime {cycleMs} ms, MaxDelay {maxDelayMs} ms, for {seconds} s");

    public static string TriggerInfo(TriggerSource source, int hostMs) => source == TriggerSource.Host
        ? P($"   觸發：C# 每 {hostMs} ms 寫 MAIN.nHostRequest (延遲只用 C# 時鐘算，含一次 ADS 寫入)",
            $"   Trigger: C# writes MAIN.nHostRequest every {hostMs} ms (latency from the C# clock only, includes one ADS write)")
        : P("   觸發：PLC 自己每 tTriggerPeriod +1 (延遲 = Windows 時間 − PLC 時間戳)",
            "   Trigger: the PLC increments every tTriggerPeriod (latency = Windows time − PLC timestamp)");

    // ---------- 執行中 ----------

    public static string PassLine(int pass, double ms, int requests) => P(
        $"   第 {pass} 輪：{ms,9:F2} ms，ADS 請求 {requests} 次",
        $"   pass {pass}: {ms,9:F2} ms, {requests} ADS requests");

    public static string SampleValues(double first, double last) => P(
        $"   aValues[1] = {first:F4}，aValues[最後] = {last:F4}",
        $"   aValues[1] = {first:F4}, aValues[last] = {last:F4}");

    public static string SumNotExecutedAsSum => P(
        "Sum command 沒有以單一請求執行 (目標不支援？)",
        "The Sum command was not executed as a single request (target does not support it?)");

    public static string SumSubError(int index, object error) => P(
        $"Sum command 第 {index + 1} 個子命令失敗：{error}",
        $"Sum command sub-command {index + 1} failed: {error}");

    public static string ProgressLine(string name, int second, int requests, int notifications, int changes, double? lastLatencyMs) => P(
        $"   [{second,3} s] ADS 請求 {requests,6}  通知 {notifications,4}  看到變化 {changes,3}  最近延遲 {FormatMs(lastLatencyMs)}",
        $"   [{second,3} s] ADS requests {requests,6}  notifications {notifications,4}  changes seen {changes,3}  last latency {FormatMs(lastLatencyMs)}");

    public static string ClockOffset(double beginMs, double endMs) => P(
        $"   Windows − PLC 時鐘差：開始 {beginMs:F2} ms，結束 {endMs:F2} ms (時鐘一致時應在 0 ~ 1 個 PLC cycle)",
        $"   Windows − PLC clock offset: begin {beginMs:F2} ms, end {endMs:F2} ms (0 to 1 PLC cycle when the clocks agree)");

    public static string ClockNotTrusted(double limitMs) => P(
        $"PLC 與 Windows 時鐘差超出 -1 ~ {limitMs} ms，延遲不可信；改用 --trigger host",
        $"PLC and Windows clocks differ by more than -1 to {limitMs} ms, latency not trustworthy; use --trigger host");

    public static string NoChangeSeen => P(
        "沒看到變化 (PLC 沒在跑？跑的秒數比 tTriggerPeriod 短？)",
        "No change seen (PLC not running? run time shorter than tTriggerPeriod?)");

    // ---------- 對照表 ----------

    public static string BulkTableTitle(int count) => P($"讀 {count} 個 LREAL", $"Reading {count} LREAL values");

    public static string ChangeTableTitle(int seconds) => P($"盯一個會變的變數 {seconds} s", $"Watching one changing variable for {seconds} s");

    public static string[] BulkHeaders => Lang == Lang.En
        ? new[] { "Mode", "Avg ms", "Min ms", "ADS requests" }
        : new[] { "模式", "平均 ms", "最快 ms", "ADS 請求" };

    public static string[] ChangeHeaders => Lang == Lang.En
        ? new[] { "Mode", "Requests", "Req/s", "Notifications", "Changes", "Avg latency", "Max latency", "CPU %" }
        : new[] { "模式", "ADS 請求", "請求/s", "PLC 通知", "看到變化", "平均延遲", "最大延遲", "CPU %" };

    public static string Speedup(double factor, int requestsA, int requestsB) => P(
        $"Sum Read 比逐一讀快 {factor:F0} 倍，ADS 請求 {requestsA} → {requestsB}",
        $"Sum Read is {factor:F0}x faster than one-by-one, ADS requests {requestsA} → {requestsB}");

    public static string ChangeNotes => P(
        "ADS 請求 = C# 送給 PLC 的請求 (D 只有 Add + Delete 兩次)；PLC 通知 = PLC 主動送來的訊息",
        "ADS requests = requests C# sent to the PLC (D only sends Add + Delete); notifications = messages the PLC pushed");

    public static string Note(string name, string note) => P($"註：{name}：{note}", $"Note: {name}: {note}");

    public static string MissedNote(string name, int missed) => P(
        $"註：{name} 漏看 {missed} 次變化 (兩次讀取之間變了不只一次)",
        $"Note: {name} missed {missed} changes (changed more than once between two reads)");

    public static string FormatMs(double? ms) => ms is double v ? $"{v,7:F2} ms" : "      - ";
}
