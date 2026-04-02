namespace UrcConverter.Core.Abstractions;

public interface IChartParser
{
    string FormatName { get; }
    string[] SupportedExtensions { get; }

    ParseResult ParseToUrc(string filePath);
}
