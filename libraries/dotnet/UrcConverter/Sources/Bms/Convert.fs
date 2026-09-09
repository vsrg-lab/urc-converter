namespace UrcConverter.Sources.Bms

module Convert =

    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Bms.Model
    open Channels

    [<Literal>]
    let private MeasureUs = 240000000.0

    [<RequireQualifiedAccess>]
    type private EntryKind =
        | Bpm of float
        | Meter of int
        | Stop of float
        | Scroll of float
        | Object of int
        | Anchor

    type private ScanAcc =
        {
            Entries: (float * int * EntryKind) list
            Objects: (float * string * string) list
            Used: Set<int * char>
        }

    type private TimingScanState =
        {
            TimeUs: float
            PrevY: float
            PendingStop: float
            CurrentBpm: float option
            CurrentBeats: int
            BpmPoints: (int * float * int * int) list
            SvPoints: (int * float) list
            Anchors: int list
            Timed: Map<int, float>
        }

    let convertBms (chart: BmsChart) : Result<Chart, UrcError> =
        result {
            let! bpmInitial =
                match chart.Bpm with
                | Some bpm when bpm > 0.0 -> Ok bpm
                | _ -> Error(UrcError.Syntax(1, "missing or non-positive #BPM"))

            let maxMeasure =
                chart.Measures
                |> Map.keys
                |> Seq.fold max -1

            let boundaries =
                [| 0 .. maxMeasure |]
                |> Array.scan (fun acc m ->
                    let rate = Map.tryFind m chart.Rates |> Option.defaultValue 1.0
                    acc + rate) 0.0

            let initialAcc =
                {
                    Entries = [ (0.0, 0, EntryKind.Bpm bpmInitial) ]
                    Objects = []
                    Used = Set.empty
                }

            let rec scanMeasures (acc: ScanAcc) (m: int) : Result<ScanAcc, UrcError> =
                if m > maxMeasure then
                    Ok { acc with Objects = List.rev acc.Objects }
                else
                    let rate = Map.tryFind m chart.Rates |> Option.defaultValue 1.0
                    let prevRate = Map.tryFind (m - 1) chart.Rates |> Option.defaultValue 1.0
                    let meterEntries =
                        if rate <> prevRate then
                            let beats = rate * 4.0
                            if abs (beats - round beats) < 1e-9 && round beats >= 1.0 then
                                [ (boundaries[m], 3, EntryKind.Meter (int (round beats))) ]
                            else []
                        else []

                    let accWithMeter = { acc with Entries = meterEntries @ acc.Entries }

                    match Map.tryFind m chart.Measures with
                    | None -> scanMeasures accWithMeter (m + 1)
                    | Some measureMap ->
                        let rec scanChannels (cAcc: ScanAcc) (channels: (string * string list) list) : Result<ScanAcc, UrcError> =
                            match channels with
                            | [] -> Ok cAcc
                            | (channel, ids) :: restChannels ->
                                let rec scanIds (iAcc: ScanAcc) (idx: int) : Result<ScanAcc, UrcError> =
                                    if idx >= ids.Length then
                                        Ok iAcc
                                    else
                                        let obj = ids[idx]
                                        let y = boundaries[m] + (float idx / float ids.Length) * rate
                                        if Set.contains channel systemChannels then
                                            if obj <> "00" then
                                                match channel with
                                                | "03" ->
                                                    let digits = idValue obj chart.Base
                                                    let bpmVal = float ((digits / 36) * 16 + (digits % 36))
                                                    scanIds { iAcc with Entries = (y, 0, EntryKind.Bpm bpmVal) :: iAcc.Entries } (idx + 1)
                                                | "08" ->
                                                    match Map.tryFind obj chart.BpmDefs with
                                                    | Some bpmVal -> scanIds { iAcc with Entries = (y, 0, EntryKind.Bpm bpmVal) :: iAcc.Entries } (idx + 1)
                                                    | None -> Error(UrcError.Syntax(1, $"undefined #BPM{obj}"))
                                                | "09" ->
                                                    match Map.tryFind obj chart.StopDefs with
                                                    | Some stopVal -> scanIds { iAcc with Entries = (y, 1, EntryKind.Stop stopVal) :: iAcc.Entries } (idx + 1)
                                                    | None -> Error(UrcError.Syntax(1, $"undefined #STOP{obj}"))
                                                | _ ->
                                                    match Map.tryFind obj chart.ScrollDefs with
                                                    | Some scrollVal -> scanIds { iAcc with Entries = (y, 2, EntryKind.Scroll scrollVal) :: iAcc.Entries } (idx + 1)
                                                    | None -> Error(UrcError.Syntax(1, $"undefined #SCROLL{obj}"))
                                            else
                                                scanIds iAcc (idx + 1)
                                        else
                                            match channelKind channel with
                                            | None -> scanIds iAcc (idx + 1)
                                            | Some kind ->
                                                let used =
                                                    if obj <> "00" then Set.add (sideOf channel[0], channel[1]) iAcc.Used
                                                    else iAcc.Used
                                                if not (obj = "00" && kind <> "ln") then
                                                    let objIdx = iAcc.Objects.Length
                                                    let objects = (y, channel, obj) :: iAcc.Objects
                                                    let entries = (y, 4, EntryKind.Object objIdx) :: iAcc.Entries
                                                    scanIds { Entries = entries; Objects = objects; Used = used } (idx + 1)
                                                else
                                                    scanIds { iAcc with Used = used } (idx + 1)

                                scanIds cAcc 0
                                |> Result.bind (fun nextAcc -> scanChannels nextAcc restChannels)

                        scanChannels accWithMeter (Map.toList measureMap)
                        |> Result.bind (fun nextAcc -> scanMeasures nextAcc (m + 1))

            let! scanned = scanMeasures initialAcc 0
            let mode = detectMode chart.Pms scanned.Used

            let sortedEntries =
                (boundaries
                 |> Seq.map (fun y -> y, 5, EntryKind.Anchor)
                 |> List.ofSeq)
                @ scanned.Entries
                |> List.sortBy (fun (y, order, _) -> y, order)

            let groupedEntries =
                sortedEntries
                |> List.groupBy (fun (y, _, _) -> y)

            let initialTiming =
                {
                    TimeUs = 0.0
                    PrevY = 0.0
                    PendingStop = 0.0
                    CurrentBpm = None
                    CurrentBeats = 4
                    BpmPoints = []
                    SvPoints = []
                    Anchors = []
                    Timed = Map.empty
                }

            let foldTimingGroup (state: TimingScanState) (y: float, group: (float * int * EntryKind) list) =
                let timeUs =
                    match state.CurrentBpm with
                    | Some b -> state.TimeUs + (MeasureUs * (y - state.PrevY) / b)
                    | None -> state.TimeUs
                    + state.PendingStop

                let rec foldGroupEvents nextBpm nextBeats pendingStop scroll anchors timed (events: (float * int * EntryKind) list) =
                    match events with
                    | [] -> nextBpm, nextBeats, pendingStop, scroll, anchors, timed
                    | (_, _, kind) :: rest ->
                        match kind with
                        | EntryKind.Bpm v -> foldGroupEvents (Some v) nextBeats pendingStop scroll anchors timed rest
                        | EntryKind.Meter v -> foldGroupEvents nextBpm v pendingStop scroll anchors timed rest
                        | EntryKind.Stop v ->
                            let stopTime =
                                match nextBpm with
                                | Some b -> MeasureUs * v / b
                                | None -> 0.0
                            foldGroupEvents nextBpm nextBeats stopTime scroll anchors timed rest
                        | EntryKind.Scroll v -> foldGroupEvents nextBpm nextBeats pendingStop (Some v) anchors timed rest
                        | EntryKind.Object idx -> foldGroupEvents nextBpm nextBeats pendingStop scroll anchors (Map.add idx timeUs timed) rest
                        | EntryKind.Anchor -> foldGroupEvents nextBpm nextBeats pendingStop scroll (Shared.roundMs (timeUs / 1000.0) :: anchors) timed rest

                let nextBpm, nextBeats, pendingStop, scroll, nextAnchors, nextTimed =
                    foldGroupEvents state.CurrentBpm state.CurrentBeats 0.0 None state.Anchors state.Timed group

                let bpmPoints =
                    match nextBpm with
                    | Some bpm when nextBpm <> state.CurrentBpm || nextBeats <> state.CurrentBeats ->
                        (Shared.roundMs (timeUs / 1000.0), bpm, nextBeats, 4) :: state.BpmPoints
                    | _ ->
                        state.BpmPoints

                let svPoints =
                    match scroll with
                    | Some s -> (Shared.roundMs (timeUs / 1000.0), s) :: state.SvPoints
                    | None -> state.SvPoints

                {
                    TimeUs = timeUs
                    PrevY = y
                    PendingStop = pendingStop
                    CurrentBpm = nextBpm
                    CurrentBeats = nextBeats
                    BpmPoints = bpmPoints
                    SvPoints = svPoints
                    Anchors = nextAnchors
                    Timed = nextTimed
                }

            let timingState = (initialTiming, groupedEntries) ||> List.fold foldTimingGroup

            let! rawNotes = buildNotes chart mode scanned.Objects timingState.Timed
            let firstNoteTime =
                rawNotes
                |> List.filter (fun (_, _, t) -> t <> NoteType.LE)
                |> List.map (fun (t, _, _) -> t)
                |> function
                    | [] -> 0
                    | xs -> List.min xs

            let anchorMs = timingState.Anchors |> List.rev |> List.tryFind (fun time -> time >= firstNoteTime)

            let! timing =
                Shared.buildTiming (List.rev timingState.BpmPoints) (List.rev timingState.SvPoints) firstNoteTime ".bms" anchorMs

            let typeOrder = function
                | NoteType.N -> 0
                | NoteType.LS -> 1
                | NoteType.LE -> 2
                | NoteType.M -> 3
                | NoteType.F -> 4

            let urcNotes =
                rawNotes
                |> List.map (fun (t, lane, nt) ->
                    {
                        TimestampMs = t - firstNoteTime
                        Lane = lane
                        Type = nt
                    })
                |> List.sortBy (fun n -> n.TimestampMs, n.Lane, typeOrder n.Type)

            do! Shared.checkHoldOverlap urcNotes

            let keys, specialKeys, specialLanes = resolveLayout mode

            return
                {
                    FormatVersion = { Major = 1; Minor = 1 }
                    Metadata =
                        {
                            Original = if chart.Pms then "PMS" else "BMS"
                            Title = chart.Title |> Option.defaultValue "Unknown"
                            Artist = chart.Artist |> Option.defaultValue "Unknown"
                            Creator = "Unknown"
                            Version = chart.PlayLevel |> Option.defaultValue "Unknown"
                        }
                    Judgment = None
                    Layout =
                        {
                            Keys = keys
                            SpecialKeys = specialKeys
                            SpecialLanes = specialLanes
                        }
                    TimingPoints = timing
                    Notes = urcNotes
                }
        }
