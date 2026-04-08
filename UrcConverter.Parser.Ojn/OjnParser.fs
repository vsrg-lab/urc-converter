namespace UrcConverter.Parser.Ojn

open UrcConverter.Core.Abstractions
open UrcConverter.Parser.Ojn.Internal.Models
open UrcConverter.Parser.Ojn.Parsing
open UrcConverter.Parser.Ojn.Conversion

type OjnParser() =
    interface IChartParser with
        member _.FormatName = FormatName

        member _.SupportedExtensions = [| ".ojn" |]

        member _.ParseToUrc (filePath: string): ParseResult = 
            match parseOjnFile filePath with
            | Ok file ->
                let charts =
                    [|
                        for i in 0 .. 2 do
                            if file.Header.NoteCount[i] > 0 then
                                toUrc file i
                    |]
                ParseResult.Success(charts)
            | Error msg ->
                ParseResult.Failure msg