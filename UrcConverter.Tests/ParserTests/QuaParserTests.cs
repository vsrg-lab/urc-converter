using FluentAssertions;

using UrcConverter.Core;
using UrcConverter.Core.Abstractions;
using UrcConverter.Core.Models.Enums;
using UrcConverter.Parser.Qua;
using UrcConverter.Tests.Fixtures;

using Xunit;

namespace UrcConverter.Tests.ParserTests;

public sealed class QuaParserTests : IDisposable
{
    private readonly QuaFileFixture _fixture = new();
    private readonly QuaParser _parser = new();

    private IChartParser Parser => _parser;

    public void Dispose() => _fixture.Dispose();

    #region Basic Parsing

    [Fact]
    public void ParseToUrc_Minimal4K_ReturnsSuccess()
    {
        var path = _fixture.CreateTempQua(QuaFileFixture.Minimal4K);

        Parser.ParseToUrc(path).Should().BeOfType<ParseResult.Success>();
    }

    [Fact]
    public void ParseToUrc_Minimal4K_HasCorrectFormatVersion()
    {
        var chart = ParseFirst(QuaFileFixture.Minimal4K);

        chart.FormatVersion.Should().Be(UrcFormat.Version);
    }

    [Fact]
    public void ParseToUrc_Minimal4K_HasCorrectMetadata()
    {
        var chart = ParseFirst(QuaFileFixture.Minimal4K);

        chart.Metadata.Original.Should().Be("Quaver");
        chart.Metadata.Title.Should().Be("Test Song");
        chart.Metadata.Artist.Should().Be("Test Artist");
        chart.Metadata.Creator.Should().Be("TestMapper");
        chart.Metadata.Version.Should().Be("Hard");
    }

    #endregion

    #region Layout

    [Fact]
    public void ParseToUrc_Minimal4K_Has4KeyLayout()
    {
        var chart = ParseFirst(QuaFileFixture.Minimal4K);

        chart.Layout.KeyCount.Should().Be(4);
        chart.Layout.SpecialKeyCount.Should().Be(0);
        chart.Layout.SpecialLanes.Should().BeEmpty();
    }

    [Fact]
    public void ParseToUrc_SevenKey_Has7KeyLayout()
    {
        var chart = ParseFirst(QuaFileFixture.SevenKey);

        chart.Layout.KeyCount.Should().Be(7);
    }

    [Fact]
    public void ParseToUrc_WithScratchKey_HasSpecialLane()
    {
        var chart = ParseFirst(QuaFileFixture.WithScratchKey);

        chart.Layout.SpecialKeyCount.Should().Be(1);
        chart.Layout.SpecialLanes.Should().ContainSingle().Which.Should().Be(0);
    }

    #endregion

    #region Notes

    [Fact]
    public void ParseToUrc_Minimal4K_Has4NormalNotes()
    {
        var chart = ParseFirst(QuaFileFixture.Minimal4K);

        chart.Notes.Should().HaveCount(4);
        chart.Notes.Should().OnlyContain(n => n.Type == NoteType.Normal);
    }

    [Fact]
    public void ParseToUrc_Minimal4K_NotesAreSortedByTimestamp()
    {
        var chart = ParseFirst(QuaFileFixture.Minimal4K);

        chart.Notes.Should().BeInAscendingOrder(n => n.Timestamp);
    }

    [Fact]
    public void ParseToUrc_Minimal4K_LanesAreZeroIndexed()
    {
        var chart = ParseFirst(QuaFileFixture.Minimal4K);

        // .qua lanes are 1-indexed, URC should be 0-indexed
        chart.Notes[0].Lane.Should().Be(0);  // Lane 1 → 0
        chart.Notes[1].Lane.Should().Be(1);  // Lane 2 → 1
        chart.Notes[2].Lane.Should().Be(2);  // Lane 3 → 2
        chart.Notes[3].Lane.Should().Be(3);  // Lane 4 → 3
    }

    #endregion

    #region Hold Notes

