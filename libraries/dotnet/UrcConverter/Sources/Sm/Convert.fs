namespace UrcConverter.Sources.Sm

module Convert =

    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Sm.Model
    open UrcConverter.Sources.Sm.Parse

    [<Literal>]
    let private MeasureRows = 192

    [<Literal>]
    let private FastBpmWarp = 9999999.0

    [<Literal>]
    let private RollTapSpacingMs = 500

    let private typeRank =
        function
        | NoteType.N -> 0
        | NoteType.LS -> 1
        | NoteType.LE -> 2
        | NoteType.M -> 3
        | NoteType.F -> 4

    let private difficultyNames =
        [
            "beginner", "Beginner"
            "easy", "Easy"
            "basic", "Easy"
            "light", "Easy"
            "medium", "Medium"
            "another", "Medium"
            "trick", "Medium"
            "standard", "Medium"
            "difficult", "Medium"
            "hard", "Hard"
            "ssr", "Hard"
            "maniac", "Hard"
            "heavy", "Hard"
            "smaniac", "Challenge"
            "challenge", "Challenge"
            "expert", "Challenge"
            "oni", "Challenge"
            "edit", "Edit"
        ]
        |> Map.ofList

    let private difficultyName (difficulty: string) (description: string) =
        let key = difficulty.Trim().ToLowerInvariant()
        let name = difficultyNames |> Map.tryFind key |> Option.defaultValue "Edit"

        if
            name = "Hard"
            && (let desc = description.Trim().ToLowerInvariant() in desc = "smaniac" || desc = "challenge")
        then
            "Challenge"
        else
            name

    // --- warp handling ------------------------------------------------------

    let private rowsOf (beats: float) : int =
        let value = beats * 48.0

        if value >= 0.0 then
            int (value + 0.5)
        else
            int (value - 0.5)

    /// Merges warp segments into [start, dest) row intervals; overlapping
    /// warps adopt the greater destination (StepMania semantics).
    let private warpIntervals (warpSegs: (float * float) list) : (int * int) list =
        warpSegs
        |> List.map (fun (beat, length) -> let start = rowsOf beat in (start, start + rowsOf length))
        |> List.sortBy fst
        |> List.fold
            (fun (merged: (int * int) list) (start, dest) ->
                match merged with
                | (lastStart, lastDest) :: tail when start < lastDest ->
                    (lastStart, max lastDest dest) :: tail
                | _ -> (start, dest) :: merged)
            []
        |> List.rev

    /// Drops notes strictly inside a warp and truncates tails that end inside
    /// one to the warp start row.
    let private filterNotes (notes: SmNote list) (intervals: (int * int) list) : SmNote list =
        notes
        |> List.filter (fun note ->
            intervals |> List.exists (fun (start, dest) -> start < note.Row && note.Row < dest)
            |> not)
        |> List.map (fun note ->
            match note.TailRow with
            | Some tailRow ->
                let truncated =
                    intervals
                    |> List.tryFind (fun (start, dest) -> start < tailRow && tailRow < dest)

                match truncated with
                | Some (start, _) -> { note with TailRow = Some start }
                | None -> note
            | None -> note)

    // --- legacy negative BPM/stop normalization -----------------------------

    type private PreEvent =
        | BpmEvt of beat: float * bpm: float
        | StopEvt of beat: float * pause: float

    type private PreState =
        {
            Bpm: float
            PrevBeat: float
            TimeOfs: float
            WarpStart: float
            PreWarpBpm: float
            OutBpm: (float * float) list
            OutStop: (float * float) list
            OutWarp: (float * float) list
        }

    /// Port of SMLoader::ProcessBPMsAndStops: converts negative BPMs/stops
    /// into warp segments and folds pre-beat-0 stops into the offset.
    let private preprocess (timing: Timing) : Result<float * (float * float) list * (float * float) list * (float * float) list, UrcError> =
        let bpms = timing.Bpms |> List.sortBy fst
        let sortedStops = timing.Stops |> List.sortBy fst

        let offset, stops =
            sortedStops
            |> List.fold
                (fun (ofs, acc) (beat, pause) ->
                    if beat < 0.0 then ofs - pause, acc
                    else ofs, (beat, pause) :: acc)
                (timing.Offset, [])
            |> fun (ofs, acc) -> ofs, List.rev acc

        let preZeroBpms, postZeroBpms = bpms |> List.partition (fun (beat, _) -> beat <= 0.0)

        let initialBpmResult =
            match preZeroBpms |> List.tryLast |> Option.map snd with
            | Some b when b <> 0.0 -> Ok(b, postZeroBpms)
            | _ ->
                match postZeroBpms with
                | (_, b) :: tail when b <> 0.0 -> Ok(b, tail)
                | _ -> Error(UrcError.syntax 1 "no BPM in simfile")

        initialBpmResult
        |> Result.map (fun (initBpm, remainingBpms) ->
            let events =
                (remainingBpms |> List.map BpmEvt)
                @ (stops |> List.map StopEvt)
                |> List.sortBy (function
                    | BpmEvt(beat, _) -> beat, 0
                    | StopEvt(beat, _) -> beat, 1)

            let initOutBpm = if initBpm > 0.0 && initBpm <= FastBpmWarp then [ (0.0, initBpm) ] else []

            let initialState =
                {
                    Bpm = initBpm
                    PrevBeat = 0.0
                    TimeOfs = 0.0
                    WarpStart = -1.0
                    PreWarpBpm = 0.0
                    OutBpm = initOutBpm
                    OutStop = []
                    OutWarp = []
                }

            let folder (state: PreState) (evt: PreEvent) =
                let beat = match evt with BpmEvt(b, _) | StopEvt(b, _) -> b
                let stateAfterTime =
                    if state.Bpm <= FastBpmWarp then
                        let nextTimeOfs = state.TimeOfs + (beat - state.PrevBeat) * 60.0 / state.Bpm
                        if state.WarpStart >= 0.0 && state.Bpm > 0.0 && nextTimeOfs > 0.0 then
                            let warpEnd = beat - (nextTimeOfs * state.Bpm / 60.0)
                            let nextOutBpm =
                                if state.Bpm <> state.PreWarpBpm then
                                    (state.WarpStart, state.Bpm) :: state.OutBpm
                                else
                                    state.OutBpm
                            { state with
                                TimeOfs = nextTimeOfs
                                PrevBeat = beat
                                WarpStart = -1.0
                                OutBpm = nextOutBpm
                                OutWarp = (state.WarpStart, warpEnd - state.WarpStart) :: state.OutWarp }
                        else
                            { state with TimeOfs = nextTimeOfs; PrevBeat = beat }
                    else
                        { state with PrevBeat = beat }

                match evt with
                | BpmEvt(_, value) ->
                    if stateAfterTime.WarpStart < 0.0 && (value < 0.0 || value > FastBpmWarp) then
                        { stateAfterTime with
                            WarpStart = beat
                            PreWarpBpm = stateAfterTime.Bpm
                            TimeOfs = 0.0
                            Bpm = value }
                    elif stateAfterTime.WarpStart < 0.0 then
                        { stateAfterTime with
                            Bpm = value
                            OutBpm = (beat, value) :: stateAfterTime.OutBpm }
                    else
                        { stateAfterTime with Bpm = value }
                | StopEvt(_, value) ->
                    if stateAfterTime.WarpStart < 0.0 && value < 0.0 then
                        { stateAfterTime with
                            WarpStart = beat
                            PreWarpBpm = stateAfterTime.Bpm
                            TimeOfs = value }
                    elif stateAfterTime.WarpStart < 0.0 then
                        { stateAfterTime with OutStop = (beat, value) :: stateAfterTime.OutStop }
                    else
                        let nextTimeOfs = stateAfterTime.TimeOfs + value
                        if value > 0.0 && nextTimeOfs > 0.0 then
                            let outWarp = (stateAfterTime.WarpStart, beat - stateAfterTime.WarpStart) :: stateAfterTime.OutWarp
                            let outStop = (beat, nextTimeOfs) :: stateAfterTime.OutStop
                            if stateAfterTime.Bpm < 0.0 || stateAfterTime.Bpm > FastBpmWarp then
                                { stateAfterTime with
                                    TimeOfs = 0.0
                                    WarpStart = beat
                                    OutWarp = outWarp
                                    OutStop = outStop }
                            else
                                let outBpm =
                                    if stateAfterTime.Bpm <> stateAfterTime.PreWarpBpm then
                                        (stateAfterTime.WarpStart, stateAfterTime.Bpm) :: stateAfterTime.OutBpm
                                    else
                                        stateAfterTime.OutBpm
                                { stateAfterTime with
                                    TimeOfs = nextTimeOfs
                                    WarpStart = -1.0
                                    OutBpm = outBpm
                                    OutWarp = outWarp
                                    OutStop = outStop }
                        else
                            { stateAfterTime with TimeOfs = nextTimeOfs }

            let finalState = events |> List.fold folder initialState

            let outBpm, outWarp =
                if finalState.WarpStart >= 0.0 then
                    let neverEnds = finalState.Bpm < 0.0 || finalState.Bpm > FastBpmWarp
                    let warpEnd = if neverEnds then 99999999.0 else finalState.PrevBeat - (finalState.TimeOfs * finalState.Bpm / 60.0)
                    let nextOutWarp = (finalState.WarpStart, warpEnd - finalState.WarpStart) :: finalState.OutWarp
                    let nextOutBpm =
                        if finalState.Bpm <> finalState.PreWarpBpm then
                            (finalState.WarpStart, finalState.Bpm) :: finalState.OutBpm
                        else
                            finalState.OutBpm
                    nextOutBpm, nextOutWarp
                else
                    finalState.OutBpm, finalState.OutWarp

            (offset, List.rev outBpm, List.rev finalState.OutStop, List.rev outWarp))

    // --- timing walk --------------------------------------------------------

    type private Entry =
        {
            Row: int
            Priority: int
            Kind: EntryKind
        }

    and private EntryKind =
        | WarpEvent of dest: int
        | DelayEvent of seconds: float
        | NoteEvent of index: int
        | TailEvent of index: int
        | AnchorEvent
        | StopEvent of seconds: float
        | BpmEvent of bpm: float
        | TimeSigEvent of meter: int * int
        | ScrollEvent of ratio: float

    type private WalkState =
        {
            Seconds: float
            Bpm: float option
            Meter: int * int
            Multiplier: float
            Warping: bool
            WarpDest: int
            PrevRow: int option
            HeadTimes: Map<int, float>
            TailTimes: Map<int, float>
            BpmPoints: (int * float * int * int) list
            SvPoints: (int * float) list
            Anchors: int list
        }

    // --- URC note assembly --------------------------------------------------

    type private UrcNote = int * int * NoteType

    let private buildUrcNotes
        (timing: Timing)
        (notes: SmNote list)
        (headTimes: Map<int, float>)
        (tailTimes: Map<int, float>)
        : Result<UrcNote list, UrcError> =
        let fakeRanges =
            timing.Fakes
            |> List.map (fun (beat, length) -> let start = rowsOf beat in (start, start + rowsOf length))

        let rec loop (rest: SmNote list) (index: int) (acc: UrcNote list) : Result<UrcNote list, UrcError> =
            match rest with
            | [] -> Ok(List.rev acc)
            | note :: tail ->
                let headMs =
                    headTimes
                    |> Map.tryFind index
                    |> Option.defaultValue 0.0
                    |> fun sec -> Shared.roundMs (sec * 1000.0)

                let inFake =
                    fakeRanges |> List.exists (fun (start, end') -> start <= note.Row && note.Row < end')

                if inFake then
                    loop tail (index + 1) ((headMs, note.Track, NoteType.F) :: acc)
                else
                    match note.Kind with
                    | Hold ->
                        let tailMs =
                            tailTimes
                            |> Map.tryFind index
                            |> Option.defaultValue 0.0
                            |> fun sec -> Shared.roundMs (sec * 1000.0)

                        if tailMs <= headMs then
                            Error(UrcError.syntax 1 $"hold on lane {note.Track} collapses to zero length")
                        else
                            loop
                                tail
                                (index + 1)
                                ((tailMs, note.Track, NoteType.LE)
                                 :: (headMs, note.Track, NoteType.LS)
                                 :: acc)
                    | Roll ->
                        let endMs =
                            tailTimes
                            |> Map.tryFind index
                            |> Option.defaultValue 0.0
                            |> fun sec -> Shared.roundMs (sec * 1000.0)

                        let taps =
                            [
                                for tapMs in headMs + RollTapSpacingMs .. RollTapSpacingMs .. endMs - 1 ->
                                    (tapMs, note.Track, NoteType.N)
                            ]

                        loop tail (index + 1) ((headMs, note.Track, NoteType.N) :: (List.append taps acc))
                    | Mine -> loop tail (index + 1) ((headMs, note.Track, NoteType.M) :: acc)
                    | FakeNote -> loop tail (index + 1) ((headMs, note.Track, NoteType.F) :: acc)
                    | Tap
                    | Lift -> loop tail (index + 1) ((headMs, note.Track, NoteType.N) :: acc)

        loop notes 0 []

    // --- chart conversion ---------------------------------------------------

    let private convertChart (simfile: SmFile) (chart: SmChart) : Result<Chart, UrcError> =
        result {
            let! lanes = resolveLanes chart.StepsType
            let timing = match chart.Timing with Some timing -> timing | None -> simfile.Timing
            let! offset, bpmSegs, stopSegs, warpSegs = preprocess timing
            let intervals = warpIntervals (warpSegs @ timing.Warps)
            let notes = filterNotes chart.Notes intervals

            let entries =
                [
                    for start, dest in intervals do
                        yield { Row = start; Priority = 0; Kind = WarpEvent dest }

                    for beat, seconds in timing.Delays do
                        yield { Row = rowsOf beat; Priority = 1; Kind = DelayEvent seconds }

                    for index, note in List.indexed notes do
                        yield { Row = note.Row; Priority = 2; Kind = NoteEvent index }

                        match note.TailRow with
                        | Some tailRow -> yield { Row = tailRow; Priority = 2; Kind = TailEvent index }
                        | None -> ()

                    for beat, seconds in stopSegs do
                        yield { Row = rowsOf beat; Priority = 3; Kind = StopEvent seconds }

                    for beat, bpm in bpmSegs do
                        yield { Row = rowsOf beat; Priority = 4; Kind = BpmEvent bpm }

                    for beat, numerator, denominator in timing.TimeSignatures do
                        yield { Row = rowsOf beat; Priority = 5; Kind = TimeSigEvent(numerator, denominator) }

                    for beat, ratio in timing.Scrolls do
                        yield { Row = rowsOf beat; Priority = 6; Kind = ScrollEvent ratio }
                ]

            let maxRow = entries |> List.map (fun entry -> entry.Row) |> List.fold max 0

            let anchorEntries =
                [
                    for row in 0 .. MeasureRows .. maxRow + MeasureRows - 1 ->
                        { Row = row; Priority = 2; Kind = AnchorEvent }
                ]

            let entries =
                anchorEntries @ entries |> List.sortBy (fun entry -> entry.Row, entry.Priority)

            let initial =
                {
                    Seconds = -offset
                    Bpm = None
                    Meter = (4, 4)
                    Multiplier = 1.0
                    Warping = false
                    WarpDest = 0
                    PrevRow = None
                    HeadTimes = Map.empty
                    TailTimes = Map.empty
                    BpmPoints = []
                    SvPoints = []
                    Anchors = []
                }

            let walk (state: WalkState) (row: int, evts: Entry list) : WalkState =
                let advanced =
                    match state.PrevRow, state.Bpm with
                    | Some prevRow, Some bpm when not state.Warping ->
                        {
                            state with
                                Seconds = state.Seconds + float (row - prevRow) / 48.0 * 60.0 / bpm
                        }
                    | _ -> state

                let entered =
                    if advanced.Warping && row >= advanced.WarpDest then
                        { advanced with Warping = false }
                    else
                        advanced

                let grouped =
                    evts
                    |> List.fold
                        (fun (state: WalkState) (entry: Entry) ->
                            match entry.Kind with
                            | WarpEvent dest ->
                                if state.Warping then
                                    { state with WarpDest = max state.WarpDest dest }
                                else
                                    { state with Warping = true; WarpDest = dest }
                            | DelayEvent seconds -> { state with Seconds = state.Seconds + seconds }
                            | NoteEvent index ->
                                { state with HeadTimes = state.HeadTimes.Add(index, state.Seconds) }
                            | TailEvent index -> { state with TailTimes = state.TailTimes.Add(index, state.Seconds) }
                            | AnchorEvent ->
                                { state with Anchors = Shared.roundMs (state.Seconds * 1000.0) :: state.Anchors }
                            | StopEvent seconds -> { state with Seconds = state.Seconds + seconds }
                            | BpmEvent bpm -> { state with Bpm = Some bpm }
                            | TimeSigEvent(numerator, denominator) ->
                                { state with Meter = (numerator, denominator) }
                            | ScrollEvent ratio -> { state with Multiplier = ratio })
                        entered

                let bpmChanged =
                    match grouped.Bpm with
                    | Some bpm when grouped.Bpm <> state.Bpm || grouped.Meter <> state.Meter ->
                        let numerator, denominator = grouped.Meter
                        (Shared.roundMs (grouped.Seconds * 1000.0), bpm, numerator, denominator) :: grouped.BpmPoints
                        |> Some
                    | _ -> None

                let svChanged =
                    if grouped.Multiplier <> state.Multiplier then
                        Some((Shared.roundMs (grouped.Seconds * 1000.0), grouped.Multiplier) :: grouped.SvPoints)
                    else
                        None

                {
                    grouped with
                        BpmPoints = bpmChanged |> Option.defaultValue grouped.BpmPoints
                        SvPoints = svChanged |> Option.defaultValue grouped.SvPoints
                        PrevRow = Some row
                }

            let walked =
                entries
                |> List.groupBy (fun entry -> entry.Row)
                |> List.fold walk initial

            let! urcNotes = buildUrcNotes timing notes walked.HeadTimes walked.TailTimes

            let firstNoteTime =
                match
                    urcNotes
                    |> List.filter (fun (_, _, kind) -> kind <> NoteType.LE)
                    |> List.map (fun (time, _, _) -> time)
                with
                | [] -> 0
                | times -> List.min times

            let anchors = List.rev walked.Anchors

            let! timingPoints =
                Shared.buildTiming
                    walked.BpmPoints
                    walked.SvPoints
                    firstNoteTime
                    ".sm"
                    (anchors |> List.tryFind (fun time -> time >= firstNoteTime))

            let ordered =
                urcNotes
                |> List.sortBy (fun (time, lane, kind) -> time, lane, typeRank kind)
                |> List.map (fun (time, lane, kind) ->
                    {
                        TimestampMs = time - firstNoteTime
                        Lane = lane
                        Type = kind
                    })

            do! Shared.checkHoldOverlap ordered

            let title =
                [ simfile.Title; simfile.Subtitle ]
                |> List.filter (fun part -> part <> "")
                |> String.concat " "

            let creator =
                if chart.Credit <> "" then
                    chart.Credit
                elif simfile.Credit <> "" then
                    simfile.Credit
                else
                    "Unknown"

            let version =
                if chart.ChartName <> "" then
                    chart.ChartName
                else
                    difficultyName chart.Difficulty chart.Description

            return
                {
                    FormatVersion = { Major = 1; Minor = 1 }
                    Metadata =
                        {
                            Original = "StepMania"
                            Title = if title = "" then "Unknown" else title
                            Artist = if simfile.Artist = "" then "Unknown" else simfile.Artist
                            Creator = creator
                            Version = version
                        }
                    Judgment = None
                    Layout = { Keys = lanes; SpecialKeys = 0; SpecialLanes = None }
                    TimingPoints = timingPoints
                    Notes = ordered
                }
        }

    /// Converts every chart of a simfile into URC charts.
    let convertSm (simfile: SmFile) : Result<Chart list, UrcError> =
        simfile.Charts |> List.traverseResultM (convertChart simfile)
