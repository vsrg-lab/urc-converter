namespace UrcConverter.Parser

open System
open System.Globalization
open FsToolkit.ErrorHandling
open UrcConverter

module internal Lex =


    let intToken (token: string) line : Result<int, UrcError> =
        match Int32.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, value -> Ok value
        | false, _ -> Error(UrcError.Syntax(line, $"invalid integer: '{token}'"))

    let floatToken (token: string) line : Result<float, UrcError> =
        match Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value -> Ok value
        | false, _ -> Error(UrcError.Syntax(line, $"invalid float: '{token}'"))

    let meterToken (token: string) line : Result<Meter, UrcError> =
        match token.Split('/') with
        | [| beatsStr; noteValueStr |] ->
            match Int32.TryParse(beatsStr, NumberStyles.None, CultureInfo.InvariantCulture),
                  Int32.TryParse(noteValueStr, NumberStyles.None, CultureInfo.InvariantCulture) with
            | (true, beats), (true, noteValue) when beats >= 1 && noteValue >= 1 ->
                Ok { Beats = beats; NoteValue = noteValue }
            | _ -> Error(UrcError.Rule(17, line, $"invalid meter: '{token}'"))
        | _ -> Error(UrcError.Rule(17, line, $"invalid meter: '{token}'"))

    let layoutTypeToken (token: string) line : Result<int * int option, UrcError> =
        match token.Split('+') with
        | [| keysStr; specialStr |] ->
            match Int32.TryParse(keysStr, NumberStyles.None, CultureInfo.InvariantCulture),
                  Int32.TryParse(specialStr, NumberStyles.None, CultureInfo.InvariantCulture) with
            | (true, keys), (true, special) -> Ok(keys, Some special)
            | _ -> Error(UrcError.Syntax(line, $"invalid Type value: '{token}'"))
        | [| keysStr |] ->
            match Int32.TryParse(keysStr, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, keys -> Ok(keys, None)
            | false, _ -> Error(UrcError.Syntax(line, $"invalid Type value: '{token}'"))
        | _ -> Error(UrcError.Syntax(line, $"invalid Type value: '{token}'"))

    let private commaList emptyMessage parse (value: string) line =
        value.Split(',')
        |> Array.toList
        |> List.traverseResultM (fun token ->
            let token = token.Trim()

            if token = "" then
                Result.Error(UrcError.Syntax(line, emptyMessage))
            else
                parse token line)

    let floatList value line =
        commaList "empty value in list" floatToken value line

    let intList emptyMessage value line =
        commaList emptyMessage intToken value line
