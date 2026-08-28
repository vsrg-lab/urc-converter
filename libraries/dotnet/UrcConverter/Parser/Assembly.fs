namespace UrcConverter.Parser

open UrcConverter
open UrcConverter.Parser.State

module internal Assembly =

    let private tryNoteType token =
        match token with
        | "N" -> Some NoteType.N
        | "LS" -> Some NoteType.LS
        | "LE" -> Some NoteType.LE
        | "M" -> Some NoteType.M
        | "F" -> Some NoteType.F
        | _ -> None

    let rec private validateNotes totalLanes openLs notes raws =
        match raws with
        | [] -> Ok(openLs, notes)
        | raw :: rest ->
            if raw.Timestamp < 0 then
                Error(UrcError.Rule(22, raw.Line, "note timestamps must be non-negative"))
            elif raw.Lane < 0 || raw.Lane >= totalLanes then
                Error(UrcError.Rule(18, raw.Line, $"lane out of range: {raw.Lane}"))
            else
                match tryNoteType raw.TypeToken with
                | None ->
                    Error(UrcError.Rule(19, raw.Line, $"unknown note type: '{raw.TypeToken}'"))
                | Some noteType ->
                    let note =
                        {
                            TimestampMs = raw.Timestamp
                            Lane = raw.Lane
                            Type = noteType
                        }

                    match noteType with
                    | NoteType.LE ->
                        if Map.containsKey raw.Lane openLs then
                            validateNotes
                                totalLanes
                                (Map.remove raw.Lane openLs)
                                (note :: notes)
                                rest
                        else
                            Error(
                                UrcError.Rule(
                                    20,
                                    raw.Line,
                                    $"LE without an open LS on lane {raw.Lane}"
                                )
                            )
                    | NoteType.LS ->
                        if Map.containsKey raw.Lane openLs then
                            Error(
                                UrcError.Rule(
                                    21,
                                    raw.Line,
                                    $"overlapping long notes on lane {raw.Lane}"
                                )
                            )
                        else
                            validateNotes
                                totalLanes
                                (Map.add raw.Lane raw.Line openLs)
                                (note :: notes)
                                rest
                    | _ -> validateNotes totalLanes openLs (note :: notes) rest

    let build (state: ParseState) endLine =
        let missingSection =
            Strings.requiredSections
            |> List.tryFind (fun name -> not (Set.contains name state.Seen))

        match missingSection with
        | Some name -> Error(UrcError.Rule(2, endLine, $"missing required section: {name}"))
        | None ->
            let keys, specialKeys =
                match state.LayoutType with
                | Some(keys, specialKeys) -> keys, specialKeys
                | None -> invalidOp "Layout Type must exist after rule 4 validation"

            let ordered =
                state.Notes |> List.rev |> List.sortBy (fun raw -> raw.Timestamp, raw.Lane)

            match validateNotes (keys + specialKeys) Map.empty [] ordered with
            | Error error -> Error error
            | Ok(openLs, revNotes) ->
                match Map.toList openLs with
                | (lane, lsLine) :: _ ->
                    Error(UrcError.Rule(20, lsLine, $"unterminated LS on lane {lane}"))
                | [] ->
                    let judgment =
                        match state.Windows, state.Rates with
                        | None, _ -> None
                        | Some windows, Some rates -> Some { Windows = windows; Rates = rates }
                        | Some _, None ->
                            invalidOp "Judgment Rate must exist after rule 4 validation"

                    let metadataValue name =
                        match List.tryFind (fun (key, _) -> key = name) state.Metadata with
                        | Some(_, value) -> value
                        | None ->
                            invalidOp $"Metadata field '{name}' must exist after rule 4 validation"

                    Ok
                        {
                            FormatVersion = state.Version
                            Metadata =
                                {
                                    Original = metadataValue "Original"
                                    Title = metadataValue "Title"
                                    Artist = metadataValue "Artist"
                                    Creator = metadataValue "Creator"
                                    Version = metadataValue "Version"
                                }
                            Judgment = judgment
                            Layout =
                                {
                                    Keys = keys
                                    SpecialKeys = specialKeys
                                    SpecialLanes = state.Special
                                }
                            TimingPoints = List.rev state.TimingPoints
                            Notes = List.rev revNotes
                        }
