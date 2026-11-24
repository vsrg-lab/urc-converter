namespace UrcConverter.Sources.Sm

[<RequireQualifiedAccess>]
module Parse =

    open System
    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources.Sm.Model
    open Msd
    open Notes

    let private stepLanes =
        [
            "dance-single", 4
            "dance-double", 8
            "dance-solo", 6
            "dance-threepanel", 3
            "pump-single", 5
            "pump-halfdouble", 6
            "pump-double", 10
            "kb7-single", 7
            "techno-single4", 4
            "techno-single5", 5
            "techno-single8", 8
            "techno-double4", 8
            "techno-double5", 10
            "techno-double8", 16
            "maniax-single", 4
            "maniax-double", 8
            "pnm-five", 5
            "pnm-nine", 9
            "para-single", 5
            "ds3ddx-single", 8
            "ez2-single", 5
            "ez2-double", 10
            "ez2-real", 7
            "kickbox-human", 4
            "kickbox-quadarm", 4
            "kickbox-insect", 6
            "kickbox-arachnid", 8
        ]
        |> Map.ofList

    let private stepAliases = Map.ofList [ "ez2-single-hard", "ez2-single"; "para", "para-single" ]

    let private timingTags =
        Set.ofList
            [
                "OFFSET"
                "BPMS"
                "STOPS"
                "FREEZES"
                "DELAYS"
                "WARPS"
                "SCROLLS"
                "FAKES"
                "TIMESIGNATURES"
            ]

    /// Track count for a steps type; unsupported types are an error.
    let resolveLanes (stepsType: string) : Result<int, UrcError> =
        let name = stepAliases |> Map.tryFind stepsType |> Option.defaultValue stepsType
        let shown = if stepsType = "" then "(missing)" else stepsType

        match stepLanes |> Map.tryFind name with
        | Some lanes -> Ok lanes
        | None -> Error(UrcError.unsupportedVersion 1 $"unsupported steps type: {shown}")

    let private prependReversed (entries: 'a list) (acc: 'a list) =
        List.fold (fun state item -> item :: state) acc entries

    let private finalizeTiming (timing: Timing) : Timing =
        { timing with
            Bpms = List.rev timing.Bpms
            Stops = List.rev timing.Stops
            Delays = List.rev timing.Delays
            Warps = List.rev timing.Warps
            Scrolls = List.rev timing.Scrolls
            TimeSignatures = List.rev timing.TimeSignatures
            Fakes = List.rev timing.Fakes
        }

    let private timingTag (timing: Timing) (tag: string) (value: string) : Result<Timing, UrcError> =
        match tag with
        | "OFFSET" ->
            if String.IsNullOrWhiteSpace value then
                Ok { timing with Offset = 0.0 }
            else
                parseFloat value |> Result.map (fun offset -> { timing with Offset = offset })
        | "BPMS" ->
            pairs value true
            |> Result.map (fun entries -> { timing with Bpms = prependReversed entries timing.Bpms })
        | "STOPS"
        | "FREEZES" ->
            pairs value true
            |> Result.map (fun entries -> { timing with Stops = prependReversed entries timing.Stops })
        | "DELAYS" ->
            pairs value false
            |> Result.map (fun entries ->
                let positive = List.filter (fun (_, seconds) -> seconds > 0.0) entries
                { timing with Delays = prependReversed positive timing.Delays })
        | "WARPS" ->
            pairs value false
            |> Result.map (fun entries -> { timing with Warps = prependReversed entries timing.Warps })
        | "SCROLLS" ->
            pairs value false
            |> Result.map (fun entries -> { timing with Scrolls = prependReversed entries timing.Scrolls })
        | "FAKES" ->
            pairs value false
            |> Result.map (fun entries ->
                let positive = List.filter (fun (_, length) -> length > 0.0) entries
                { timing with Fakes = prependReversed positive timing.Fakes })
        | "TIMESIGNATURES" ->
            expressions value 3
            |> Result.bind (
                List.traverseResultM (fun parts ->
                    match parts with
                    | beatStr :: numStr :: denStr :: _ ->
                        result {
                            let! beat = parseBeat beatStr
                            let! numerator = parseInt numStr
                            let! denominator = parseInt denStr

                            return
                                if numerator >= 1 && denominator >= 1 && beat >= 0.0 then
                                    Some(beat, numerator, denominator)
                                else
                                    None
                        }
                    | _ -> Error(UrcError.syntax 1 "malformed time signature"))
            )
            |> Result.map (fun entries ->
                let valid = List.choose id entries
                { timing with TimeSignatures = prependReversed valid timing.TimeSignatures })
        | _ -> Ok timing

    let private finalizePending
        (simfile: SmFile)
        (pending: (SmChart * string option) option)
        : Result<SmFile, UrcError> =
        match pending with
        | Some(openChart, Some rawNotes) ->
            resolveLanes openChart.StepsType
            |> Result.bind (fun lanes -> parseNoteData rawNotes lanes)
            |> Result.map (fun notes ->
                let finalized =
                    { openChart with
                        Timing = openChart.Timing |> Option.map finalizeTiming
                        Notes = notes
                    }

                { simfile with Charts = finalized :: simfile.Charts })
        | _ -> Ok simfile

    /// Parses a .sm or .ssc simfile into its source model.
    let parse (text: string) : Result<SmFile, UrcError> =
        let applyTag
            (simfile: SmFile, pending: (SmChart * string option) option)
            (params_: string list)
            : Result<SmFile * (SmChart * string option) option, UrcError> =
            match params_ with
            | [] -> Ok(simfile, pending)
            | rawTag :: rest ->
                let tag = rawTag.ToUpperInvariant()
                let value = match rest with | v :: _ -> v | [] -> ""

                if tag = "NOTEDATA" then
                    finalizePending simfile pending
                    |> Result.map (fun nextSim -> nextSim, Some(SmChart.Empty, None))
                elif tag = "NOTES" || tag = "NOTES2" then
                    match pending, rest with
                    | Some(openChart, _), _ when List.length params_ < 7 ->
                        Ok(simfile, Some(openChart, Some value))
                    | _, stepsType :: description :: difficulty :: _ :: _ :: noteData :: _ ->
                        result {
                            let! nextSim = finalizePending simfile pending

                            let block =
                                { SmChart.Empty with
                                    StepsType = stepsType.Trim()
                                    Description = description.Trim()
                                    Difficulty = difficulty.Trim()
                                    Credit = description.Trim()
                                }

                            let! lanes = resolveLanes block.StepsType
                            let! notes = parseNoteData noteData lanes
                            return { nextSim with Charts = { block with Notes = notes } :: nextSim.Charts }, None
                        }
                    | _ -> Ok(simfile, pending)
                else
                    match pending with
                    | None ->
                        match tag with
                        | "TITLE" -> Ok({ simfile with Title = value }, pending)
                        | "SUBTITLE" -> Ok({ simfile with Subtitle = value }, pending)
                        | "ARTIST" -> Ok({ simfile with Artist = value }, pending)
                        | "CREDIT" -> Ok({ simfile with Credit = value }, pending)
                        | _ ->
                            timingTag simfile.Timing tag value
                            |> Result.map (fun timing -> { simfile with Timing = timing }, pending)
                    | Some(openChart, rawNotes) ->
                        match tag with
                        | "STEPSTYPE" -> Ok(simfile, Some({ openChart with StepsType = value.Trim() }, rawNotes))
                        | "DESCRIPTION" -> Ok(simfile, Some({ openChart with Description = value.Trim() }, rawNotes))
                        | "DIFFICULTY" -> Ok(simfile, Some({ openChart with Difficulty = value.Trim() }, rawNotes))
                        | "CHARTNAME" -> Ok(simfile, Some({ openChart with ChartName = value.Trim() }, rawNotes))
                        | "CREDIT" -> Ok(simfile, Some({ openChart with Credit = value }, rawNotes))
                        | _ when timingTags.Contains tag ->
                            if String.IsNullOrWhiteSpace value then
                                Ok(simfile, pending)
                            else
                                let baseTiming =
                                    match openChart.Timing with
                                    | Some timing -> timing
                                    | None -> { Timing.Empty with Offset = simfile.Timing.Offset }

                                timingTag baseTiming tag value
                                |> Result.map (fun timing ->
                                    simfile, Some({ openChart with Timing = Some timing }, rawNotes))
                        | _ -> Ok(simfile, pending)

        let rec loop state remaining =
            match remaining with
            | [] ->
                let simfile, pending = state

                finalizePending simfile pending
                |> Result.bind (fun completed ->
                    if List.isEmpty completed.Charts then
                        Error(UrcError.syntax 1 "no chart in simfile")
                    else
                        Ok
                            { completed with
                                Timing = finalizeTiming completed.Timing
                                Charts = List.rev completed.Charts
                            })
            | params_ :: rest ->
                match applyTag state params_ with
                | Ok next -> loop next rest
                | Error err -> Error err

        loop (SmFile.Empty, None) (tokenize text)
