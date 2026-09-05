namespace UrcConverter.Sources.Sm

module Parse =

    open System.Globalization
    open UrcConverter
    open UrcConverter.Sources.Sm.Model

    [<Literal>]
    let private RowsPerBeat = 48.0

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

    // --- scalar parsing -----------------------------------------------------

    let private parseFloat (token: string) : Result<float, UrcError> =
        match System.Double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value -> Ok value
        | false, _ -> Error(UrcError.syntax 1 $"invalid number: {token}")

    let private parseBeat (token: string) : Result<float, UrcError> =
        let trimmed = token.TrimEnd()

        if trimmed.EndsWith("r", System.StringComparison.Ordinal)
           || trimmed.EndsWith("R", System.StringComparison.Ordinal) then
            Error(UrcError.syntax 1 $"row-format beats are not supported: {token}")
        else
            parseFloat token

    let private parseInt (token: string) : Result<int, UrcError> =
        match System.Int32.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, value -> Ok value
        | false, _ -> Error(UrcError.syntax 1 $"invalid integer: {token}")

    let private expressions (value: string) (minimum: int) : Result<string list list, UrcError> =
        let rec loop (parts: string list) (acc: string list list) : Result<string list list, UrcError> =
            match parts with
            | [] -> Ok(List.rev acc)
            | expression :: rest ->
                if expression.Trim().Length = 0 then
                    loop rest acc
                else
                    let fields = List.ofArray (expression.Split('='))

                    if fields.Length < minimum then
                        Error(UrcError.syntax 1 $"malformed timing expression: {expression}")
                    else
                        loop rest (fields :: acc)

        loop (List.ofArray (value.Split(','))) []

    let private pairs (value: string) (skipZero: bool) : Result<(float * float) list, UrcError> =
        expressions value 2
        |> Result.bind (fun list ->
            let rec loop (entries: (float * float) list) (rest: string list list) : Result<(float * float) list, UrcError> =
                match rest with
                | [] -> Ok(List.rev entries)
                | parts :: tail when parts.Length <> 2 ->
                    let joined = String.concat "=" parts
                    Error(UrcError.syntax 1 $"malformed timing expression: {joined}")
                | parts :: tail ->
                    match parseBeat parts[0], parseFloat parts[1] with
                    | Error error, _
                    | _, Error error -> Error error
                    | Ok beat, Ok number ->
                        if not skipZero || number <> 0.0 then
                            loop ((beat, number) :: entries) tail
                        else
                            loop entries tail

            loop [] list)

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

    // --- note data ----------------------------------------------------------

    let private rows (beats: float) : int =
        let value = beats * RowsPerBeat

        if value >= 0.0 then
            int (value + 0.5)
        else
            int (value - 0.5)

    let private parseNoteData (data: string) (lanes: int) : Result<SmNote list, UrcError> =
        let notes = ResizeArray<SmNote>()

        let processMeasure (measure: int) (part: string) (openHolds: Map<int, int>) : Result<Map<int, int>, UrcError> =
            let content =
                part.Split('\n')
                |> Array.map (fun raw -> raw.Trim(' ', '\t', '\r'))
                |> Array.filter (fun line -> line.Length > 0)

            let total = float content.Length

            let rec processLines (index: int) (openHolds: Map<int, int>) : Result<Map<int, int>, UrcError> =
                if index >= content.Length then
                    Ok openHolds
                else
                    let line = content[index]
                    let row = rows ((float measure + float index / total) * 4.0)
                    let chars = line.ToCharArray()

                    // Keysound index suffixes ("[3]") are consumed but dropped.
                    let skipKeysound (position: int) =
                        if position < chars.Length && chars[position] = '[' then
                            let rec findBracket (i: int) =
                                if i >= chars.Length || chars[i] = ']' then i else findBracket (i + 1)

                            let end' = findBracket position
                            if end' >= chars.Length then chars.Length else end' + 1
                        else
                            position

                    let rec processChars (track: int) (position: int) (holds: Map<int, int>) : Result<Map<int, int>, UrcError> =
                        if track >= lanes || position >= chars.Length then
                            Ok holds
                        else
                            let char = chars[position]
                            let position = position + 1

                            let addNote kind =
                                notes.Add({ Row = row; Track = track; Kind = kind; TailRow = None })

                            match char with
                            | '1' ->
                                addNote Tap
                                processChars (track + 1) (skipKeysound position) holds
                            | '2'
                            | '4' ->
                                if holds.ContainsKey track then
                                    Error(UrcError.syntax 1 $"overlapping hold head at row {row}")
                                else
                                    addNote (if char = '2' then Hold else Roll)
                                    processChars (track + 1) (skipKeysound position) (holds.Add(track, notes.Count - 1))
                            | '3' ->
                                match holds |> Map.tryFind track with
                                | Some noteIndex ->
                                    let head = notes[noteIndex]
                                    notes[noteIndex] <- { head with TailRow = Some row }
                                    processChars (track + 1) (skipKeysound position) (holds.Remove track)
                                | None -> Error(UrcError.syntax 1 $"hold tail without a head at row {row}")
                            | 'M' ->
                                addNote Mine
                                processChars (track + 1) (skipKeysound position) holds
                            | 'L' ->
                                addNote Lift
                                processChars (track + 1) (skipKeysound position) holds
                            | 'F' ->
                                addNote FakeNote
                                processChars (track + 1) (skipKeysound position) holds
                            | _ ->
                                // Unknown characters are ignored, like StepMania.
                                processChars (track + 1) (skipKeysound position) holds

                    processChars 0 0 openHolds
                    |> Result.bind (fun holds -> processLines (index + 1) holds)

            processLines 0 openHolds

        // Zero-length measure parts are skipped without advancing the index.
        let rec loopMeasures (measure: int) (parts: string list) (openHolds: Map<int, int>) : Result<Map<int, int>, UrcError> =
            match parts with
            | [] -> Ok openHolds
            | part :: tail when part.Length = 0 -> loopMeasures measure tail openHolds
            | part :: tail ->
                processMeasure measure part openHolds
                |> Result.bind (fun holds -> loopMeasures (measure + 1) tail holds)

        loopMeasures 0 (List.ofArray (data.Split(','))) Map.empty
        |> Result.bind (fun openHolds ->
            if not openHolds.IsEmpty then
                Error(UrcError.syntax 1 "hold note without a tail")
            else
                Ok(List.ofSeq notes))

    // --- MSD tokenization ---------------------------------------------------

    /// Splits a simfile into MSD values (#TAG:param:...;) following MsdFile.
    let private tokenize (text: string) : string list list =
        let values = ResizeArray<string list>()
        let params_ = ResizeArray<string>()
        let current = System.Text.StringBuilder()
        let line = System.Text.StringBuilder()
        let mutable reading = false
        let mutable i = 0
        let n = text.Length

        let endParam () =
            params_.Add(current.ToString())
            current.Clear() |> ignore
            line.Clear() |> ignore

        while i < n do
            if i + 1 < n && text[i] = '/' && text[i + 1] = '/' then
                while i < n && text[i] <> '\n' do
                    i <- i + 1
            elif reading && text[i] = '#' then
                let visible = line.ToString().Trim(' ', '\t')

                if visible.Length > 0 then
                    current.Append('#') |> ignore
                    line.Append('#') |> ignore
                    i <- i + 1
                else
                    params_.Add(current.ToString().TrimEnd(' ', '\t', '\r', '\n'))
                    values.Add(List.ofSeq params_)
                    params_.Clear() |> ignore
                    current.Clear() |> ignore
                    line.Clear() |> ignore
                    reading <- false
            elif not reading then
                if text[i] = '#' then
                    reading <- true
                    line.Clear() |> ignore
                    i <- i + 1
                elif text[i] <> '\\' then
                    i <- i + 1
                elif i + 1 < n then
                    i <- i + 2
                else
                    i <- i + 1
            else
                if text[i] = ':' then
                    endParam ()
                elif text[i] = ';' then
                    endParam ()
                    values.Add(List.ofSeq params_)
                    params_.Clear() |> ignore
                    current.Clear() |> ignore
                    line.Clear() |> ignore
                    reading <- false
                elif text[i] = '\\' then
                    i <- i + 1

                    if i < n then
                        current.Append text[i] |> ignore
                        line.Append text[i] |> ignore
                else
                    current.Append text[i] |> ignore
                    line.Append text[i] |> ignore

                if i < n && (text[i] = '\r' || text[i] = '\n') then
                    line.Clear() |> ignore

                i <- i + 1

        if reading then
            params_.Add(current.ToString())

        List.ofSeq values

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
                        let charts = simfile.Charts @ [ { openChart with Notes = notes } ]
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
                        { simfile with Charts = simfile.Charts @ [ { block with Notes = notes } ] }, None)
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

        (Ok(SmFile.Empty, None), tokenize text)
        ||> List.fold (fun state params_ ->
            state
            |> Result.bind (fun current -> applyTag current params_))
        |> Result.bind (fun (simfile, _) ->
            if simfile.Charts.IsEmpty then
                Error(UrcError.syntax 1 "no chart in simfile")
            else
                Ok simfile)
