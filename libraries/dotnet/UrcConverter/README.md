# UrcConverter (.NET / F#)

URC 1.x parser, serializer, playback rate scaler, and VSRG chart converter
(`.osu`, `.qua`, `.bms`/`.bme`/`.bml`/`.pms`, `.sm`/`.ssc`, `.ojn`) for .NET.
See the [repository](https://github.com/vsrg-lab/urc-converter) for the format
overview and the other ports (Python, TypeScript, Rust).

```sh
dotnet add package UrcConverter
```

## Parsing, rate scaling, and writing URC

```fsharp
open UrcConverter

match Urc.parse text with
| Ok chart ->
    match Urc.scaleRate chart 1.25 with // 0.50x..3.00x in 0.01 steps
    | Ok scaled ->
        for warning in scaled.Warnings do
            eprintfn "%s: %s" warning.Code warning.Message
        printfn "%s" (Urc.write scaled.Chart)
    | Error err -> eprintfn "%s (%d): %s" err.Category err.Line err.Message
| Error err -> eprintfn "%s (%d): %s" err.Category err.Line err.Message
```

## Converting source charts

```fsharp
open System.IO
open UrcConverter
open UrcConverter.Sources

let osuChart =
    File.ReadAllText("chart.osu")
    |> Osu.Parse.parse
    |> Result.bind Osu.Convert.convert

let quaChart =
    File.ReadAllText("chart.qua")
    |> Qua.Parse.parse
    |> Result.bind Qua.Convert.convert

let bmsChart =
    File.ReadAllBytes("chart.bms")
    |> fun bytes -> Bms.Parse.parse bytes { Bms.Parse.ParseOptions.Default with Seed = Some 42L }
    |> Result.bind Bms.Convert.convert

let smCharts =
    File.ReadAllText("chart.sm")
    |> Sm.Parse.parse
    |> Result.bind Sm.Convert.convert

let ojnCharts =
    File.ReadAllBytes("chart.ojn")
    |> Ojn.Parse.parse
    |> Result.bind Ojn.Convert.convert
```
