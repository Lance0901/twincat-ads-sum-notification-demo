using System.Globalization;
using System.Text;

namespace AdsReadDemo;

/// <summary>終端機對照表。中文字佔兩格，用顯示寬度對齊。</summary>
internal static class Report
{
    public static void PrintBulk(IReadOnlyList<BulkResult> rows)
    {
        if (rows.Count == 0) return;
        Console.WriteLine();
        Console.WriteLine(Text.BulkTableTitle(rows[0].Count));
        var cells = rows.Select(r => new[]
        {
            r.Name,
            r.AvgMs.ToString("F2", CultureInfo.InvariantCulture),
            r.MinMs.ToString("F2", CultureInfo.InvariantCulture),
            r.RequestsPerPass.ToString(CultureInfo.InvariantCulture),
        }).ToList();
        PrintTable(Text.BulkHeaders, cells);

        var a = rows.FirstOrDefault(r => r.Name == Text.ModeAName);
        var b = rows.FirstOrDefault(r => r.Name == Text.ModeBName);
        if (a is not null && b is not null && b.AvgMs > 0)
        {
            Console.WriteLine(Text.Speedup(a.AvgMs / b.AvgMs, a.RequestsPerPass, b.RequestsPerPass));
        }
    }

    public static void PrintChange(IReadOnlyList<ChangeResult> rows, int seconds)
    {
        if (rows.Count == 0) return;
        Console.WriteLine();
        Console.WriteLine(Text.ChangeTableTitle(seconds));
        var cells = rows.Select(r => new[]
        {
            r.Name,
            r.Requests.ToString(CultureInfo.InvariantCulture),
            r.RequestsPerSec.ToString("F1", CultureInfo.InvariantCulture),
            r.Notifications.ToString(CultureInfo.InvariantCulture),
            r.Changes.ToString(CultureInfo.InvariantCulture),
            Text.FormatMs(r.AvgLatencyMs).Trim(),
            Text.FormatMs(r.MaxLatencyMs).Trim(),
            r.CpuPercent.ToString("F2", CultureInfo.InvariantCulture),
        }).ToList();
        PrintTable(Text.ChangeHeaders, cells);
        Console.WriteLine(Text.ChangeNotes);
        foreach (var r in rows)
        {
            if (r.LatencyNote is not null) Console.WriteLine(Text.Note(r.Name, r.LatencyNote));
            if (r.Missed > 0) Console.WriteLine(Text.MissedNote(r.Name, r.Missed));
        }
    }

    private static void PrintTable(string[] headers, List<string[]> rows)
    {
        int cols = headers.Length;
        var widths = new int[cols];
        for (int c = 0; c < cols; c++)
        {
            widths[c] = Math.Max(Width(headers[c]), rows.Max(r => Width(r[c])));
        }
        string Line(char fill) => "+" + string.Join("+", widths.Select(w => new string(fill, w + 2))) + "+";
        string Row(string[] cells) => "|" + string.Join("|", cells.Select((s, c) => " " + Pad(s, widths[c], c > 0) + " ")) + "|";

        Console.WriteLine(Line('-'));
        Console.WriteLine(Row(headers));
        Console.WriteLine(Line('='));
        foreach (var r in rows) Console.WriteLine(Row(r));
        Console.WriteLine(Line('-'));
    }

    private static string Pad(string s, int width, bool right)
    {
        string space = new(' ', Math.Max(0, width - Width(s)));
        return right ? space + s : s + space;
    }

    private static int Width(string s)
    {
        int w = 0;
        foreach (Rune r in s.EnumerateRunes())
        {
            w += IsWide(r.Value) ? 2 : 1;
        }
        return w;
    }

    // CJK、全形標點：終端機佔兩格
    private static bool IsWide(int cp) =>
        cp is >= 0x1100 and <= 0x115F
        or >= 0x2E80 and <= 0xA4CF
        or >= 0xAC00 and <= 0xD7A3
        or >= 0xF900 and <= 0xFAFF
        or >= 0xFE30 and <= 0xFE4F
        or >= 0xFF00 and <= 0xFF60
        or >= 0xFFE0 and <= 0xFFE6;
}
