namespace UrcConverter.Sources.Sm

[<RequireQualifiedAccess>]
module Parse =

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
        | "OFFSET" -> parseFloat value |> Result.map (fun offset -> { timing with Offset = offset })
        | "BPMS" ->
            pairs value true
            |> Result.map (fun entries -> { timing with Bpms = prependReversed entries timing.Bpms })
        | "STOPS"
        | "FREEZES" ->
            pairs value true
            |> Result.map (fun entries -> { timing with Stops = prependReversed entries timing.Stops })
        | "DELAYS" ->
            pairs value true
            |> Result.map (fun entries -> { timing with Delays = prependReversed entries timing.Delays })
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

    /// Parses a .sm or .ssc simfile into its source model.
    let parse (text: string) : Result<SmFile, UrcError> =
        let applyTag (simfile: SmFile, chart: SmChart option) (params_: string list) : Result<SmFile * SmChart option, UrcError> =
            match params_ with
            | [] -> Ok(simfile, chart)
            | rawTag :: rest ->
                let tag = rawTag.ToUpperInvariant()
                let value = match rest with | v :: _ -> v | [] -> ""

                if tag = "NOTEDATA" then
                    Ok(simfile, Some SmChart.Empty)
                elif tag = "NOTES" || tag = "NOTES2" then
                    match chart, rest with
                    | Some openChart, _ ->
                        resolveLanes openChart.StepsType
                        |> Result.bind (fun lanes -> parseNoteData value lanes)
                        |> Result.map (fun notes ->
                            let finalized =
                                { openChart with
                                    Timing = openChart.Timing |> Option.map finalizeTiming
                                    Notes = notes
                                }

                            { simfile with Charts = finalized :: simfile.Charts }, None)
                    | None, stepsType :: description :: difficulty :: _ :: _ :: noteData :: _ ->
                        let block =
                            { SmChart.Empty with
                                StepsType = stepsType.Trim()
                                Description = description.Trim()
                                Difficulty = difficulty.Trim()
                                Credit = description.Trim()
                            }

                        resolveLanes block.StepsType
                        |> Result.bind (fun lanes -> parseNoteData noteData lanes)
                        |> Result.map (fun notes ->
                            { simfile with Charts = { block with Notes = notes } :: simfile.Charts }, None)
                    | None, _ -> Ok(simfile, chart)
                else
                    match chart with
                    | None ->
                        match tag with
                        | "TITLE" -> Ok({ simfile with Title = value }, chart)
                        | "SUBTITLE" -> Ok({ simfile with Subtitle = value }, chart)
                        | "ARTIST" -> Ok({ simfile with Artist = value }, chart)
                        | "CREDIT" -> Ok({ simfile with Credit = value }, chart)
                        | _ ->
                            timingTag simfile.Timing tag value
                            |> Result.map (fun timing -> { simfile with Timing = timing }, chart)
                    | Some openChart ->
                        match tag with
                        | "STEPSTYPE" -> Ok(simfile, Some { openChart with StepsType = value.Trim() })
                        | "DESCRIPTION" -> Ok(simfile, Some { openChart with Description = value.Trim() })
                        | "DIFFICULTY" -> Ok(simfile, Some { openChart with Difficulty = value.Trim() })
                        | "CHARTNAME" -> Ok(simfile, Some { openChart with ChartName = value.Trim() })
                        | "CREDIT" -> Ok(simfile, Some { openChart with Credit = value })
                        | _ when timingTags.Contains tag ->
                            let baseTiming =
                                match openChart.Timing with
                                | Some timing -> timing
                                | None -> { Timing.Empty with Offset = simfile.Timing.Offset }

                            timingTag baseTiming tag value
                            |> Result.map (fun timing -> simfile, Some { openChart with Timing = Some timing })
                        | _ -> Ok(simfile, Some openChart)

        let rec loop state remaining =
            match remaining with
            | [] ->
                let simfile, _ = state

                if List.isEmpty simfile.Charts then
                    Error(UrcError.syntax 1 "no chart in simfile")
                else
                    Ok
                        { simfile with
                            Timing = finalizeTiming simfile.Timing
                            Charts = List.rev simfile.Charts
                        }
            | params_ :: rest ->
                match applyTag state params_ with
                | Ok next -> loop next rest
                | Error err -> Error err

        loop (SmFile.Empty, None) (tokenize text)
