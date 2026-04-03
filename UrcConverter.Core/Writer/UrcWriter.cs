using System.Globalization;
using System.Text;
using UrcConverter.Core.Models;
using UrcConverter.Core.Models.Enums;

namespace UrcConverter.Core.Writer;

/// <summary>
/// Serializes a <see cref="UrcChart"/> to the URC text format.
/// </summary>
public static class UrcWriter
{
    #region Public Methods

    /// <summary>
    /// Write a URC chart to a file.
    /// </summary>
    public static void WriteToFile(UrcChart chart, string filePath)
    {
        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        WriteTo(chart, writer);
    }

    /// <summary>
    /// Write a URC chart to a <see cref="TextWriter"/>.
    /// </summary>
    public static void WriteTo(UrcChart chart, TextWriter writer)
    {
        writer.WriteLine($"@URC {chart.FormatVersion}");
        writer.WriteLine();

        WriteMetadata(chart.Metadata, writer);
        WriteJudgment(chart.Judgment, writer);
        WriteLayout(chart.Layout, writer);
        WriteTiming(chart.Timings, writer);
        WriteNotes(chart.Notes, writer);
    }

    /// <summary>
    /// Serialize a URC chart to a string.
    /// </summary>
    public static string WriteToString(UrcChart chart)
    {
        using var sw = new StringWriter();
        WriteTo(chart, sw);
        return sw.ToString();
    }

    #endregion

    #region Private Methods

    private static void WriteMetadata(UrcMetadata m, TextWriter w)
    {
        w.WriteLine("@Metadata");
        w.WriteLine($"Original: {m.Original}");
        w.WriteLine($"Title: {m.Title}");
        w.WriteLine($"Artist: {m.Artist}");
        w.WriteLine($"Creator: {m.Creator}");
        w.WriteLine($"Version: {m.Version}");
        w.WriteLine();
    }

    private static void WriteJudgment(UrcJudgment? j, TextWriter w)
    {
        if (j is null)
            return;

        w.WriteLine("@Judgment");
        w.WriteLine($"Window: {FormatDoubleList(j.Windows)}");
        w.WriteLine($"Rate: {FormatDoubleList(j.Rates)}");
        w.WriteLine();
    }

    private static void WriteLayout(UrcLayout layout, TextWriter w)
    {
        w.WriteLine("@Layout");

        var type = layout.SpecialKeyCount > 0
            ? $"{layout.KeyCount}+{layout.SpecialKeyCount}"
            : layout.KeyCount.ToString();
        w.WriteLine($"Type: {type}");

        w.WriteLine(layout.SpecialLanes.Count > 0
            ? $"Special: {string.Join(", ", layout.SpecialLanes)}"
            : "Special: None");
        w.WriteLine();
    }

    private static void WriteTiming(IReadOnlyList<UrcTiming> timings, TextWriter w)
    {
        w.WriteLine("@Timing");

        foreach (var t in timings)
            w.WriteLine($"{t.Timestamp}, {FormatDouble(t.Bpm)}, {t.Meter}" + (t.Multiplier is 1.0 ? "" : $", {FormatDouble(t.Multiplier)}"));

        w.WriteLine();
    }

    private static void WriteNotes(IReadOnlyList<UrcNote> notes, TextWriter w)
    {
        w.WriteLine("@Notes");

        foreach (var n in notes)
        {
            var type = n.Type switch
            {
                NoteType.Normal => "N",
                NoteType.LongStart => "LS",
                NoteType.LongEnd => "LE",
                NoteType.Mine => "M",
                NoteType.Fake => "F",
                _ => "N"
            };

            w.WriteLine($"{n.Timestamp}, {n.Lane}, {type}");
        }
    }

    private static string FormatDouble(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string FormatDoubleList(IReadOnlyList<double> values) => string.Join(", ", values.Select(FormatDouble));

    #endregion
}