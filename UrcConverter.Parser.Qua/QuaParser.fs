namespace UrcConverter.Parser.Qua

open UrcConverter.Core.Abstractions
open UrcConverter.Parser.Qua.Internal.Models
open UrcConverter.Parser.Qua.Parsing
open UrcConverter.Parser.Qua.Conversion

type QuaParser() =
    interface IChartParser with
        member _.FormatName = FormatName

        member _.SupportedExtensions = [| ".qua" |]

        member _.ParseToUrc (filePath: string): ParseResult = 
            match parseQuaFile filePath with
            | Ok chart -> ParseResult.Success([| toUrc chart |])
            | Error msg -> ParseResult.Failure msg