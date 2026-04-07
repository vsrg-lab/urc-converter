namespace UrcConverter.Parser.Osu

open UrcConverter.Core.Abstractions
open UrcConverter.Parser.Osu.Internal.Models
open UrcConverter.Parser.Osu.Parsing
open UrcConverter.Parser.Osu.Conversion

type OsuParser() =
    interface IChartParser with
        member _.FormatName = FormatName

        member _.SupportedExtensions = [| ".osu" |]

        member _.ParseToUrc (filePath: string): ParseResult =
            match parseOsuFile filePath with
            | Ok chart  -> ParseResult.Success([| toUrc chart |])
            | Error msg -> ParseResult.Failure msg
