using FluentAssertions;

using UrcConverter.Core;
using UrcConverter.Core.Abstractions;
using UrcConverter.Core.Models.Enums;
using UrcConverter.Parser.Bms;
using UrcConverter.Tests.Fixtures;

using Xunit;

namespace UrcConverter.Tests.ParserTests;

public sealed class BmsParserTests : IDisposable
{
    private readonly BmsFileFixture _fixture = new();
    private readonly BmsParser _parser = new();

    private IChartParser Parser => _parser;

    public void Dispose() => _fixture.Dispose();

    #region Basic Parsing

    [Fact]
    public void ParseToUrc_Minimal7K_ReturnsSuccess()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.Minimal7K);
        var result = Parser.ParseToUrc(path);

        result.Should().BeOfType<ParseResult.Success>();
    }

    [Fact]
    public void ParseToUrc_Minimal7K_HasCorrectFormatVersion()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.Minimal7K);
        var chart = ParseSuccess(path);

        chart.FormatVersion.Should().Be(UrcFormat.Version);
    }

    [Fact]
    public void ParseToUrc_Minimal7K_HasCorrectMetadata()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.Minimal7K);
        var chart = ParseSuccess(path);

        chart.Metadata.Original.Should().Be("BMS");
        chart.Metadata.Title.Should().Be("Test BMS");
        chart.Metadata.Artist.Should().Be("Test Artist");
    }

    #endregion

    #region Layout Detection

    [Fact]
    public void ParseToUrc_Minimal7K_Detects7KNoScratch()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.Minimal7K);
        var chart = ParseSuccess(path);

        chart.Layout.KeyCount.Should().Be(7);
        chart.Layout.SpecialKeyCount.Should().Be(0);
        chart.Layout.SpecialLanes.Should().BeEmpty();
    }

    [Fact]
    public void ParseToUrc_5KWithScratch_Detects5KPlusScratch()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.FiveKeyWithScratch);
        var chart = ParseSuccess(path);

        chart.Layout.KeyCount.Should().Be(5);
        chart.Layout.SpecialKeyCount.Should().Be(1);
        chart.Layout.SpecialLanes.Should().ContainSingle().Which.Should().Be(0);
    }

    #endregion

    #region Note Conversion

    [Fact]
    public void ParseToUrc_Minimal7K_Has4NormalNotes()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.Minimal7K);
        var chart = ParseSuccess(path);

        chart.Notes.Should().HaveCount(4);
        chart.Notes.Should().OnlyContain(n => n.Type == NoteType.Normal);
    }

    [Fact]
    public void ParseToUrc_Minimal7K_NotesAreSortedByTimestamp()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.Minimal7K);
        var chart = ParseSuccess(path);

        chart.Notes.Should().BeInAscendingOrder(n => n.Timestamp);
    }

    #endregion

    #region Timing

    [Fact]
    public void ParseToUrc_Minimal7K_HasTimingPointWithCorrectBpm()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.Minimal7K);
        var chart = ParseSuccess(path);

        chart.Timings.Should().Contain(t => t.Timestamp == 0);
        chart.Timings.First(t => t.Timestamp == 0).Bpm.Should().BeApproximately(180.0, 0.01);
    }

    [Fact]
    public void ParseToUrc_BpmAndScroll_HasBpmChangeWithScroll()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.BpmAndScrollChange);
        var chart = ParseSuccess(path);
        var change = chart.Timings.First(t => t.Timestamp > 0);
        
        change.Bpm.Should().BeApproximately(180.0, 0.01);
        change.Multiplier.Should().BeApproximately(0.8, 0.01);
    }

    [Fact]
    public void ParseToUrc_BpmAndScroll_NoDuplicateTimestamps()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.BpmAndScrollChange);
        var chart = ParseSuccess(path);
        var timestamps = chart.Timings.Select(t => t.Timestamp).ToList();

        timestamps.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ParseToUrc_ExtBpm_ChangesToExtendedBpm()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.ExtBpm);
        var chart = ParseSuccess(path);

        chart.Timings.Should().Contain(t => Math.Abs(t.Bpm - 200.0) < 0.01);
    }

    [Fact]
    public void ParseToUrc_StopAddsTime_NoteAfterStopIsDelayed()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.StopAddsTime);
        var chart = ParseSuccess(path);

        // Measure 0 at BPM 120: duration = 4 * 60000/120 = 2000ms
        // Stop = 48 units at BPM 200: 48/48 * 60000/200 = 300ms
        // Note in measure 1 should be > 2000ms
        var lastNote = chart.Notes.OrderByDescending(n => n.Timestamp).First();
        lastNote.Timestamp.Should().BeGreaterThan(2000);
    }

    #endregion

    #region Meter Inference

    [Fact]
    public void ParseToUrc_MeasureLength34_InfersMeterAs34()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.MeasureLength34);
        var chart = ParseSuccess(path);

        // Measure 0 has length 0.75 → 3/4
        var initialTiming = chart.Timings.First(t => t.Timestamp == 0);
        initialTiming.Meter.Should().Be("3/4");
    }

    [Fact]
    public void ParseToUrc_MeasureLength34_AffectsTimestamps()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.MeasureLength34);
        var chart = ParseSuccess(path);

        // Measure 0 duration at 3/4, BPM 120: 0.75 * 4 * 60000/120 = 1500ms
        // Note in measure 1 starts at 1500ms
        chart.Notes.Should().HaveCount(3);
        chart.Notes[0].Timestamp.Should().Be(0);
        chart.Notes[2].Timestamp.Should().Be(1500);
    }

    [Fact]
    public void ParseToUrc_MeasureLength78_InfersMeterAs78()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.MeasureLength78);
        var chart = ParseSuccess(path);

        // Measure 0 is default (4/4), measure 1 has length 0.875 → 7/8
        // There should be a timing point at measure 1 start with meter "7/8"
        var measure1Start = chart.Timings.Where(t => t.Timestamp > 0 && t.Meter == "7/8");
        measure1Start.Should().NotBeEmpty();
    }

    [Fact]
    public void ParseToUrc_Minimal7K_DefaultMeterIs44()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.Minimal7K);
        var chart = ParseSuccess(path);

        chart.Timings.First(t => t.Timestamp == 0).Meter.Should().Be("4/4");
    }

    #endregion

    #region Long Notes

    [Fact]
    public void ParseToUrc_LnType1_ProducesLongNotes()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.LnType1);
        var chart = ParseSuccess(path);

        var starts = chart.Notes.Where(n => n.Type == NoteType.LongStart).ToList();
        var ends = chart.Notes.Where(n => n.Type == NoteType.LongEnd).ToList();

        starts.Should().HaveCount(1);
        ends.Should().HaveCount(1);
        starts[0].Lane.Should().Be(ends[0].Lane);
        starts[0].Timestamp.Should().BeLessThan(ends[0].Timestamp);
    }

    [Fact]
    public void ParseToUrc_LnObj_ProducesLongNotes()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.LnObj);
        var chart = ParseSuccess(path);

        var starts = chart.Notes.Where(n => n.Type == NoteType.LongStart).ToList();
        var ends = chart.Notes.Where(n => n.Type == NoteType.LongEnd).ToList();

        starts.Should().HaveCount(1);
        ends.Should().HaveCount(1);
        starts[0].Lane.Should().Be(ends[0].Lane);
        starts[0].Timestamp.Should().BeLessThan(ends[0].Timestamp);
    }

    #endregion

    #region Judgment

    [Fact]
    public void ParseToUrc_Minimal7K_JudgmentIsNull()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.Minimal7K);
        var chart = ParseSuccess(path);

        chart.Judgment.Should().BeNull();
    }

    #endregion

    #region Rejection

    [Fact]
    public void ParseToUrc_InvalidContent_ReturnsFailure()
    {
        var path = _fixture.CreateTempBms(BmsFileFixture.InvalidContent);
        var result = Parser.ParseToUrc(path);

        result.Should().BeOfType<ParseResult.Failure>();
    }

    [Fact]
    public void ParseToUrc_NonexistentFile_ReturnsFailure()
    {
        var result = Parser.ParseToUrc(@"C:\nonexistent\file.bms");

        result.Should().BeOfType<ParseResult.Failure>();
    }

    #endregion

    #region Interface

    [Fact]
    public void SupportedExtensions_ContainsBmsFormats()
    {
        Parser.SupportedExtensions.Should().Contain(".bms");
        Parser.SupportedExtensions.Should().Contain(".bme");
        Parser.SupportedExtensions.Should().Contain(".bml");
    }

    [Fact]
    public void FormatName_IsBms()
    {
        Parser.FormatName.Should().Be("BMS");
    }

    #endregion

    #region Helpers

    private Core.Models.UrcChart ParseSuccess(string path)
    {
        var result = Parser.ParseToUrc(path);
        result.Should().BeOfType<ParseResult.Success>();
        return ((ParseResult.Success)result).Chart;
    }

    #endregion
}