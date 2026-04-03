namespace UrcConverter.Core.Models;

public record UrcChart(
    string FormatVersion,
    UrcMetadata Metadata,
    UrcLayout Layout,
    IReadOnlyList<UrcTiming> Timings,
    IReadOnlyList<UrcNote> Notes,
    UrcJudgment? Judgement = null
);
