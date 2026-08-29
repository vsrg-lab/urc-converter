# UrcConverter (F#)

URC 1.x parser and writer. See the [repo root](../../README.md) for the format
overview and the other ports (Python, TypeScript, Rust).

```sh
dotnet add package UrcConverter
```

```fsharp
open UrcConverter
open UrcConverter.Parser.Scan
open UrcConverter.Writer

match parse text with
| Ok chart -> printfn "%s" (write chart) // canonical URC text
| Error error -> printfn "%s (%d)" error.Category error.Line
```
