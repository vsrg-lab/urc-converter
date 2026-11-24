namespace UrcConverter

/// Primary entry point for parsing, serializing, and scaling URC charts.
[<RequireQualifiedAccess>]
module Urc =

    /// Parses URC 1.x text into a validated Chart model.
    let parse (text: string) : Result<Chart, UrcError> =
        Parser.Scan.parse text

    /// Serializes a Chart model into canonical URC text.
    let write (chart: Chart) : string =
        Writer.write chart

    /// Scales a chart's playback rate within 0.50x to 3.00x in 0.01 steps.
    let scaleRate (chart: Chart) (rate: float) : Result<RateResult, UrcError> =
        Rate.scaleRate chart rate
