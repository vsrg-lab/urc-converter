namespace UrcConverter.Sources.Sm

module Convert =

    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Sm.Model
    open UrcConverter.Sources.Sm.Notes
    open UrcConverter.Sources.Sm.Parse
    open Preprocess

    [<Literal>]
    let private MeasureRows = 192

    let private typeRank =
        function
        | NoteType.N -> 0
        | NoteType.LS -> 1
        | NoteType.LE -> 2
        | NoteType.M -> 3
        | NoteType.F -> 4


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
                                { state with HeadTimes = Map.add index state.Seconds state.HeadTimes }
                            | TailEvent index -> { state with TailTimes = Map.add index state.Seconds state.TailTimes }
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
                    (List.rev walked.BpmPoints)
                    (List.rev walked.SvPoints)
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

            let creator = firstNonEmpty chart.Credit simfile.Credit "Unknown"

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
