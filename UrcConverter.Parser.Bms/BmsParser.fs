namespace UrcConverter.Parser.Bms

open UrcConverter.Core.Abstractions
open UrcConverter.Parser.Bms.Parsing
open UrcConverter.Parser.Bms.Conversion

type BmsParser() =
    interface IChartParser with
        member _.FormatName = "BMS"

        member _.SupportedExtensions = [| ".bms"; ".bme"; ".bml" |]

        member _.ParseToUrc (filePath: string): ParseResult = 
            match parseBmsFile filePath with
            | Ok chart -> ParseResult.Success(toUrc chart)
            | Error msg -> ParseResult.Failure msg
