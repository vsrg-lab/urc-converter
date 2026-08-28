namespace UrcConverter.Parser

open System
open System.Globalization
open FParsec
open FsToolkit.ErrorHandling
open UrcConverter

module internal Lex =

    let pUnsigned: Parser<int, unit> =
        many1 digit
        >>= fun digits ->
            match Int32.TryParse(String(Array.ofList digits)) with
            | true, value -> preturn value
            | false, _ -> fail "integer out of range"

    let private pInt: Parser<int, unit> =
        pipe2 (opt (pchar '-')) pUnsigned (fun sign value -> if sign.IsSome then -value else value)

    let private pFloat: Parser<float, unit> =
        pipe2 pInt (opt (pchar '.' >>. many1 digit)) (fun whole fraction ->
            match fraction with
            | None -> float whole
            | Some digits ->
                Double.Parse(
                    $"{whole}.{String(Array.ofList digits)}",
                    CultureInfo.InvariantCulture
                ))

    let private pMeter: Parser<Meter, unit> =
        pUnsigned
        .>>. (pchar '/' >>. pUnsigned)
        >>= fun (beats, noteValue) ->
            if beats < 1 || noteValue < 1 then fail "meter components must be positive"
            else preturn { Beats = beats; NoteValue = noteValue }

    let private pLayoutType: Parser<int * int option, unit> =
        pipe2 pUnsigned (opt (pchar '+' >>. pUnsigned)) (fun keys special -> keys, special)

    let private tokenValue parser category token line message : Result<'a, UrcError> =
        match run (parser .>> eof) token with
        | Success(value, _, _) -> Result.Ok value
        | Failure _ ->
            Result.Error
                {
                    Category = category
                    Line = line
                    Message = message
                }

    let intToken token line =
        tokenValue pInt Strings.syntax token line $"invalid integer: '{token}'"

    let floatToken token line =
        tokenValue pFloat Strings.syntax token line $"invalid float: '{token}'"

    let meterToken token line =
        tokenValue pMeter (Strings.rule 17) token line $"invalid meter: '{token}'"

    let layoutTypeToken token line =
        tokenValue pLayoutType Strings.syntax token line $"invalid Type value: '{token}'"

    let private commaList emptyMessage parse (value: string) line =
        value.Split(',')
        |> Seq.traverseResultM (fun token ->
            let token = token.Trim()

            if token = "" then
                Result.Error(UrcError.Syntax(line, emptyMessage))
            else
                parse token line)
        |> Result.map List.ofSeq

    let floatList value line =
        commaList "empty value in list" floatToken value line

    let intList emptyMessage value line =
        commaList emptyMessage intToken value line
