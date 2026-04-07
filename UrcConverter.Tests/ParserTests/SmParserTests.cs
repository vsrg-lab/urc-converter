using FluentAssertions;

using UrcConverter.Core;
using UrcConverter.Core.Abstractions;
using UrcConverter.Core.Models;
using UrcConverter.Core.Models.Enums;
using UrcConverter.Parser.Sm;
using UrcConverter.Tests.Fixtures;

using Xunit;

namespace UrcConverter.Tests.ParserTests;

public sealed class SmParserTests : IDisposable
{
    private readonly SmFileFixture _fixture = new();
    private readonly SmParser _parser = new();

    private IChartParser Parser => _parser;

    public void Dispose() => _fixture.Dispose();

    #region Basic Parsing

    [Fact]
    public void ParseToUrc_Minimal4K_ReturnsSuccess()
    {
        var path = _fixture.CreateTempSm(SmFileFixture.Minimal4K);

        Parser.ParseToUrc(path).Should().BeOfType<ParseResult.Success>();
    }

    [Fact]
    public void ParseToUrc_Minimal4K_HasCorrectFormatVersion()
    {
        var chart = ParseFirst(SmFileFixture.Minimal4K);

        chart.FormatVersion.Should().Be(UrcFormat.Version);
    }

    [Fact]
    public void ParseToUrc_Minimal4K_HasCorrectMetadata()
    {
        var chart = ParseFirst(SmFileFixture.Minimal4K);

        chart.Metadata.Original.Should().Be("StepMania");
        chart.Metadata.Title.Should().Be("Test Song");
        chart.Metadata.Artist.Should().Be("Test Artist");
        chart.Metadata.Creator.Should().Be("TestMapper");
    }

    #endregion

    #region Layout

    [Fact]
    public void ParseToUrc_Minimal4K_HasCorrectLayout()
    {
        var chart = ParseFirst(SmFileFixture.Minimal4K);

        chart.Layout.KeyCount.Should().Be(4);
        chart.Layout.SpecialKeyCount.Should().Be(0);
    }

    [Fact]
    public void ParseToUrc_PumpSingle5K_Has5KeyLayout()
    {
        var chart = ParseFirst(SmFileFixture.PumpSingle5K);

        chart.Layout.KeyCount.Should().Be(5);
    }

    #endregion

    #region Notes

    [Fact]
    public void ParseToUrc_Minimal4K_Has4NormalNotes()
    {
        var chart = ParseFirst(SmFileFixture.Minimal4K);

        chart.Notes.Should().HaveCount(4);
        chart.Notes.Should().OnlyContain(n => n.Type == NoteType.Normal);
    }

    [Fact]
    public void ParseToUrc_Minimal4K_NotesAreSortedByTimestamp()
    {
        var chart = ParseFirst(SmFileFixture.Minimal4K);

        chart.Notes.Should().BeInAscendingOrder(n => n.Timestamp);
    }

    [Fact]
    public void ParseToUrc_Minimal4K_NotesOnDifferentLanes()
    {
        var chart = ParseFirst(SmFileFixture.Minimal4K);

        var lanes = chart.Notes.Select(n => n.Lane).Distinct().ToList();
        lanes.Should().HaveCount(4);
    }

    #endregion

    #region Hold Notes

    [Fact]
    public void ParseToUrc_WithHoldNote_ProducesLongNotes()
    {
        var chart = ParseFirst(SmFileFixture.WithHoldNote);

        var starts = chart.Notes.Where(n => n.Type == NoteType.LongStart).ToList();
        var ends = chart.Notes.Where(n => n.Type == NoteType.LongEnd).ToList();

        starts.Should().HaveCount(1);
        ends.Should().HaveCount(1);
        starts[0].Lane.Should().Be(ends[0].Lane);
        starts[0].Timestamp.Should().BeLessThan(ends[0].Timestamp);
    }

    #endregion

    #region Mines and Fakes

    [Fact]
    public void ParseToUrc_MinesAndFakes_HasCorrectNoteTypes()
    {
        var chart = ParseFirst(SmFileFixture.MinesAndFakes);

        chart.Notes.Should().Contain(n => n.Type == NoteType.Normal);
        chart.Notes.Should().Contain(n => n.Type == NoteType.Mine);
        chart.Notes.Should().Contain(n => n.Type == NoteType.Fake);
    }

    #endregion

    #region Timing

    [Fact]
    public void ParseToUrc_Minimal4K_HasCorrectBpm()
    {
        var chart = ParseFirst(SmFileFixture.Minimal4K);

        chart.Timings.Should().Contain(t => t.Timestamp == 0);
        chart.Timings.First(t => t.Timestamp == 0).Bpm.Should().BeApproximately(180.0, 0.01);
    }

