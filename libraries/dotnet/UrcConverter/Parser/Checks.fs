namespace UrcConverter.Parser

open UrcConverter
open UrcConverter.Parser.State

module internal Checks =

    let private checkMetadataComplete (state: ParseState) line =
        let missing =
            Strings.metadataFields
            |> List.tryFind (fun name ->
                not (List.exists (fun (key, _) -> key = name) state.Metadata))

        match missing with
        | Some name -> Error(UrcError.Rule(4, line, $"Metadata is missing field: {name}"))
        | None -> Ok()

    let private checkJudgment (state: ParseState) line =
        match state.Windows, state.Rates with
        | None, _
        | _, None -> Error(UrcError.Rule(4, line, "Judgment requires both Window and Rate"))
        | Some windows, Some rates ->
            if List.length windows <> List.length rates then
                Error(UrcError.Rule(7, line, "Window and Rate must have the same count"))
            elif
                windows
                |> List.pairwise
                |> List.exists (fun (earlier, later) -> later < earlier)
            then
                Error(UrcError.Rule(8, line, "Window values must be ascending"))
            elif
                rates |> List.pairwise |> List.exists (fun (earlier, later) -> later > earlier)
            then
                Error(UrcError.Rule(9, line, "Rate values must be descending"))
            elif rates |> List.exists (fun rate -> rate < 0.0 || rate > 100.0) then
                Error(UrcError.Rule(10, line, "Rate values must be in 0-100"))
            else
                Ok()

    let private checkLayout (state: ParseState) line =
        match state.LayoutType with
        | None -> Error(UrcError.Rule(4, line, "Layout is missing field: Type"))
        | Some(keys, specialKeys) ->
            if not state.SpecialSeen then
                Error(UrcError.Rule(4, line, "Layout is missing field: Special"))
            else
                match state.Special with
                | None -> Ok()
                | Some lanes ->
                    let total = keys + specialKeys

                    lanes
                    |> List.tryFind (fun lane -> lane < 0 || lane >= total)
                    |> function
                        | Some lane ->
                            Error(UrcError.Rule(12, line, $"special lane out of range: {lane}"))
                        | None ->
                            if List.length (List.distinct lanes) <> List.length lanes then
                                Error(UrcError.Rule(13, line, "duplicate special lanes"))
                            else
                                Ok()

    let finalizeSection (state: ParseState) section line =
        match section with
        | Section.Metadata -> checkMetadataComplete state line
        | Section.Judgment -> checkJudgment state line
        | Section.Layout -> checkLayout state line
        | Section.Timing when List.isEmpty state.TimingPoints ->
            Error(UrcError.Rule(14, line, "first timing point must be at timestamp 0"))
        | Section.UrcHeader
        | Section.Notes
        | Section.Timing -> Ok()
