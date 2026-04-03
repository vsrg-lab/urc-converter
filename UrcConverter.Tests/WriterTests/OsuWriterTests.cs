using FluentAssertions;
using Xunit;
using UrcConverter.Core;
using UrcConverter.Core.Models;
using UrcConverter.Core.Models.Enums;
using UrcConverter.Core.Writer;

namespace UrcConverter.Tests.WriterTests;

public class UrcWriterTests
{
    #region Header

    [Fact]
    public void WriteToString_StartsWithUrcVersionHeader()
    {
        var chart = MakeChart();
        var output = UrcWriter.WriteToString(chart);

        output.Should().StartWith($"@URC {UrcFormat.Version}");
    }

    #endregion

    #region Metadata

    [Fact]
    public void WriteToString_ContainsAllMetadataFields()
    {
        var chart = MakeChart();
        var output = UrcWriter.WriteToString(chart);

        output.Should().Contain("@Metadata");
        output.Should().Contain("Original: osu!mania");
        output.Should().Contain("Title: Test");
        output.Should().Contain("Artist: Artist");
        output.Should().Contain("Creator: Mapper");
        output.Should().Contain("Version: Hard");
    }

    #endregion

    #region Timing Multiplier

    [Fact]
    public void WriteToString_OmitsMultiplierWhenOne()
    {
        var chart = MakeChart(timings: [new UrcTiming(0, 180.0, "4/4", 1.0)]);
        var output = UrcWriter.WriteToString(chart);
        var timingLine = GetLineContaining(output, "0, 180");

        // Should be "0, 180, 4/4" with no 4th field
        timingLine.Should().Be("0, 180, 4/4");
    }

    [Fact]
    public void WriteToString_IncludesMultiplierWhenNotOne()
    {
        var chart = MakeChart(timings:
        [
            new UrcTiming(0, 180.0, "4/4", 1.0),
            new UrcTiming(5000, 180.0, "4/4", 0.8)
        ]);

        var output = UrcWriter.WriteToString(chart);
        var svLine = GetLineContaining(output, "5000, 180");

        svLine.Should().Be("5000, 180, 4/4, 0.8");
    }

    #endregion

    #region Judgment

    [Fact]
    public void WriteToString_OmitsJudgmentSectionWhenNull()
    {
        var chart = MakeChart(judgment: null);
        var output = UrcWriter.WriteToString(chart);

        output.Should().NotContain("@Judgment");
    }

    [Fact]
    public void WriteToString_IncludesJudgmentSectionWhenPresent()
    {
        var judgment = new UrcJudgment([16.5, 43.5], [100.0, 100.0]);
        var chart = MakeChart(judgment: judgment);
        var output = UrcWriter.WriteToString(chart);

        output.Should().Contain("@Judgment");
        output.Should().Contain("Window: 16.5, 43.5");
        output.Should().Contain("Rate: 100, 100");
    }

    #endregion

    #region Layout

    [Fact]
    public void WriteToString_WritesStandardLayout()
    {
        var chart = MakeChart(layout: new UrcLayout(7, 0, []));
        var output = UrcWriter.WriteToString(chart);

        output.Should().Contain("Type: 7");
        output.Should().Contain("Special: None");
    }

    [Fact]
    public void WriteToString_WritesSpecialLayout()
    {
        var chart = MakeChart(layout: new UrcLayout(7, 1, [0]));
        var output = UrcWriter.WriteToString(chart);

        output.Should().Contain("Type: 7+1");
        output.Should().Contain("Special: 0");
    }

    #endregion

    #region Notes

    [Fact]
    public void WriteToString_WritesNoteTypes()
    {
        var chart = MakeChart(notes:
        [
            new UrcNote(100, 0, NoteType.Normal),
            new UrcNote(200, 1, NoteType.LongStart),
            new UrcNote(300, 1, NoteType.LongEnd),
            new UrcNote(400, 2, NoteType.Mine),
            new UrcNote(500, 3, NoteType.Fake)
        ]);

        var output = UrcWriter.WriteToString(chart);

        output.Should().Contain("100, 0, N");
        output.Should().Contain("200, 1, LS");
        output.Should().Contain("300, 1, LE");
        output.Should().Contain("400, 2, M");
        output.Should().Contain("500, 3, F");
    }

    #endregion

    #region Section Order

    [Fact]
    public void WriteToString_SectionsAreInSpecOrder()
    {
        var judgment = new UrcJudgment([16.5], [100.0]);
        var chart = MakeChart(judgment: judgment);
        var output = UrcWriter.WriteToString(chart);

        var metaIdx = output.IndexOf("@Metadata", StringComparison.InvariantCulture);
        var judgeIdx = output.IndexOf("@Judgment", StringComparison.InvariantCulture);
        var layoutIdx = output.IndexOf("@Layout", StringComparison.InvariantCulture);
        var timingIdx = output.IndexOf("@Timing", StringComparison.InvariantCulture);
        var notesIdx = output.IndexOf("@Notes", StringComparison.InvariantCulture);

        metaIdx.Should().BeLessThan(judgeIdx);
        judgeIdx.Should().BeLessThan(layoutIdx);
        layoutIdx.Should().BeLessThan(timingIdx);
        timingIdx.Should().BeLessThan(notesIdx);
    }

    #endregion

    #region BPM Formatting

    [Fact]
    public void WriteToString_FormatsDecimalBpmCorrectly()
    {
        var chart = MakeChart(timings: [new UrcTiming(0, 222.22, "4/4")]);

        var output = UrcWriter.WriteToString(chart);

        output.Should().Contain("0, 222.22, 4/4");
    }

    #endregion

    #region Helpers

    private static UrcChart MakeChart(
        UrcMetadata? metadata = null,
        UrcLayout? layout = null,
        UrcTiming[]? timings = null,
        UrcNote[]? notes = null,
        UrcJudgment? judgment = null)
    {
        return new UrcChart(
            FormatVersion: UrcFormat.Version,
            Metadata: metadata ?? new UrcMetadata("osu!mania", "Test", "Artist", "Mapper", "Hard"),
            Layout: layout ?? new UrcLayout(4, 0, Array.Empty<int>()),
            Timings: timings ?? [new UrcTiming(0, 180.0, "4/4")],
            Notes: notes ?? [new UrcNote(1000, 0, NoteType.Normal)],
            Judgment: judgment
        );
    }

    private static string GetLineContaining(string text, string fragment) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).First(l => l.Contains(fragment));

    #endregion
}