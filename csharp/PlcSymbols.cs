using System.Buffers.Binary;

namespace AdsReadDemo;

/// <summary>PLC 端 symbol 路徑與資料格式，與 plc/ 裡的 MAIN、ST_Trigger 一致。</summary>
internal static class PlcSymbols
{
    /// <summary>ARRAY[1..1000] OF LREAL，下界 1。</summary>
    public const string ValueElement = "MAIN.aValues[{0}]";

    /// <summary>ST_Trigger (pack_mode 1，12 bytes)。</summary>
    public const string Trigger = "MAIN.stTrigger";

    public const int TriggerSize = 12;

    /// <summary>每 cycle 的 F_GetSystemTime() (ULINT, 100 ns since 1601-01-01 UTC)。</summary>
    public const string PlcTimeNow = "MAIN.nPlcTimeNow";

    public const string AutoTrigger = "MAIN.bAutoTrigger";

    public const string HostRequest = "MAIN.nHostRequest";

    public const string TriggerPeriod = "MAIN.tTriggerPeriod";

    public const string CycleCount = "MAIN.nCycleCount";

    public static string Value(int index) => string.Format(ValueElement, index);

    /// <summary>解析 ST_Trigger：nTrigger UDINT @0、nPlcTime ULINT @4 (little endian)。</summary>
    public static (uint Count, long PlcTime) ParseTrigger(ReadOnlySpan<byte> data) =>
        (BinaryPrimitives.ReadUInt32LittleEndian(data), (long)BinaryPrimitives.ReadUInt64LittleEndian(data[4..]));
}
