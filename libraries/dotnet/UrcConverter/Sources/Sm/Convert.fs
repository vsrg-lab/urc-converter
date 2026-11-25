namespace UrcConverter.Sources.Sm

[<RequireQualifiedAccess>]
module Convert =

    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Sm.Model
    open UrcConverter.Sources.Sm.Notes
    open Preprocess

    [<Literal>]
    let private MeasureRows = 192L

    type private Entry =
        {
            Row: int64
            Priority: int
            Kind: EntryKind
        }

    and private EntryKind =
        | WarpEvent of dest: int64
        | WarpEndEvent
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
            WarpDest: int64 option
            PrevRow: int64 option
            BpmPoints: (int * float * int * int) list
            SvPoints: (int * float) list
            Anchors: int list
        }

    let private convertChart (simfile: SmFile) (chart: SmChart) : Result<Chart, UrcError> =
        result {
            let! lanes = Parse.resolveLanes chart.StepsType

            let timing =
                match chart.Timing with
                | None -> simfile.Timing
                | Some ct when List.isEmpty ct.Bpms -> { ct with Bpms = simfile.Timing.Bpms }
                | Some ct -> ct

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
                        yield { Row = int64 note.Row; Priority = 2; Kind = NoteEvent index }

                        match note.TailRow with
                        | Some tailRow -> yield { Row = int64 tailRow; Priority = 2; Kind = TailEvent index }
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

            let anchorEntries =
                match notes with
                | [] -> [ { Row = 0L; Priority = 2; Kind = AnchorEvent } ]
                | _ ->
                    let firstNoteRow = notes |> List.map (fun note -> int64 note.Row) |> List.min
                    let floorMeasure = (max firstNoteRow 0L / MeasureRows) * MeasureRows

                    if firstNoteRow > floorMeasure then
                        [
                            { Row = floorMeasure; Priority = 2; Kind = AnchorEvent }
                            { Row = floorMeasure + MeasureRows; Priority = 2; Kind = AnchorEvent }
                        ]
                    else
                        [ { Row = floorMeasure; Priority = 2; Kind = AnchorEvent } ]

            let baseEntries = entries @ anchorEntries
            let maxRow = baseEntries |> List.fold (fun acc entry -> max acc entry.Row) 0L

            let warpEndEntries =
                [
                    for _, dest in intervals do
                        if dest <= maxRow then
                            yield { Row = dest; Priority = 0; Kind = WarpEndEvent }
                ]

            let entries =
                baseEntries @ warpEndEntries
                |> List.sortBy (fun entry -> entry.Row, entry.Priority)

            let headTimes = Array.zeroCreate<float> notes.Length
            let tailTimes = Array.zeroCreate<float> notes.Length

            let initial =
                {
                    Seconds = -offset
                    Bpm = None
                    Meter = (4, 4)
                    Multiplier = 1.0
                    WarpDest = None
                    PrevRow = None
                    BpmPoints = []
                    SvPoints = []
                    Anchors = []
                }

            let walk (state: WalkState) (row: int64, evts: Entry list) : WalkState =
                let advanced =
                    match state.PrevRow, state.Bpm, state.WarpDest with
                    | Some prevRow, Some bpm, None ->
                        { state with
                            Seconds = state.Seconds + float (row - prevRow) / 48.0 * 60.0 / bpm
                        }
                    | _ -> state

                let entered =
                    match advanced.WarpDest with
                    | Some dest when row >= dest -> { advanced with WarpDest = None }
                    | _ -> advanced

                let grouped =
                    evts
                    |> List.fold
                        (fun (s: WalkState) (entry: Entry) ->
                            match entry.Kind with
                            | WarpEvent dest ->
                                let nextDest =
                                    match s.WarpDest with
                                    | Some current -> max current dest
                                    | None -> dest

                                { s with WarpDest = Some nextDest }
                            | WarpEndEvent -> s
                            | DelayEvent seconds -> { s with Seconds = s.Seconds + seconds }
                            | NoteEvent index ->
                                headTimes[index] <- s.Seconds
                                s
                            | TailEvent index ->
                                tailTimes[index] <- s.Seconds
                                s
                            | AnchorEvent ->
                                { s with Anchors = Shared.roundMs (s.Seconds * 1000.0) :: s.Anchors }
                            | StopEvent seconds -> { s with Seconds = s.Seconds + seconds }
                            | BpmEvent bpm -> { s with Bpm = Some bpm }
                            | TimeSigEvent(numerator, denominator) ->
                                { s with Meter = (numerator, denominator) }
                            | ScrollEvent ratio -> { s with Multiplier = ratio })
                        entered

                let bpmPoints =
                    match grouped.Bpm with
                    | Some bpm when grouped.Bpm <> state.Bpm || grouped.Meter <> state.Meter ->
                        let numerator, denominator = grouped.Meter
                        (Shared.roundMs (grouped.Seconds * 1000.0), bpm, numerator, denominator) :: grouped.BpmPoints
                    | _ -> grouped.BpmPoints

                let svPoints =
                    if grouped.Multiplier <> state.Multiplier then
                        (Shared.roundMs (grouped.Seconds * 1000.0), grouped.Multiplier) :: grouped.SvPoints
                    else
                        grouped.SvPoints

                { grouped with
                    BpmPoints = bpmPoints
                    SvPoints = svPoints
                    PrevRow = Some row
                }

            let walked = entries |> List.groupBy _.Row |> List.fold walk initial

            let! urcNotes = buildUrcNotes timing notes headTimes tailTimes

            let firstNoteTime =
                urcNotes
                |> List.choose (fun (time, _, kind) -> if kind <> NoteType.LE then Some time else None)
                |> function
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
                |> List.sortBy (fun (time, lane, kind) -> time, lane, kind)
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
    let convert (simfile: SmFile) : Result<Chart list, UrcError> =
        simfile.Charts |> List.traverseResultM (convertChart simfile)
