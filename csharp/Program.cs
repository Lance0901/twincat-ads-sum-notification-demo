using TwinCAT.Ads;
using AdsReadDemo;

// 流程：連線 → A 逐一讀 / B Sum Read (讀 1000 個 LREAL) → C 輪詢 / D Notification (盯 MAIN.stTrigger) → 對照表
// 數字只代表跑的那台機器；UmRT 不是 real-time，延遲與 cycle 會比實機大。

Text.UseUtf8Console();

Options opt;
try
{
    opt = Options.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

using var client = new AdsClient();
try
{
    client.Connect(opt.AmsNetId, opt.Port);
    var state = client.ReadState();
    Console.WriteLine(Text.Connected(opt.AmsNetId, opt.Port, state.AdsState));
    if (state.AdsState != AdsState.Run)
    {
        Console.WriteLine(Text.NotRunning(state.AdsState));
    }
    PrintPlcInfo(client);
    return Run(client, opt);
}
catch (AdsErrorException ex)
{
    Console.Error.WriteLine(Text.AdsError(ex.ErrorCode, ex.Message));
    Console.Error.WriteLine(Text.AdsErrorHint);
    return 1;
}

static int Run(AdsClient client, Options opt)
{
    bool all = opt.Mode == Mode.All;
    var bulk = new List<BulkResult>();
    var change = new List<ChangeResult>();

    if (all || opt.Mode is Mode.A or Mode.B)
    {
        using var reader = new BulkRead(client, opt);
        if (all || opt.Mode == Mode.A)
        {
            Console.WriteLine();
            Console.WriteLine(Text.ModeATitle(opt.Count));
            bulk.Add(Sample(reader.RunOneByOne()));
        }
        if (all || opt.Mode == Mode.B)
        {
            Console.WriteLine();
            Console.WriteLine(Text.ModeBTitle(opt.Count, opt.SumChunk));
            bulk.Add(Sample(reader.RunSum()));
        }
    }

    var watch = new ChangeWatch(client, opt);
    if (all || opt.Mode == Mode.C)
    {
        Console.WriteLine();
        Console.WriteLine(Text.ModeCTitle(opt.PollMs, opt.Seconds));
        Console.WriteLine(Text.TriggerInfo(opt.Trigger, opt.HostTriggerMs));
        change.Add(watch.RunPolling());
    }
    if (all || opt.Mode == Mode.D)
    {
        Console.WriteLine();
        Console.WriteLine(Text.ModeDTitle(opt.NotifyCycleMs, opt.NotifyMaxDelayMs, opt.Seconds));
        Console.WriteLine(Text.TriggerInfo(opt.Trigger, opt.HostTriggerMs));
        change.Add(watch.RunNotification());
    }

    Report.PrintBulk(bulk);
    Report.PrintChange(change, opt.Seconds);
    return 0;
}

static BulkResult Sample(BulkResult r)
{
    Console.WriteLine(Text.SampleValues(r.FirstValue, r.LastValue));
    return r;
}

static void PrintPlcInfo(AdsClient client)
{
    uint periodHandle = client.CreateVariableHandle(PlcSymbols.TriggerPeriod);
    uint cycleHandle = client.CreateVariableHandle(PlcSymbols.CycleCount);
    try
    {
        uint periodMs = (uint)client.ReadAny(periodHandle, typeof(uint)); // TIME = UDINT ms
        uint cycles = (uint)client.ReadAny(cycleHandle, typeof(uint));
        Console.WriteLine(Text.PlcInfo((int)periodMs, cycles));
    }
    finally
    {
        client.DeleteVariableHandle(periodHandle);
        client.DeleteVariableHandle(cycleHandle);
    }
}
