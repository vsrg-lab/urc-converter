using FluentAssertions;
using Xunit;
using UrcConverter.Core;
using UrcConverter.Core.Abstractions;
using UrcConverter.Core.Engine;
using UrcConverter.Parser.Osu;
using UrcConverter.Tests.Fixtures;

namespace UrcConverter.Tests.EngineTests;

public sealed class ConverterEngineTests : IDisposable
{
    private readonly OsuFileFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Convert_NoParserRegistered_ReturnsFailure()
    {
        var engine = new ConverterEngine();
        var path = _fixture.CreateTempOsu(OsuFileFixture.Minimal4K);
        var result = engine.Convert(path);

        result.Should().BeOfType<ParseResult.Failure>();
        ((ParseResult.Failure)result).Error.Should().Contain("No parser found");
    }

    [Fact]
    public void Convert_WrongExtension_ReturnsFailure()
    {
        var engine = new ConverterEngine();
        engine.RegisterParser(new OsuParser());
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.bms");
        
        File.WriteAllText(path, "dummy");
        _fixture.CreateTempOsu(""); // just to register cleanup; actual temp file managed manually
        
        try
        {
            var result = engine.Convert(path);

            result.Should().BeOfType<ParseResult.Failure>();
            ((ParseResult.Failure)result).Error.Should().Contain("No parser found");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Convert_FileNotFound_ReturnsFailure()
    {
        var engine = new ConverterEngine();
        engine.RegisterParser(new OsuParser());
        var result = engine.Convert(@"C:\nonexistent\path\file.osu");

        result.Should().BeOfType<ParseResult.Failure>();
        ((ParseResult.Failure)result).Error.Should().Contain("File not found");
    }

    [Fact]
    public void Convert_ValidOsuFile_ReturnsSuccess()
    {
        var engine = new ConverterEngine();
        engine.RegisterParser(new OsuParser());
        var path = _fixture.CreateTempOsu(OsuFileFixture.Minimal4K);

        var result = engine.Convert(path);

        result.Should().BeOfType<ParseResult.Success>();
    }

    [Fact]
    public void UrcFormatVersion_IsNotEmpty()
    {
        UrcFormat.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void UrcFormatVersion_MatchesExpectedValue()
    {
        UrcFormat.Version.Should().Be("1.1");
    }
}