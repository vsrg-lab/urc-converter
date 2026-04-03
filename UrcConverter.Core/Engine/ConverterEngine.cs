using UrcConverter.Core.Abstractions;

namespace UrcConverter.Core.Engine;

public class ConverterEngine
{
    private readonly List<IChartParser> _parsers = [];

    public void RegisterParser(IChartParser parser) => _parsers.Add(parser);

    public ParseResult Convert(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var parser = _parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(ext));

        return parser is null
            ? new ParseResult.Failure($"No parser found for extension: {ext}")
            : parser.ParseToUrc(filePath);
    }
}
