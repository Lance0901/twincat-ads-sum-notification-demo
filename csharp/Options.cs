using TwinCAT.Ads;

namespace AdsReadDemo;

/// <summary>要跑哪些模式。</summary>
internal enum Mode
{
    A,
    B,
    C,
    D,
    All,
}

/// <summary>模式 C / D 的值變化由誰觸發。</summary>
internal enum TriggerSource
{
    /// <summary>PLC 每 tTriggerPeriod 自動 +1，延遲用 PLC 時間戳算 (需要 PLC 與 Windows 時鐘一致)。</summary>
    Plc,

    /// <summary>C# 寫 MAIN.nHostRequest，PLC 下一個 cycle +1；延遲只用 C# 端 Stopwatch 算，不依賴 PLC 時鐘。</summary>
    Host,
}

/// <summary>
/// 命令列參數。目標預設是本機 (AmsNetId.Local = 127.0.0.1.1.1) 的 PLC port 851；
/// UmRT 有自己的 AmsNetId 時用 --netid 指定。所有預設值只寫在這個類別。
/// </summary>
internal sealed class Options
{
    /// <summary>目標 AmsNetId；null = AmsNetId.Local。</summary>
    public string? NetId { get; private set; }

    public int Port { get; private set; } = 851;

    public Mode Mode { get; private set; } = Mode.All;

    /// <summary>模式 A / B 讀幾個元素 (1..PLC 陣列長度 1000)。</summary>
    public int Count { get; private set; } = 1000;

    /// <summary>模式 A / B 各跑幾輪 (取平均 / 最小)。</summary>
    public int Repeat { get; private set; } = 5;

    /// <summary>一個 Sum command 最多帶幾個子命令；InfoSys 建議不超過 500。</summary>
    public int SumChunk { get; private set; } = 500;

    /// <summary>模式 C / D 各跑幾秒。</summary>
    public int Seconds { get; private set; } = 10;

    /// <summary>模式 C 輪詢間隔 (ms)。</summary>
    public int PollMs { get; private set; } = 10;

    /// <summary>模式 D：ADS server 檢查變數是否改變的週期 (ms)。</summary>
    public int NotifyCycleMs { get; private set; } = 10;

    /// <summary>模式 D：通知最晚延遲 (ms)；0 = 不累積，變了就送。</summary>
    public int NotifyMaxDelayMs { get; private set; }

    public TriggerSource Trigger { get; private set; } = TriggerSource.Plc;

    /// <summary>--trigger host 時 C# 觸發的間隔 (ms)。</summary>
    public int HostTriggerMs { get; private set; } = 2000;

    /// <summary>PLC 與 Windows 時鐘差在一個模式內漂移超過這個值 (ms) 就標示延遲不可信。</summary>
    public double MaxClockDriftMs { get; private set; } = 20.0;

    public Lang Lang { get; private set; } = Lang.Zh;

    public AmsNetId AmsNetId => NetId is null ? AmsNetId.Local : new AmsNetId(NetId);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        // 先抓 --lang，讓後面的錯誤訊息與 --help 不管參數順序都用指定語言
        int langAt = Array.LastIndexOf(args, "--lang");
        if (langAt >= 0 && langAt + 1 < args.Length && TryParseLang(args[langAt + 1], out var early)) Text.Lang = early;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(Text.MissingValue(args[i]));
            int NextInt() => int.TryParse(Next(), out int v) ? v : throw new ArgumentException(Text.NotANumber(args[i - 1], args[i]));
            switch (args[i])
            {
                case "--netid": o.NetId = Next(); break;
                case "--port": o.Port = NextInt(); break;
                case "--mode":
                    string m = Next();
                    o.Mode = m.ToLowerInvariant() switch
                    {
                        "a" => Mode.A,
                        "b" => Mode.B,
                        "c" => Mode.C,
                        "d" => Mode.D,
                        "all" => Mode.All,
                        _ => throw new ArgumentException(Text.BadMode(m)),
                    };
                    break;
                case "--count": o.Count = NextInt(); break;
                case "--repeat": o.Repeat = NextInt(); break;
                case "--sum-chunk": o.SumChunk = NextInt(); break;
                case "--seconds": o.Seconds = NextInt(); break;
                case "--poll-ms": o.PollMs = NextInt(); break;
                case "--notify-cycle-ms": o.NotifyCycleMs = NextInt(); break;
                case "--notify-max-delay-ms": o.NotifyMaxDelayMs = NextInt(); break;
                case "--trigger":
                    string t = Next();
                    o.Trigger = t.ToLowerInvariant() switch
                    {
                        "plc" => TriggerSource.Plc,
                        "host" => TriggerSource.Host,
                        _ => throw new ArgumentException(Text.BadTrigger(t)),
                    };
                    break;
                case "--host-trigger-ms": o.HostTriggerMs = NextInt(); break;
                case "--max-clock-drift-ms": o.MaxClockDriftMs = NextInt(); break;
                case "--lang":
                    string v = Next();
                    o.Lang = TryParseLang(v, out var lang) ? lang : throw new ArgumentException(Text.BadLang(v));
                    Text.Lang = lang;
                    break;
                case "-h":
                case "--help":
                    Console.WriteLine(Text.Help);
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException(Text.UnknownArg(args[i]));
            }
        }
        Require(o.Count is >= 1 and <= 1000, "--count", "1..1000");
        Require(o.Repeat >= 1, "--repeat", ">= 1");
        Require(o.SumChunk >= 1, "--sum-chunk", ">= 1");
        Require(o.Seconds >= 1, "--seconds", ">= 1");
        Require(o.PollMs >= 1, "--poll-ms", ">= 1");
        Require(o.NotifyCycleMs >= 0, "--notify-cycle-ms", ">= 0");
        Require(o.NotifyMaxDelayMs >= 0, "--notify-max-delay-ms", ">= 0");
        Require(o.HostTriggerMs >= 100, "--host-trigger-ms", ">= 100");
        return o;
    }

    private static void Require(bool ok, string arg, string range)
    {
        if (!ok) throw new ArgumentException(Text.OutOfRange(arg, range));
    }

    private static bool TryParseLang(string value, out Lang lang)
    {
        switch (value.ToLowerInvariant())
        {
            case "zh": lang = Lang.Zh; return true;
            case "en": lang = Lang.En; return true;
            default: lang = Lang.Zh; return false;
        }
    }
}
