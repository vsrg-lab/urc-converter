namespace UrcConverter.Parser

open FsToolkit.ErrorHandling
open FParsec
open UrcConverter
open UrcConverter.Parser.State

module Scan =

    let private pVersion: Parser<Version, unit> =
        pstring "@URC "
        >>. pipe2 Lex.pUnsigned (pchar '.' >>. Lex.pUnsigned) (fun major minor ->
            { Major = major; Minor = minor })

    let private (|Skip|SectionName|Content|) (text: string) =
        if text.Length = 0 || text[0] = '#' then Skip
        elif text[0] = '@' then SectionName text
        else Content text

    let private handleContent section (state: ParseState) text line =
        match section with
        | Section.UrcHeader ->
            Result.Error(UrcError.Syntax(line, "unexpected content after @URC header"))
        | Section.Metadata -> Fields.metadataField state text line
        | Section.Judgment -> Fields.judgmentField state text line
        | Section.Layout -> Fields.layoutField state text line
        | Section.Timing -> Fields.timingPoint state text line
        | Section.Notes -> Fields.noteLine state text line

    let private header (text: string) (line: int) (state: ParseState) =
        if text.Length < 4 || text[0..3] <> "@URC" then
            Result.Error(UrcError.Rule(1, line, "first line must be '@URC <version>'"))
        else
            match run (pVersion .>> eof) text with
            | Success(version, _, _) ->
                if version.Major <> 1 || version.Minor > 1 then
                    Result.Error(
                        UrcError.UnsupportedVersion(
                            line,
                            $"unsupported version: {version.Major}.{version.Minor}"
                        )
                    )
                else
                    Result.Ok { state with Version = version }
            | Failure _ -> Result.Error(UrcError.Syntax(line, $"malformed @URC header: '{text}'"))

    let private sectionHeader (name: string) (line: int) (state: ParseState) =
        match Map.tryFind name byName with
        | None -> Result.Error(UrcError.Syntax(line, $"unknown section: {name}"))
        | Some(section, index) ->
            if Set.contains name state.Seen then
                Result.Error(UrcError.Rule(3, line, $"duplicate section: {name}"))
            elif index <= state.LastIndex then
                Result.Error(UrcError.Rule(3, line, $"section out of order: {name}"))
            else
                Result.Ok(
                    { state with
                        Seen = Set.add name state.Seen
                        LastIndex = index
                    },
                    section
                )

    let private scanStep line text rest state current =
        result {
            if line = 1 then
                let! nextState = header text line state
                return rest, nextState, current
            else
                match text with
                | Skip -> return rest, state, current
                | SectionName name ->
                    do! Checks.finalizeSection state current line
                    let! nextState, nextSection = sectionHeader name line state
                    return rest, nextState, nextSection
                | Content field ->
                    let! nextState = handleContent current state field line
                    return rest, nextState, current
        }

    let rec private scanLoop (lines: (int * string) list) state current =
        match lines with
        | [] -> Result.Ok(state, current)
        | (line, raw) :: rest ->
            match scanStep line (raw.Trim()) rest state current with
            | Result.Ok(nextLines, nextState, nextCurrent) -> scanLoop nextLines nextState nextCurrent
            | Result.Error error -> Result.Error error

    let parse (text: string) : Result<Chart, UrcError> =
        let text =
            match text with
            | t when t.Length > 0 && t[0] = '\uFEFF' -> t[1..]
            | t -> t

        let lines =
            text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n') |> Array.toList

        let endLine = List.length lines + 1

        let numbered =
            lines |> List.indexed |> List.map (fun (index, line) -> index + 1, line)

        match scanLoop numbered initial Section.UrcHeader with
        | Result.Ok(state, current) ->
            Checks.finalizeSection state current endLine
            |> Result.bind (fun () -> Assembly.build state endLine)
        | Result.Error error -> Result.Error error
