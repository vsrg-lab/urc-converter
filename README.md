# URC Converter

A library and CLI tool that converts various VSRG (Vertical Scrolling Rhythm Game) chart formats into the [URC (Universal Rhythm Chart)](https://github.com/vsrg-lab/urc-spec) intermediate format.

## Purpose

URC Converter serves as the preprocessing stage of a VSRG difficulty prediction ML pipeline. It normalizes game-specific chart formats into a single common representation so that downstream tools (feature extractors, ML models) only need to understand one format.

```
osu! (.osu)     ─┐
BMS  (.bms)     ─┤
StepMania (.sm) ─┼→  URC (.urc)  →  [ML Pipeline]
Quaver (.qua)   ─┤
O2Jam (.ojn)    ─┘
```

## Supported Formats

| Format | Extensions |
|--------|-----------|
| osu!mania | `.osu` |
| BMS | `.bms` `.bme` `.bml` |
| StepMania | `.sm` `.ssc` |
| Quaver | `.qua` |
| O2Jam | `.ojn` |


## Build & Run

### Requirements

- .NET 10.0 SDK

### Build

```bash
dotnet build
```

### Test

```bash
dotnet test
```

### CLI Usage

```bash
# Single file conversion
dotnet run --project UrcConverter.Cli -- convert chart.osu
dotnet run --project UrcConverter.Cli -- convert chart.bms -o ./output

# Batch conversion
dotnet run --project UrcConverter.Cli -- batch ./charts
dotnet run --project UrcConverter.Cli -- batch ./charts -o ./output -r
```

## Library Usage

```csharp
using UrcConverter.Core.Abstractions;
using UrcConverter.Core.Engine;
using UrcConverter.Core.Writer;
using UrcConverter.Parser.Osu;
using UrcConverter.Parser.Bms;

var engine = new ConverterEngine();
engine.RegisterParser(new OsuParser());
engine.RegisterParser(new BmsParser());

var result = engine.Convert("chart.osu");

if (result is ParseResult.Success success)
{
    foreach (var chart in success.Charts)
        UrcWriter.WriteToFile(chart, $"{chart.Metadata.Version}.urc");
}
```

## Related Projects

- [urc-spec](https://github.com/vsrg-lab/urc-spec) — URC format specification

## License

MIT License
