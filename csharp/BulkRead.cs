using System.Diagnostics;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;

namespace AdsReadDemo;

/// <summary>模式 A / B 一輪的結果。</summary>
internal sealed record BulkResult(
    string Name,
    int Count,
    int Repeat,
    double AvgMs,
    double MinMs,
    int RequestsPerPass,
    double FirstValue,
    double LastValue);

/// <summary>
/// 模式 A (逐一讀) 與模式 B (Sum Read) 共用同一組 variable handle，差別只在讀法：
/// A 對每個 handle 送一次 ADS Read；B 把 handle 打包進 Sum command，一次請求讀回一批。
/// 建立 / 釋放 handle 不算進計時。
/// </summary>
internal sealed class BulkRead : IDisposable
{
    private readonly AdsClient _client;
    private readonly Options _opt;
    private readonly uint[] _handles;

    public BulkRead(AdsClient client, Options opt)
    {
        _client = client;
        _opt = opt;
        var names = Enumerable.Range(1, opt.Count).Select(PlcSymbols.Value).ToArray();
        var handles = new List<uint>(names.Length);
        foreach (var chunk in names.Chunk(opt.SumChunk))
        {
            var created = new SumCreateHandles(client, chunk).CreateHandles();
            ThrowOnSubError(created);
            handles.AddRange(created.Handles ?? throw new InvalidOperationException(Text.SumSubError(0, created.ErrorCode)));
        }
        _handles = handles.ToArray();
    }

    /// <summary>模式 A：迴圈 N 次，一次讀一個 aValues[i]。</summary>
    public BulkResult RunOneByOne()
    {
        var values = new double[_handles.Length];
        var times = new List<double>(_opt.Repeat);
        int requests = 0;
        for (int r = 0; r < _opt.Repeat; r++)
        {
            requests = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < _handles.Length; i++)
            {
                values[i] = (double)_client.ReadAny(_handles[i], typeof(double));
                requests++;
            }
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            Console.WriteLine(Text.PassLine(r + 1, sw.Elapsed.TotalMilliseconds, requests));
        }
        return new BulkResult(Text.ModeAName, _handles.Length, _opt.Repeat, times.Average(), times.Min(), requests, values[0], values[^1]);
    }

    /// <summary>模式 B：Sum command，每 SumChunk 個子命令一個 ADS 請求。</summary>
    public BulkResult RunSum()
    {
        var values = new double[_handles.Length];
        var times = new List<double>(_opt.Repeat);
        int requests = 0;
        for (int r = 0; r < _opt.Repeat; r++)
        {
            requests = 0;
            int offset = 0;
            var sw = Stopwatch.StartNew();
            foreach (var chunk in _handles.Chunk(_opt.SumChunk))
            {
                var types = Enumerable.Repeat(typeof(double), chunk.Length).ToArray();
                // SumFallbackMode.Sum：目標不支援 Sum command 時直接丟例外，不偷偷退回逐一讀
                var cmd = new SumHandleReadAnyType(_client, chunk, types, SumFallbackMode.Sum);
                var read = cmd.Read();
                requests++;
                ThrowOnSubError(read);
                object[] result = read.Values ?? throw new InvalidOperationException(Text.SumSubError(0, read.ErrorCode));
                if (!cmd.ExecutedAsSum) throw new InvalidOperationException(Text.SumNotExecutedAsSum);
                for (int i = 0; i < result.Length; i++) values[offset + i] = (double)result[i]!;
                offset += result.Length;
            }
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            Console.WriteLine(Text.PassLine(r + 1, sw.Elapsed.TotalMilliseconds, requests));
        }
        return new BulkResult(Text.ModeBName, _handles.Length, _opt.Repeat, times.Average(), times.Min(), requests, values[0], values[^1]);
    }

    public void Dispose()
    {
        foreach (var chunk in _handles.Chunk(_opt.SumChunk))
        {
            new SumReleaseHandles(_client, chunk).ReleaseHandles();
        }
    }

    /// <summary>Sum command 整體成功但個別子命令可能失敗 (例如 symbol 不存在)，逐一檢查。</summary>
    private static void ThrowOnSubError(ResultSumCommand result)
    {
        result.ThrowOnError();
        var failed = result.SubErrors?.Select((e, i) => (e, i)).FirstOrDefault(x => x.e != AdsErrorCode.NoError);
        if (failed is { } f && f.e != AdsErrorCode.NoError)
        {
            throw new InvalidOperationException(Text.SumSubError(f.i, f.e));
        }
    }
}
