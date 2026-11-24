namespace UrcConverter.Parser

open FsToolkit.ErrorHandling
open UrcConverter
open UrcConverter.Parser.State

module internal Checks =

    let private requireMetadataField (fields: Map<string, string>) (name: string) (line: int) =
        Map.tryFind name fields
        |> Result.requireSome (UrcError.Rule(4, line, $"Metadata is missing field: {name}"))

    let private checkMetadataComplete (state: ParseState) line : Result<ParseState, UrcError> =
        result {
            let! original = requireMetadataField state.Metadata "Original" line
            let! title = requireMetadataField state.Metadata "Title" line
            let! artist = requireMetadataField state.Metadata "Artist" line
            let! creator = requireMetadataField state.Metadata "Creator" line
            let! version = requireMetadataField state.Metadata "Version" line

            return
                { state with
                    ValidatedMetadata =
                        Some
                            {
                                Original = original
                                Title = title
                                Artist = artist
                                Creator = creator
                                Version = version
                            }
                }
        }

    let private checkJudgment (state: ParseState) line : Result<ParseState, UrcError> =
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
                Ok
                    { state with
                        ValidatedJudgment = Some { Windows = windows; Rates = rates }
                    }

    let private checkLayout (state: ParseState) line : Result<ParseState, UrcError> =
        match state.LayoutType with
        | None -> Error(UrcError.Rule(4, line, "Layout is missing field: Type"))
        | Some(keys, specialKeys) ->
            match state.Special with
            | SpecialField.Unseen ->
                Error(UrcError.Rule(4, line, "Layout is missing field: Special"))
            | SpecialField.NoneExplicit ->
                Ok
                    { state with
                        ValidatedLayout =
                            Some
                                {
                                    Keys = keys
                                    SpecialKeys = specialKeys
                                    SpecialLanes = None
                                }
                    }
            | SpecialField.Lanes lanes ->
                let total = keys + specialKeys

                match lanes |> List.tryFind (fun lane -> lane < 0 || lane >= total) with
                | Some lane ->
                    Error(UrcError.Rule(12, line, $"special lane out of range: {lane}"))
                | None ->
                    if List.length (List.distinct lanes) <> List.length lanes then
                        Error(UrcError.Rule(13, line, "duplicate special lanes"))
                    else
                        Ok
                            { state with
                                ValidatedLayout =
                                    Some
                                        {
                                            Keys = keys
                                            SpecialKeys = specialKeys
                                            SpecialLanes = Some lanes
                                        }
                            }

    let finalizeSection (state: ParseState) section line : Result<ParseState, UrcError> =
        match section with
        | Section.Metadata -> checkMetadataComplete state line
        | Section.Judgment -> checkJudgment state line
        | Section.Layout -> checkLayout state line
        | Section.Timing when List.isEmpty state.TimingPoints ->
            Error(UrcError.Rule(14, line, "first timing point must be at timestamp 0"))
        | Section.UrcHeader
        | Section.Notes
        | Section.Timing -> Ok state
