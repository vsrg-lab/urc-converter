namespace UrcConverter.Parser

open FsToolkit.ErrorHandling
open UrcConverter
open UrcConverter.Parser.Lex
open UrcConverter.Parser.State

module internal Fields =

    let private (|NameValue|_|) (text: string) =
        match text.Split(':', 2) with
        | [| name; value |] -> Some(name.Trim(), value)
        | _ -> None

    let metadataField (state: ParseState) (text: string) (line: int) =
        match text with
        | NameValue(name, value) ->
            if not (List.contains name Strings.metadataFields) then
                Error(UrcError.Rule(6, line, $"unknown metadata field: {name}"))
            elif List.exists (fun (key, _) -> key = name) state.Metadata then
                Error(UrcError.Syntax(line, $"duplicate metadata field: {name}"))
            else
                match value.Trim() with
                | "" -> Error(UrcError.Rule(5, line, $"metadata field has no value: {name}"))
                | trimmed ->
                    Ok
                        { state with
                            Metadata = (name, trimmed) :: state.Metadata
                        }
        | _ -> Error(UrcError.Syntax(line, $"expected 'Field: Value', got: '{text}'"))

    let judgmentField (state: ParseState) (text: string) (line: int) =
        match text with
        | NameValue(name, value) ->
            if name = Strings.judgmentFieldWindow then
                match state.Windows with
                | Some _ -> Error(UrcError.Syntax(line, "duplicate judgment field: Window"))
                | None ->
                    floatList value line
                    |> Result.map (fun windows -> { state with Windows = Some windows })
            elif name = Strings.judgmentFieldRate then
                match state.Rates with
                | Some _ -> Error(UrcError.Syntax(line, "duplicate judgment field: Rate"))
                | None ->
                    floatList value line
                    |> Result.map (fun rates -> { state with Rates = Some rates })
            else
                Error(UrcError.Rule(6, line, $"unknown judgment field: {name}"))
        | _ -> Error(UrcError.Syntax(line, $"expected 'Field: values', got: '{text}'"))

    let layoutField (state: ParseState) (text: string) (line: int) =
        match text with
        | NameValue(name, value) ->
            if name = Strings.layoutFieldType then
                match state.LayoutType with
                | Some _ -> Error(UrcError.Syntax(line, "duplicate layout field: Type"))
                | None ->
                    layoutTypeToken (value.Trim()) line
                    |> Result.bind (fun (keys, special) ->
                        match keys, special with
                        | keys, _ when keys < 1 ->
                            Error(UrcError.Syntax(line, "Type values must be positive"))
                        | _, Some specialKeys when specialKeys < 1 ->
                            Error(UrcError.Syntax(line, "Type values must be positive"))
                        | keys, Some specialKeys ->
                            Ok { state with LayoutType = Some(keys, specialKeys) }
                        | keys, None -> Ok { state with LayoutType = Some(keys, 0) })
            elif name = Strings.layoutFieldSpecial then
                if state.SpecialSeen then
                    Error(UrcError.Syntax(line, "duplicate layout field: Special"))
                elif value.Trim() = Strings.specialNone then
                    Ok
                        { state with
                            Special = None
                            SpecialSeen = true
                        }
                else
                    intList "empty lane in Special list" value line
                    |> Result.map (fun lanes ->
                        { state with
                            Special = Some lanes
                            SpecialSeen = true
                        })
            else
                Error(UrcError.Rule(6, line, $"unknown layout field: {name}"))
        | _ -> Error(UrcError.Syntax(line, $"expected 'Field: Value', got: '{text}'"))

    let private addTiming (state: ParseState) timestamp bpm meter multiplier line =
        match state.TimingPoints with
        | [] when timestamp <> 0 ->
            Error(UrcError.Rule(14, line, "first timing point must be at timestamp 0"))
        | latest :: _ when timestamp <= latest.TimestampMs ->
            Error(UrcError.Rule(15, line, "timing timestamps must be strictly ascending"))
        | _ when bpm <= 0.0 -> Error(UrcError.Rule(16, line, "bpm must be positive"))
        | _ ->
            Ok
                { state with
                    TimingPoints =
                        {
                            TimestampMs = timestamp
                            Bpm = bpm
                            Meter = meter
                            Multiplier = multiplier
                        }
                        :: state.TimingPoints
                }

    let timingPoint (state: ParseState) (text: string) (line: int) =
        match text.Split(',') with
        | [| t; b; m |] ->
            result {
                let! timestamp = intToken (t.Trim()) line
                let! bpm = floatToken (b.Trim()) line
                let! meter = meterToken (m.Trim()) line
                return! addTiming state timestamp bpm meter None line
            }
        | [| t; b; m; mult |] ->
            result {
                let! timestamp = intToken (t.Trim()) line
                let! bpm = floatToken (b.Trim()) line
                let! meter = meterToken (m.Trim()) line

                let multiplierText = mult.Trim()
                // A trailing comma leaves an empty 4th field; the spec reads it as "no multiplier".
                let! multiplier =
                    if multiplierText = "" then
                        Ok None
                    else
                        floatToken multiplierText line |> Result.map Some

                return! addTiming state timestamp bpm meter multiplier line
            }
        | fields ->
            Error(
                UrcError.Syntax(line, $"timing point needs 3 or 4 fields, got {fields.Length}")
            )

    let noteLine (state: ParseState) (text: string) (line: int) =
        match text.Split(',') with
        | [| t; l; typeToken |] ->
            result {
                let! timestamp = intToken (t.Trim()) line
                let! lane = intToken (l.Trim()) line

                return
                    { state with
                        Notes =
                            {
                                Timestamp = timestamp
                                Lane = lane
                                TypeToken = typeToken.Trim()
                                Line = line
                            }
                            :: state.Notes
                    }
            }
        | fields -> Error(UrcError.Syntax(line, $"note needs 3 fields, got {fields.Length}"))
