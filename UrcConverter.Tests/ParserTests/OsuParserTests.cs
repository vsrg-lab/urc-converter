using FluentAssertions;
using Xunit;
using UrcConverter.Core;
using UrcConverter.Core.Abstractions;
using UrcConverter.Core.Models.Enums;
using UrcConverter.Parser.Osu;
using UrcConverter.Tests.Fixtures;

namespace UrcConverter.Tests.ParserTests;

public sealed class OsuParserTests : IDisposable
{
    private readonly OsuFileFixture _fixture = new();
    private readonly OsuParser _parser = new();

    private IChartParser Parser => _parser;
    
    public void Dispose() => _fixture.Dispose();

    #region Basic Parsing

    [Fact]
    public void ParseToUrc_Minimal4K_ReturnsSuccess()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.Minimal4K);
        var result = Parser.ParseToUrc(path);
        
        result.Should().BeOfType<ParseResult.Success>();
    }

    [Fact]
    public void ParseToUrc_Minimal4K_HasCorrectFormatVersion()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.Minimal4K);
        var chart = ParseSuccess(path);
        
        chart.FormatVersion.Should().Be(UrcFormat.Version);
    }

    [Fact]
    public void ParseToUrc_Minimal4K_PrefersUnicodeMetadata()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.Minimal4K);
        var chart = ParseSuccess(path);

        chart.Metadata.Title.Should().Be("テスト曲");
        chart.Metadata.Artist.Should().Be("テストアーティスト");
        chart.Metadata.Creator.Should().Be("TestMapper");
        chart.Metadata.Version.Should().Be("Hard");
        chart.Metadata.Original.Should().Be("osu!mania");
    }

    [Fact]
    public void ParseToUrc_Minimal4K_HasCorrectLayout()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.Minimal4K);
        var chart = ParseSuccess(path);

        chart.Layout.KeyCount.Should().Be(4);
        chart.Layout.SpecialKeyCount.Should().Be(0);
        chart.Layout.SpecialLanes.Should().BeEmpty();
    }

    #endregion

    #region Note Conversion

    [Fact]
    public void ParseToUrc_Minimal4K_ConvertsNormalNotes()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.Minimal4K);
        var chart = ParseSuccess(path);
        var normals = chart.Notes.Where(n => n.Type == NoteType.Normal).ToList();
        
        normals.Should().HaveCount(3);
    }

    [Fact]
    public void ParseToUrc_Minimal4K_ConvertsHoldNote()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.Minimal4K);
        var chart = ParseSuccess(path);
        var lnStarts = chart.Notes.Where(n => n.Type == NoteType.LongStart).ToList();
        var lnEnds = chart.Notes.Where(n => n.Type == NoteType.LongEnd).ToList();

        lnStarts.Should().ContainSingle();
        lnEnds.Should().ContainSingle();
        lnStarts[0].Lane.Should().Be(lnEnds[0].Lane);
        lnStarts[0].Timestamp.Should().BeLessThan(lnEnds[0].Timestamp);
    }

    [Fact]
    public void ParseToUrc_Minimal4K_CalculatesLanesCorrectly()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.Minimal4K);
        var chart = ParseSuccess(path);

        // 4K: x=64 → lane 0, x=192 → lane 1, x=320 → lane 2, x=448 → lane 3
        chart.Notes.Should().Contain(n => n.Timestamp == 1000 && n.Lane == 0);
        chart.Notes.Should().Contain(n => n.Timestamp == 1500 && n.Lane == 1);
        chart.Notes.Should().Contain(n => n.Timestamp == 2000 && n.Lane == 2 && n.Type == NoteType.LongStart);
        chart.Notes.Should().Contain(n => n.Timestamp == 2500 && n.Lane == 3);
    }

    [Fact]
    public void ParseToUrc_Minimal4K_NotesAreSortedByTimestamp()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.Minimal4K);
        var chart = ParseSuccess(path);

        chart.Notes.Should().BeInAscendingOrder(n => n.Timestamp);
    }

    #endregion

    #region Timing Conversion

    [Fact]
    public void ParseToUrc_Minimal4K_HasSingleTimingPoint()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.Minimal4K);
        var chart = ParseSuccess(path);

        chart.Timings.Should().ContainSingle();
        chart.Timings[0].Timestamp.Should().Be(0);
        chart.Timings[0].Bpm.Should().BeApproximately(180.0, 0.01);
        chart.Timings[0].Meter.Should().Be("4/4");
        chart.Timings[0].Multiplier.Should().Be(1.0);
    }

    [Fact]
    public void ParseToUrc_SimultaneousBpmAndSv_MergesIntoSingleTimingPoint()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.SimultaneousBpmAndSv);
        var chart = ParseSuccess(path);

        // Should have 2 timing points: t=0 and t=5000 (merged)
        chart.Timings.Should().HaveCount(2);

        var merged = chart.Timings.Single(t => t.Timestamp == 5000);
        merged.Bpm.Should().BeApproximately(180.0, 0.01);    // from red line
        merged.Multiplier.Should().BeApproximately(0.8, 0.01); // from green line (-125 → 100/125 = 0.8)
    }

    [Fact]
    public void ParseToUrc_SimultaneousBpmAndSv_DoesNotProduceDuplicateTimestamps()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.SimultaneousBpmAndSv);
        var chart = ParseSuccess(path);
        var timestamps = chart.Timings.Select(t => t.Timestamp).ToList();

        timestamps.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ParseToUrc_MultipleSvChanges_ConvertsCorrectly()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.MultipleSvChanges);
        var chart = ParseSuccess(path);

        // BPM 180, then SV changes at 2000, 4000, 6000
        chart.Timings.Should().HaveCount(4);
        chart.Timings[0].Multiplier.Should().Be(1.0);

        // -200 → 100/200 = 0.5
        chart.Timings[1].Multiplier.Should().BeApproximately(0.5, 0.01);
        chart.Timings[1].Bpm.Should().BeApproximately(180.0, 0.01); // BPM carried

        // -50 → 100/50 = 2.0
        chart.Timings[2].Multiplier.Should().BeApproximately(2.0, 0.01);

        // -100 → 100/100 = 1.0
        chart.Timings[3].Multiplier.Should().BeApproximately(1.0, 0.01);
    }

    #endregion

    #region Judgment

    [Fact]
    public void ParseToUrc_Minimal4K_HasJudgmentFromOD()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.Minimal4K);
        var chart = ParseSuccess(path);

        chart.Judgment.Should().NotBeNull();
        chart.Judgment!.Windows.Should().HaveCount(5);
        chart.Judgment.Rates.Should().HaveCount(5);

        // OD 8: MAX=16.5, 300=67.5-24=43.5, 200=100.5-24=76.5, ...
        chart.Judgment.Windows[0].Should().Be(16.5);
        chart.Judgment.Windows[1].Should().BeApproximately(43.5, 0.01);
    }

    #endregion

    #region Rejection

    [Fact]
    public void ParseToUrc_StandardMode_ReturnsFailure()
    {
        var path = _fixture.CreateTempOsu(OsuFileFixture.StandardMode);
        var result = Parser.ParseToUrc(path);

        result.Should().BeOfType<ParseResult.Failure>();
        ((ParseResult.Failure)result).Error.Should().Contain("Mode");
    }

    [Fact]
    public void ParseToUrc_NonexistentFile_ReturnsFailure()
    {
        var result = Parser.ParseToUrc(@"C:\nonexistent\file.osu");

        result.Should().BeOfType<ParseResult.Failure>();
    }

    #endregion

    #region Interface

    [Fact]
    public void SupportedExtensions_ContainsOsu()
    {
        Parser.SupportedExtensions.Should().Contain(".osu");
    }

    [Fact]
    public void FormatName_IsOsuMania()
    {
        Parser.FormatName.Should().Be("osu!mania");
    }

    #endregion

    #region Helpers

    private Core.Models.UrcChart ParseSuccess(string path)
    {
        var result = Parser.ParseToUrc(path);
        result.Should().BeOfType<ParseResult.Success>();
        return ((ParseResult.Success)result).Charts[0];
    }

    #endregion
}