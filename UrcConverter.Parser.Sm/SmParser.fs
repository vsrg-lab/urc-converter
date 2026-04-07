namespace UrcConverter.Parser.Sm

open UrcConverter.Core.Abstractions
open UrcConverter.Parser.Sm.Internal.Models
open UrcConverter.Parser.Sm.Parsing
open UrcConverter.Parser.Sm.Conversion

type SmParser() =
    interface IChartParser with
        member _.FormatName = FormatName

        member _.SupportedExtensions = [| ".sm"; ".ssc" |]

        member _.ParseToUrc (filePath: string): ParseResult = 
            match parseSmFile filePath with
            | Ok file ->
                let charts = 
                    file.Charts
                    |> List.map (toUrc file)
                    |> Array.ofList
                ParseResult.Success(charts)
            | Error msg ->
                ParseResult.Failure msg