    [Fact]
    public void ParseToUrc_BpmChangeAndStop_HasBpmChange()
    {
        var chart = ParseFirst(SmFileFixture.BpmChangeAndStop);

        chart.Timings.Should().Contain(t => Math.Abs(t.Bpm - 120.0) < 0.01);
        chart.Timings.Should().Contain(t => Math.Abs(t.Bpm - 180.0) < 0.01);
    }

    [Fact]
    public void ParseToUrc_BpmChangeAndStop_StopDelaysNote()
    {
        var chart = ParseFirst(SmFileFixture.BpmChangeAndStop);

        var notes = chart.Notes.OrderBy(n => n.Timestamp).ToList();
        notes.Should().HaveCount(2);

        // Second note at beat 4 = 2000ms (stop doesn't affect notes at same beat)
        notes[1].Timestamp.Should().Be(2000);
    }

    #endregion

    #region Multiple Charts

    [Fact]
    public void ParseToUrc_MultipleCharts_ReturnsAllCharts()
    {
        var path = _fixture.CreateTempSm(SmFileFixture.MultipleCharts);
        var result = Parser.ParseToUrc(path);

        result.Should().BeOfType<ParseResult.Success>();
        var charts = ((ParseResult.Success)result).Charts;
        charts.Should().HaveCount(2);
    }

    [Fact]
    public void ParseToUrc_MultipleCharts_ContainsBothDifficulties()
    {
        var path = _fixture.CreateTempSm(SmFileFixture.MultipleCharts);
        var charts = ((ParseResult.Success)Parser.ParseToUrc(path)).Charts;

        charts.Should().Contain(c => c.Metadata.Version.Contains("Easy"));
        charts.Should().Contain(c => c.Metadata.Version.Contains("Challenge"));
    }

    [Fact]
    public void ParseToUrc_MultipleCharts_ChallengeHasMoreNotes()
    {
        var path = _fixture.CreateTempSm(SmFileFixture.MultipleCharts);
        var charts = ((ParseResult.Success)Parser.ParseToUrc(path)).Charts;

        var easy = charts.First(c => c.Metadata.Version.Contains("Easy"));
        var challenge = charts.First(c => c.Metadata.Version.Contains("Challenge"));

        challenge.Notes.Count.Should().BeGreaterThan(easy.Notes.Count);
    }

    #endregion

    #region SSC Format

    [Fact]
    public void ParseToUrc_SscWithScrolls_ReturnsSuccess()
    {
        var path = _fixture.CreateTempSm(SmFileFixture.SscWithScrolls, ".ssc");

        Parser.ParseToUrc(path).Should().BeOfType<ParseResult.Success>();
    }

    [Fact]
    public void ParseToUrc_SscWithScrolls_HasScrollMultiplier()
    {
        var path = _fixture.CreateTempSm(SmFileFixture.SscWithScrolls, ".ssc");
        var chart = ((ParseResult.Success)Parser.ParseToUrc(path)).Charts[0];

        var svChange = chart.Timings.FirstOrDefault(t => t.Timestamp > 0 && Math.Abs(t.Multiplier - 0.5) < 0.01);
        svChange.Should().NotBeNull();
    }

    #endregion

    #region Judgment

    [Fact]
    public void ParseToUrc_Minimal4K_JudgmentIsNull()
    {
        var chart = ParseFirst(SmFileFixture.Minimal4K);

        chart.Judgment.Should().BeNull();
    }

    #endregion

    #region Rejection

    [Fact]
    public void ParseToUrc_InvalidContent_ReturnsFailure()
    {
        var path = _fixture.CreateTempSm(SmFileFixture.InvalidContent);

        Parser.ParseToUrc(path).Should().BeOfType<ParseResult.Failure>();
    }

    [Fact]
    public void ParseToUrc_NonexistentFile_ReturnsFailure()
    {
        Parser.ParseToUrc(@"C:\nonexistent\file.sm").Should().BeOfType<ParseResult.Failure>();
    }

    #endregion

    #region Interface

    [Fact]
    public void SupportedExtensions_ContainsSmAndSsc()
    {
        Parser.SupportedExtensions.Should().Contain(".sm");
        Parser.SupportedExtensions.Should().Contain(".ssc");
    }

    [Fact]
    public void FormatName_IsStepMania()
    {
        Parser.FormatName.Should().Be("StepMania");
    }

    #endregion

    #region Helpers

    private UrcChart ParseFirst(string content, string ext = ".sm")
    {
        var path = _fixture.CreateTempSm(content, ext);
        var result = Parser.ParseToUrc(path);
        result.Should().BeOfType<ParseResult.Success>();
        return ((ParseResult.Success)result).Charts[0];
    }

    #endregion
}