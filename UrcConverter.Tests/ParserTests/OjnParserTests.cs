using FluentAssertions;
using Xunit;
using UrcConverter.Core;
using UrcConverter.Core.Abstractions;
using UrcConverter.Core.Models;
using UrcConverter.Core.Models.Enums;
using UrcConverter.Parser.Ojn;
using UrcConverter.Tests.Fixtures;

namespace UrcConverter.Tests.ParserTests;

public sealed class OjnParserTests : IDisposable
{
    private readonly OjnFileFixture _fixture = new();
    private readonly OjnParser _parser = new();

    private IChartParser Parser => _parser;

    public void Dispose() => _fixture.Dispose();

    #region Basic Parsing

    [Fact]
    public void ParseToUrc_Minimal7K_ReturnsSuccess()
    {
        var path = _fixture.CreateTempOjn(OjnFileFixture.Minimal7K);

        Parser.ParseToUrc(path).Should().BeOfType<ParseResult.Success>();
    }

    [Fact]
    public void ParseToUrc_Minimal7K_HasCorrectFormatVersion()
    {
        var chart = ParseLast(OjnFileFixture.Minimal7K);

        chart.FormatVersion.Should().Be(UrcFormat.Version);
    }

    [Fact]
    public void ParseToUrc_Minimal7K_HasCorrectMetadata()
    {
        var chart = ParseLast(OjnFileFixture.Minimal7K);

        chart.Metadata.Original.Should().Be("O2Jam");
        chart.Metadata.Title.Should().Be("Test O2Jam");
        chart.Metadata.Artist.Should().Be("Test Artist");
        chart.Metadata.Creator.Should().Be("TestNoter");
        chart.Metadata.Version.Should().Contain("HX");
    }

    #endregion

    #region Layout

    [Fact]
    public void ParseToUrc_Minimal7K_Has7KeyLayout()
    {
        var chart = ParseLast(OjnFileFixture.Minimal7K);

        chart.Layout.KeyCount.Should().Be(7);
        chart.Layout.SpecialKeyCount.Should().Be(0);
        chart.Layout.SpecialLanes.Should().BeEmpty();
    }

    #endregion

    #region Notes

    [Fact]
    public void ParseToUrc_Minimal7K_Has4NormalNotes()
    {
        var chart = ParseLast(OjnFileFixture.Minimal7K);

        chart.Notes.Should().HaveCount(4);
        chart.Notes.Should().OnlyContain(n => n.Type == NoteType.Normal);
    }

    [Fact]
    public void ParseToUrc_Minimal7K_NotesAreSortedByTimestamp()
    {
        var chart = ParseLast(OjnFileFixture.Minimal7K);

        chart.Notes.Should().BeInAscendingOrder(n => n.Timestamp);
    }

    [Fact]
    public void ParseToUrc_Minimal7K_NotesOnDifferentLanes()
    {
        var chart = ParseLast(OjnFileFixture.Minimal7K);
        var lanes = chart.Notes.Select(n => n.Lane).Distinct().ToList();

        lanes.Should().HaveCount(4);
        chart.Notes.Should().Contain(n => n.Lane == 0);
        chart.Notes.Should().Contain(n => n.Lane == 1);
        chart.Notes.Should().Contain(n => n.Lane == 2);
        chart.Notes.Should().Contain(n => n.Lane == 3);
    }

    #endregion

    #region Long Notes

    [Fact]
    public void ParseToUrc_WithLongNote_ProducesLongNotes()
    {
        var chart = ParseLast(OjnFileFixture.WithLongNote);

        var starts = chart.Notes.Where(n => n.Type == NoteType.LongStart).ToList();
        var ends = chart.Notes.Where(n => n.Type == NoteType.LongEnd).ToList();

        starts.Should().HaveCount(1);
        ends.Should().HaveCount(1);
        starts[0].Lane.Should().Be(ends[0].Lane);
        starts[0].Timestamp.Should().BeLessThan(ends[0].Timestamp);
    }

    #endregion

    #region Timing

    [Fact]
    public void ParseToUrc_Minimal7K_HasCorrectBpm()
    {
        var chart = ParseLast(OjnFileFixture.Minimal7K);

        chart.Timings.Should().Contain(t => t.Timestamp == 0);
        chart.Timings.First(t => t.Timestamp == 0).Bpm.Should().BeApproximately(180.0, 0.01);
    }

    [Fact]
    public void ParseToUrc_Minimal7K_MultiplierIsAlwaysOne()
    {
        var chart = ParseLast(OjnFileFixture.Minimal7K);

        chart.Timings.Should().OnlyContain(t => Math.Abs(t.Multiplier - 1.0) < 0.001);
    }

    [Fact]
    public void ParseToUrc_WithBpmChange_HasBpmChange()
    {
        var chart = ParseLast(OjnFileFixture.WithBpmChange);

        chart.Timings.Should().Contain(t => Math.Abs(t.Bpm - 120.0) < 0.01);
        chart.Timings.Should().Contain(t => Math.Abs(t.Bpm - 200.0) < 0.01);
    }