    [Fact]
    public void ParseToUrc_WithHoldNote_ProducesLongNotes()
    {
        var chart = ParseFirst(QuaFileFixture.WithHoldNote);

        var starts = chart.Notes.Where(n => n.Type == NoteType.LongStart).ToList();
        var ends = chart.Notes.Where(n => n.Type == NoteType.LongEnd).ToList();

        starts.Should().HaveCount(1);
        ends.Should().HaveCount(1);
        starts[0].Lane.Should().Be(ends[0].Lane);
        starts[0].Timestamp.Should().Be(1000);
        ends[0].Timestamp.Should().Be(2000);
    }

    #endregion

    #region Timing

    [Fact]
    public void ParseToUrc_Minimal4K_HasCorrectBpm()
    {
        var chart = ParseFirst(QuaFileFixture.Minimal4K);

        chart.Timings.Should().Contain(t => t.Timestamp == 0);
        chart.Timings.First(t => t.Timestamp == 0).Bpm.Should().BeApproximately(180.0, 0.01);
    }

    [Fact]
    public void ParseToUrc_BpmChangeAndSv_HasBpmChangeWithSv()
    {
        var chart = ParseFirst(QuaFileFixture.BpmChangeAndSv);

        // Should have timing points at BPM 120 and 180
        chart.Timings.Should().Contain(t => Math.Abs(t.Bpm - 120.0) < 0.01);
        chart.Timings.Should().Contain(t => Math.Abs(t.Bpm - 180.0) < 0.01);

        // At t=5000, BPM 180 + SV 0.8 merged
        var change = chart.Timings.First(t => t.Timestamp == 5000);
        change.Bpm.Should().BeApproximately(180.0, 0.01);
        change.Multiplier.Should().BeApproximately(0.8, 0.01);
    }

    [Fact]
    public void ParseToUrc_BpmChangeAndSv_NoDuplicateTimestamps()
    {
        var chart = ParseFirst(QuaFileFixture.BpmChangeAndSv);
        var timestamps = chart.Timings.Select(t => t.Timestamp).ToList();

        timestamps.Should().OnlyHaveUniqueItems();
    }

    #endregion

    #region Meter

    [Fact]
    public void ParseToUrc_Minimal4K_DefaultMeterIs44()
    {
        var chart = ParseFirst(QuaFileFixture.Minimal4K);

        chart.Timings.First(t => t.Timestamp == 0).Meter.Should().Be("4/4");
    }

    [Fact]
    public void ParseToUrc_ThreeFourTime_MeterIs34()
    {
        var chart = ParseFirst(QuaFileFixture.ThreeFourTime);

        chart.Timings.First(t => t.Timestamp == 0).Meter.Should().Be("3/4");
    }

    #endregion

    #region Judgment

    [Fact]
    public void ParseToUrc_Minimal4K_JudgmentIsNull()
    {
        var chart = ParseFirst(QuaFileFixture.Minimal4K);

        chart.Judgment.Should().BeNull();
    }

    #endregion

    #region Rejection

    [Fact]
    public void ParseToUrc_InvalidContent_ReturnsFailure()
    {
        var path = _fixture.CreateTempQua(QuaFileFixture.InvalidContent);

        Parser.ParseToUrc(path).Should().BeOfType<ParseResult.Failure>();
    }

    [Fact]
    public void ParseToUrc_NonexistentFile_ReturnsFailure()
    {
        Parser.ParseToUrc(@"C:\nonexistent\file.qua").Should().BeOfType<ParseResult.Failure>();
    }

    #endregion

    #region Interface

    [Fact]
    public void SupportedExtensions_ContainsQua()
    {
        Parser.SupportedExtensions.Should().Contain(".qua");
    }

    [Fact]
    public void FormatName_IsQuaver()
    {
        Parser.FormatName.Should().Be("Quaver");
    }

    #endregion

    #region Helpers

    private Core.Models.UrcChart ParseFirst(string content)
    {
        var path = _fixture.CreateTempQua(content);
        var result = Parser.ParseToUrc(path);
        result.Should().BeOfType<ParseResult.Success>();
        return ((ParseResult.Success)result).Charts[0];
    }

    #endregion
}