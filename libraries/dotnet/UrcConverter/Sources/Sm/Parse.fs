namespace UrcConverter.Sources.Sm

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

    let private timingTag (timing: Timing) (tag: string) (value: string) : Result<Timing, UrcError> =
        match tag with
        | "OFFSET" -> parseFloat value |> Result.map (fun offset -> { timing with Offset = offset })
        | "BPMS" -> pairs value true |> Result.map (fun entries -> { timing with Bpms = timing.Bpms @ entries })
        | "STOPS"
        | "FREEZES" -> pairs value true |> Result.map (fun entries -> { timing with Stops = timing.Stops @ entries })
        | "DELAYS" -> pairs value true |> Result.map (fun entries -> { timing with Delays = timing.Delays @ entries })
        | "WARPS" -> pairs value false |> Result.map (fun entries -> { timing with Warps = timing.Warps @ entries })
        | "SCROLLS" -> pairs value false |> Result.map (fun entries -> { timing with Scrolls = timing.Scrolls @ entries })
        | "FAKES" ->
            pairs value false
            |> Result.map (fun entries ->
                { timing with Fakes = timing.Fakes @ List.filter (fun (_, length) -> length > 0.0) entries })
        | "TIMESIGNATURES" ->
            expressions value 3
            |> Result.bind (fun list ->
                let rec loop (acc: (float * int * int) list) (rest: string list list) =
                    match rest with
                    | [] -> Ok(List.rev acc)
                    | parts :: tail ->
                        match parseBeat parts[0], parseInt parts[1], parseInt parts[2] with
                        | Error error, _, _
                        | _, Error error, _
                        | _, _, Error error -> Error error
                        | Ok beat, Ok numerator, Ok denominator when
                            numerator >= 1 && denominator >= 1 && beat >= 0.0
                            ->
                            loop ((beat, numerator, denominator) :: acc) tail
                        | Ok _, Ok _, Ok _ -> loop acc tail

                loop [] list)
            |> Result.map (fun entries -> { timing with TimeSignatures = timing.TimeSignatures @ entries })
        | _ -> Ok timing

    // --- public API ---------------------------------------------------------

    /// Parses a .sm or .ssc simfile into its source model.
    let parseSm (text: string) : Result<SmFile, UrcError> =
        let applyTag (state: SmFile * SmChart option) (params_: string list) : Result<SmFile * SmChart option, UrcError> =
            let simfile, chart = state
            let tag = params_.Head.ToUpperInvariant()
            let value = if params_.Length > 1 then params_[1] else ""

            if tag = "NOTEDATA" then
                Ok(simfile, Some SmChart.Empty)
            elif tag = "NOTES" || tag = "NOTES2" then
                match chart with
                | Some openChart ->
                    resolveLanes openChart.StepsType
                    |> Result.bind (fun lanes -> parseNoteData value lanes)
                    |> Result.map (fun notes ->
                        let charts = { openChart with Notes = notes } :: simfile.Charts
                        ({ simfile with Charts = charts }, None))
                | None when params_.Length >= 7 ->
                    let block =
                        { SmChart.Empty with
                            StepsType = params_[1].Trim()
                            Description = params_[2].Trim()
                            Difficulty = params_[3].Trim()
                            Credit = params_[2].Trim()
                        }

                    resolveLanes block.StepsType
                    |> Result.bind (fun lanes -> parseNoteData params_[6] lanes)
                    |> Result.map (fun notes ->
                        { simfile with Charts = { block with Notes = notes } :: simfile.Charts }, None)
                | None -> Ok(simfile, chart)
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
                    | _ ->
                        if timingTags.Contains tag then
                            let baseTiming =
                                match openChart.Timing with
                                | Some timing -> timing
                                | None -> { Timing.Empty with Offset = simfile.Timing.Offset }

                            timingTag baseTiming tag value
                            |> Result.map (fun timing -> simfile, Some { openChart with Timing = Some timing })
                        else
                            Ok(simfile, Some openChart)

        let rec loop state =
            function
            | [] ->
                let simfile, _ = state

                if List.isEmpty simfile.Charts then
                    Error(UrcError.syntax 1 "no chart in simfile")
                else
                    Ok { simfile with Charts = List.rev simfile.Charts }
            | params_ :: rest ->
                match applyTag state params_ with
                | Ok next -> loop next rest
                | Error err -> Error err

        loop (SmFile.Empty, None) (tokenize text)

