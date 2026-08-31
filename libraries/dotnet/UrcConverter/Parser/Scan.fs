namespace UrcConverter.Parser

open System.Globalization
open System.IO
open FsToolkit.ErrorHandling
open UrcConverter
open UrcConverter.Parser.State

module Scan =

    let private parseVersion (text: string) : Version option =
        if text.StartsWith("@URC ") then
            let ver = text.Substring(5).Trim()
            let parts = ver.Split('.')
            if parts.Length = 2 then
                match System.Int32.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture),
                      System.Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture) with
                | (true, major), (true, minor) when major >= 0 && minor >= 0 ->
                    Some { Major = major; Minor = minor }
                | _ -> None
            else
                None
        else
            None

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
            match parseVersion text with
            | Some version ->
                if version.Major <> 1 || version.Minor > 1 then
                    Result.Error(
                        UrcError.UnsupportedVersion(
                            line,
                            $"unsupported version: {version.Major}.{version.Minor}"
                        )
                    )
                else
                    Result.Ok { state with Version = version }
            | None -> Result.Error(UrcError.Syntax(line, $"malformed @URC header: '{text}'"))

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

    let parse (text: string) : Result<Chart, UrcError> =
        let text =
            if text.Length > 0 && text[0] = '\uFEFF' then text.Substring(1) else text

        use reader = new StringReader(text)

        let rec loop lineNumber state current =
            let raw = reader.ReadLine()
            if isNull raw then
                Result.Ok(lineNumber, state, current)
            else
                let trimmed = raw.Trim()
                if lineNumber = 1 then
                    match header trimmed 1 state with
                    | Ok nextState -> loop 2 nextState current
                    | Error err -> Error err
                else
                    match trimmed with
                    | Skip -> loop (lineNumber + 1) state current
                    | SectionName name ->
                        match Checks.finalizeSection state current lineNumber with
                        | Error err -> Error err
                        | Ok () ->
                            match sectionHeader name lineNumber state with
                            | Ok(nextState, nextSection) -> loop (lineNumber + 1) nextState nextSection
                            | Error err -> Error err
                    | Content field ->
                        match handleContent current state field lineNumber with
                        | Ok nextState -> loop (lineNumber + 1) nextState current
                        | Error err -> Error err

        match loop 1 initial Section.UrcHeader with
        | Ok(endLine, state, current) ->
            Checks.finalizeSection state current endLine
            |> Result.bind (fun () -> Assembly.build state endLine)
        | Error error -> Error error
