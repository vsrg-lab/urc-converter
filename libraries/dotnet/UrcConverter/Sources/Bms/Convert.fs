namespace UrcConverter.Sources.Bms

[<RequireQualifiedAccess>]
module Convert =

    open System
    open System.Globalization
    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Bms.Model
    open Channels

    [<Literal>]
    let private MeasureUs = 240000000.0

    let private hexBpm (obj: string) : Result<float, UrcError> =
        match Int32.TryParse(obj, NumberStyles.HexNumber, CultureInfo.InvariantCulture) with
        | true, v -> Ok(float v)
        | false, _ -> Error(UrcError.syntax 1 $"invalid hex BPM in channel 03: \"{obj}\"")

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
            ObjectCount: int
            Used: Set<int * char>
            LnChannels: Set<string>
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

    /// Maps a BMS-family chart onto a URC chart.
    let convert (chart: BmsChart) : Result<Chart, UrcError> =
        result {
            let! bpmInitial =
                match chart.Bpm with
                | Some bpm when bpm > 0.0 -> Ok bpm
                | _ -> Error(UrcError.syntax 1 "missing or non-positive #BPM")

            let maxMeasure = chart.Measures |> Map.keys |> Seq.fold max -1

            let boundaries =
                [| 0 .. maxMeasure |]
                |> Array.scan
                    (fun acc m ->
                        let rate = Map.tryFind m chart.Rates |> Option.defaultValue 1.0
                        acc + rate)
                    0.0

            let initialAcc =
                {
                    Entries = [ (0.0, 0, EntryKind.Bpm bpmInitial) ]
                    Objects = []
                    ObjectCount = 0
                    Used = Set.empty
                    LnChannels = Set.empty
                }

            let rec scanIds
                (measureBoundary: float)
                (rate: float)
                (channel: string)
                (totalIds: float)
                (idx: int)
                (iAcc: ScanAcc)
                (remaining: string list)
                : Result<ScanAcc, UrcError> =
                match remaining with
                | [] -> Ok iAcc
                | obj :: rest ->
                    let y = measureBoundary + (float idx / totalIds) * rate

                    if Set.contains channel systemChannels then
                        if obj = "00" then
                            scanIds measureBoundary rate channel totalIds (idx + 1) iAcc rest
                        else
                            match channel with
                            | "03" ->
                                match hexBpm obj with
                                | Ok bpmVal ->
                                    let nextAcc =
                                        { iAcc with
                                            Entries = (y, 0, EntryKind.Bpm bpmVal) :: iAcc.Entries
                                        }

                                    scanIds measureBoundary rate channel totalIds (idx + 1) nextAcc rest
                                | Error err -> Error err
                            | "08" ->
                                match Map.tryFind obj chart.BpmDefs with
                                | Some bpmVal when bpmVal <= 0.0 ->
                                    Error(UrcError.syntax 1 $"non-positive #BPM{obj}: {bpmVal}")
                                | Some bpmVal ->
                                    let nextAcc =
                                        { iAcc with
                                            Entries = (y, 0, EntryKind.Bpm bpmVal) :: iAcc.Entries
                                        }

                                    scanIds measureBoundary rate channel totalIds (idx + 1) nextAcc rest
                                | None -> Error(UrcError.syntax 1 $"undefined #BPM{obj}")
                            | "09" ->
                                match Map.tryFind obj chart.StopDefs with
                                | Some stopVal ->
                                    let nextAcc =
                                        { iAcc with
                                            Entries = (y, 1, EntryKind.Stop stopVal) :: iAcc.Entries
                                        }

                                    scanIds measureBoundary rate channel totalIds (idx + 1) nextAcc rest
                                | None -> Error(UrcError.syntax 1 $"undefined #STOP{obj}")
                            | _ ->
                                match Map.tryFind obj chart.ScrollDefs with
                                | Some scrollVal ->
                                    let nextAcc =
                                        { iAcc with
                                            Entries = (y, 2, EntryKind.Scroll scrollVal) :: iAcc.Entries
                                        }

                                    scanIds measureBoundary rate channel totalIds (idx + 1) nextAcc rest
                                | None -> Error(UrcError.syntax 1 $"undefined #SCROLL{obj}")
                    else
                        match channelKind channel with
                        | None -> scanIds measureBoundary rate channel totalIds (idx + 1) iAcc rest
                        | Some kind ->
                            let lnChannels =
                                if kind = ChannelKind.LongNote then
                                    Set.add channel iAcc.LnChannels
                                else
                                    iAcc.LnChannels

                            let used =
                                if obj <> "00" then
                                    Set.add (sideOf channel[0], channel[1]) iAcc.Used
                                else
                                    iAcc.Used

                            if not (obj = "00" && kind <> ChannelKind.LongNote) then
                                let nextAcc =
                                    {
                                        Entries = (y, 4, EntryKind.Object iAcc.ObjectCount) :: iAcc.Entries
                                        Objects = (y, channel, obj) :: iAcc.Objects
                                        ObjectCount = iAcc.ObjectCount + 1
                                        Used = used
                                        LnChannels = lnChannels
                                    }

                                scanIds measureBoundary rate channel totalIds (idx + 1) nextAcc rest
                            else
                                scanIds
                                    measureBoundary
                                    rate
                                    channel
                                    totalIds
                                    (idx + 1)
                                    { iAcc with Used = used; LnChannels = lnChannels }
                                    rest

            let rec scanChannels
                (measureBoundary: float)
                (rate: float)
                (cAcc: ScanAcc)
                (channels: (string * string list) list)
                : Result<ScanAcc, UrcError> =
                match channels with
                | [] -> Ok cAcc
                | (channel, ids) :: restChannels ->
                    let totalIds = float (List.length ids)

                    match scanIds measureBoundary rate channel totalIds 0 cAcc ids with
                    | Ok nextAcc -> scanChannels measureBoundary rate nextAcc restChannels
                    | Error err -> Error err

            let rec scanMeasures (acc: ScanAcc) (m: int) : Result<ScanAcc, UrcError> =
                if m > maxMeasure then
                    Ok acc
                else
                    let rate = Map.tryFind m chart.Rates |> Option.defaultValue 1.0
                    let prevRate = Map.tryFind (m - 1) chart.Rates |> Option.defaultValue 1.0

                    let accWithMeter =
                        if rate <> prevRate then
                            let beats = rate * 4.0

                            if abs (beats - round beats) < 1e-9 && round beats >= 1.0 then
                                { acc with
                                    Entries = (boundaries[m], 3, EntryKind.Meter(int (round beats))) :: acc.Entries
                                }
                            else
                                acc
                        else
                            acc

                    match Map.tryFind m chart.Measures with
                    | None -> scanMeasures accWithMeter (m + 1)
                    | Some measureMap ->
                        match scanChannels boundaries[m] rate accWithMeter (Map.toList measureMap) with
                        | Ok nextAcc -> scanMeasures nextAcc (m + 1)
                        | Error err -> Error err

            let! scannedRaw = scanMeasures initialAcc 0

            let scanned =
                let withLntype2 =
                    if chart.LnType = 2 && not (Set.isEmpty scannedRaw.LnChannels) then
                        (scannedRaw, scannedRaw.LnChannels)
                        ||> Set.fold (fun acc channel ->
                            let accMid =
                                (acc, [ 0 .. maxMeasure ])
                                ||> List.fold (fun a m ->
                                    let present =
                                        Map.tryFind m chart.Measures
                                        |> Option.map (Map.containsKey channel)
                                        |> Option.defaultValue false

                                    if present then
                                        a
                                    else
                                        let y = boundaries[m]

                                        { a with
                                            Entries = (y, 4, EntryKind.Object a.ObjectCount) :: a.Entries
                                            Objects = (y, channel, "00") :: a.Objects
                                            ObjectCount = a.ObjectCount + 1
                                        })

                            let endY = boundaries[maxMeasure + 1]

                            { accMid with
                                Entries = (endY, 4, EntryKind.Object accMid.ObjectCount) :: accMid.Entries
                                Objects = (endY, channel, "00") :: accMid.Objects
                                ObjectCount = accMid.ObjectCount + 1
                            })
                    else
                        scannedRaw

                { withLntype2 with
                    Entries = List.rev withLntype2.Entries
                    Objects = List.rev withLntype2.Objects
                }

            let mode = detectMode chart.Pms scanned.Used

            let sortedEntries =
                scanned.Entries
                @ (boundaries |> Seq.map (fun y -> y, 5, EntryKind.Anchor) |> List.ofSeq)
                |> List.sortBy (fun (y, order, _) -> y, order)

            let groupedEntries = sortedEntries |> List.groupBy (fun (y, _, _) -> y)

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

                let rec foldGroupEvents
                    nextBpm
                    nextBeats
                    pendingStop
                    scroll
                    anchors
                    timed
                    (events: (float * int * EntryKind) list)
                    =
                    match events with
                    | [] -> nextBpm, nextBeats, pendingStop, scroll, anchors, timed
                    | (_, _, kind) :: rest ->
                        match kind with
                        | EntryKind.Bpm v ->
                            foldGroupEvents (Some v) nextBeats pendingStop scroll anchors timed rest
                        | EntryKind.Meter v ->
                            foldGroupEvents nextBpm v pendingStop scroll anchors timed rest
                        | EntryKind.Stop v ->
                            let stopTime =
                                match nextBpm with
                                | Some b -> MeasureUs * v / b
                                | None -> 0.0

                            foldGroupEvents nextBpm nextBeats (pendingStop + stopTime) scroll anchors timed rest
                        | EntryKind.Scroll v ->
                            foldGroupEvents nextBpm nextBeats pendingStop (Some v) anchors timed rest
                        | EntryKind.Object idx ->
                            foldGroupEvents
                                nextBpm
                                nextBeats
                                pendingStop
                                scroll
                                anchors
                                (Map.add idx timeUs timed)
                                rest
                        | EntryKind.Anchor ->
                            foldGroupEvents
                                nextBpm
                                nextBeats
                                pendingStop
                                scroll
                                (Shared.roundMs (timeUs / 1000.0) :: anchors)
                                timed
                                rest

                let nextBpm, nextBeats, pendingStop, scroll, nextAnchors, nextTimed =
                    foldGroupEvents
                        state.CurrentBpm
                        state.CurrentBeats
                        0.0
                        None
                        state.Anchors
                        state.Timed
                        group

                let bpmPoints =
                    match nextBpm with
                    | Some bpm when nextBpm <> state.CurrentBpm || nextBeats <> state.CurrentBeats ->
                        (Shared.roundMs (timeUs / 1000.0), bpm, nextBeats, 4) :: state.BpmPoints
                    | _ -> state.BpmPoints

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
                |> List.choose (fun (t, _, nt) -> if nt <> NoteType.LE then Some t else None)
                |> function
                    | [] -> 0
                    | xs -> List.min xs

            let anchorMs =
                timingState.Anchors
                |> List.rev
                |> List.tryFind (fun time -> time >= firstNoteTime)

            let! timing =
                Shared.buildTiming
                    (List.rev timingState.BpmPoints)
                    (List.rev timingState.SvPoints)
                    firstNoteTime
                    ".bms"
                    anchorMs

            let urcNotes =
                rawNotes
                |> List.map (fun (t, lane, nt) ->
                    {
                        TimestampMs = t - firstNoteTime
                        Lane = lane
                        Type = nt
                    })
                |> List.sortBy (fun n -> n.TimestampMs, n.Lane, n.Type)

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