    [Fact]
    public void ParseToUrc_WithBpmChange_NoDuplicateTimestamps()
    {
        var chart = ParseLast(OjnFileFixture.WithBpmChange);
        var timestamps = chart.Timings.Select(t => t.Timestamp).ToList();

        timestamps.Should().OnlyHaveUniqueItems();
    }

    #endregion

    #region Meter Inference

    [Fact]
    public void ParseToUrc_Minimal7K_DefaultMeterIs44()
    {
        var chart = ParseLast(OjnFileFixture.Minimal7K);

        chart.Timings.First(t => t.Timestamp == 0).Meter.Should().Be("4/4");
    }

    [Fact]
    public void ParseToUrc_MeasureFraction34_InfersMeterAs34()
    {
        var chart = ParseLast(OjnFileFixture.MeasureFraction34);

        chart.Timings.First(t => t.Timestamp == 0).Meter.Should().Be("3/4");
    }

    [Fact]
    public void ParseToUrc_MeasureFraction34_AffectsTimestamps()
    {
        var chart = ParseLast(OjnFileFixture.MeasureFraction34);

        // Measure 0 at 3/4, BPM 120: 0.75 * 4 * 60000/120 = 1500ms
        // Note in measure 1 starts at 1500ms
        var lastNote = chart.Notes.OrderByDescending(n => n.Timestamp).First();
        lastNote.Timestamp.Should().Be(1500);
    }

    #endregion

    #region Multiple Difficulties

    [Fact]
    public void ParseToUrc_Minimal7K_SkipsEmptyDifficulties()
    {
        var path = _fixture.CreateTempOjn(OjnFileFixture.Minimal7K);
        var result = Parser.ParseToUrc(path);

        result.Should().BeOfType<ParseResult.Success>();
        var charts = ((ParseResult.Success)result).Charts;

        // Only HX has notes
        charts.Should().ContainSingle();
        charts[0].Metadata.Version.Should().Contain("HX");
    }

    [Fact]
    public void ParseToUrc_ThreeDifficulties_ReturnsAllThreeCharts()
    {
        var path = _fixture.CreateTempOjn(OjnFileFixture.ThreeDifficulties);
        var result = Parser.ParseToUrc(path);

        result.Should().BeOfType<ParseResult.Success>();
        var charts = ((ParseResult.Success)result).Charts;

        charts.Should().HaveCount(3);
        charts.Should().Contain(c => c.Metadata.Version.Contains("EX"));
        charts.Should().Contain(c => c.Metadata.Version.Contains("NX"));
        charts.Should().Contain(c => c.Metadata.Version.Contains("HX"));
    }

    [Fact]
    public void ParseToUrc_ThreeDifficulties_HxHasMoreNotes()
    {
        var path = _fixture.CreateTempOjn(OjnFileFixture.ThreeDifficulties);
        var charts = ((ParseResult.Success)Parser.ParseToUrc(path)).Charts;

        var ex = charts.First(c => c.Metadata.Version.Contains("EX"));
        var hx = charts.First(c => c.Metadata.Version.Contains("HX"));

        hx.Notes.Count.Should().BeGreaterThan(ex.Notes.Count);
    }

    #endregion

    #region Judgment

    [Fact]
    public void ParseToUrc_Minimal7K_JudgmentIsNull()
    {
        var chart = ParseLast(OjnFileFixture.Minimal7K);

        chart.Judgment.Should().BeNull();
    }

    #endregion

    #region Rejection

    [Fact]
    public void ParseToUrc_InvalidSignature_ReturnsFailure()
    {
        var path = _fixture.CreateTempOjn(OjnFileFixture.InvalidSignature);

        Parser.ParseToUrc(path).Should().BeOfType<ParseResult.Failure>();
    }

    [Fact]
    public void ParseToUrc_NonexistentFile_ReturnsFailure()
    {
        Parser.ParseToUrc(@"C:\nonexistent\file.ojn").Should().BeOfType<ParseResult.Failure>();
    }

    [Fact]
    public void ParseToUrc_TooSmallFile_ReturnsFailure()
    {
        var path = _fixture.CreateTempOjn(new byte[100]);

        Parser.ParseToUrc(path).Should().BeOfType<ParseResult.Failure>();
    }

    #endregion

    #region Interface

    [Fact]
    public void SupportedExtensions_ContainsOjn()
    {
        Parser.SupportedExtensions.Should().Contain(".ojn");
    }

    [Fact]
    public void FormatName_IsO2Jam()
    {
        Parser.FormatName.Should().Be("O2Jam");
    }

    #endregion

    #region Helpers

    private UrcChart ParseLast(byte[] data)
    {
        var path = _fixture.CreateTempOjn(data);
        var result = Parser.ParseToUrc(path);
        result.Should().BeOfType<ParseResult.Success>();
        var charts = ((ParseResult.Success)result).Charts;
        return charts[^1];
    }

    #endregion
}