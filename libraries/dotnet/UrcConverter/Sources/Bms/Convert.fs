namespace UrcConverter.Sources.Bms

[<RequireQualifiedAccess>]
module Convert =

    open System
    open System.Collections.Generic
    open System.Globalization
    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Bms.Model
    open Channels

    [<Literal>]
    let private MeasureUs = 240000000.0

    let rec private gcd (a: int) (b: int) =
        if b = 0 then a else gcd b (a % b)

    let private channelSlots (lines: string list list) (keepZero: bool) : (float * string) list =
        match lines with
        | [ ids ] ->
            let total = float (List.length ids)

            ids
            |> List.indexed
            |> List.choose (fun (idx, obj) ->
                if keepZero || obj <> "00" then
                    Some(float idx / total, obj)
                else
                    None)
        | _ ->
            let slots = Dictionary<struct (int * int), string>()

            for ids in lines do
                let total = List.length ids
                let mutable idx = 0

                for obj in ids do
                    if keepZero || obj <> "00" then
                        let divisor = gcd idx total
                        let key = struct (idx / divisor, total / divisor)

                        if obj <> "00" then
                            slots[key] <- obj
                        elif not (slots.ContainsKey key) then
                            slots[key] <- "00"

                    idx <- idx + 1

            slots
            |> Seq.map (fun kv -> kv.Key, kv.Value)
            |> Seq.toList
            |> List.sortWith (fun (struct (a0, a1), _) (struct (b0, b1), _) ->
                compare (int64 a0 * int64 b1) (int64 b0 * int64 a1))
            |> List.map (fun (struct (num, den), obj) -> float num / float den, obj)

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

            let rec scanSlots
                (measureBoundary: float)
                (rate: float)
                (channel: string)
                (isSystem: bool)
                (kind: ChannelKind option)
                (iAcc: ScanAcc)
                (remaining: (float * string) list)
                : Result<ScanAcc, UrcError> =
                match remaining with
                | [] -> Ok iAcc
                | (pos, obj) :: rest ->
                    let y = measureBoundary + pos * rate

                    if isSystem then
                        match channel with
                        | "03" ->
                            match hexBpm obj with
                            | Ok bpmVal ->
                                let nextAcc =
                                    { iAcc with
                                        Entries = (y, 0, EntryKind.Bpm bpmVal) :: iAcc.Entries
                                    }

                                scanSlots measureBoundary rate channel isSystem kind nextAcc rest
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

                                scanSlots measureBoundary rate channel isSystem kind nextAcc rest
                            | None -> Error(UrcError.syntax 1 $"undefined #BPM{obj}")
                        | "09" ->
                            match Map.tryFind obj chart.StopDefs with
                            | Some stopVal ->
                                let nextAcc =
                                    { iAcc with
                                        Entries = (y, 1, EntryKind.Stop stopVal) :: iAcc.Entries
                                    }

                                scanSlots measureBoundary rate channel isSystem kind nextAcc rest
                            | None -> Error(UrcError.syntax 1 $"undefined #STOP{obj}")
                        | _ ->
                            match Map.tryFind obj chart.ScrollDefs with
                            | Some scrollVal ->
                                let nextAcc =
                                    { iAcc with
                                        Entries = (y, 2, EntryKind.Scroll scrollVal) :: iAcc.Entries
                                    }

                                scanSlots measureBoundary rate channel isSystem kind nextAcc rest
                            | None -> Error(UrcError.syntax 1 $"undefined #SCROLL{obj}")
                    else
                        let lnChannels =
                            if kind = Some ChannelKind.LongNote then
                                Set.add channel iAcc.LnChannels
                            else
                                iAcc.LnChannels

                        let used =
                            if obj <> "00" then
                                Set.add (sideOf channel[0], channel[1]) iAcc.Used
                            else
                                iAcc.Used

                        let nextAcc =
                            {
                                Entries = (y, 4, EntryKind.Object iAcc.ObjectCount) :: iAcc.Entries
                                Objects = (y, channel, obj) :: iAcc.Objects
                                ObjectCount = iAcc.ObjectCount + 1
                                Used = used
                                LnChannels = lnChannels
                            }

                        scanSlots measureBoundary rate channel isSystem kind nextAcc rest

            let rec scanChannels
                (measureBoundary: float)
                (rate: float)
                (cAcc: ScanAcc)
                (channels: (string * string list list) list)
                : Result<ScanAcc, UrcError> =
                match channels with
                | [] -> Ok cAcc
                | (channel, lines) :: restChannels ->
                    let isSystem = Set.contains channel systemChannels
                    let kind = if isSystem then None else channelKind channel

                    if not isSystem && Option.isNone kind then
                        scanChannels measureBoundary rate cAcc restChannels
                    else
                        let slots =
                            channelSlots lines (kind = Some ChannelKind.LongNote && chart.LnType = 2)

                        match scanSlots measureBoundary rate channel isSystem kind cAcc slots with
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

            let timed = Array.zeroCreate<float> scanned.ObjectCount

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
                    (events: (float * int * EntryKind) list)
                    =
                    match events with
                    | [] -> nextBpm, nextBeats, pendingStop, scroll, anchors
                    | (_, _, kind) :: rest ->
                        match kind with
                        | EntryKind.Bpm v ->
                            foldGroupEvents (Some v) nextBeats pendingStop scroll anchors rest
                        | EntryKind.Meter v ->
                            foldGroupEvents nextBpm v pendingStop scroll anchors rest
                        | EntryKind.Stop v ->
                            let stopTime =
                                match nextBpm with
                                | Some b -> MeasureUs * v / b
                                | None -> 0.0

                            foldGroupEvents nextBpm nextBeats (pendingStop + stopTime) scroll anchors rest
                        | EntryKind.Scroll v ->
                            foldGroupEvents nextBpm nextBeats pendingStop (Some v) anchors rest
                        | EntryKind.Object idx ->
                            timed[idx] <- timeUs
                            foldGroupEvents nextBpm nextBeats pendingStop scroll anchors rest
                        | EntryKind.Anchor ->
                            foldGroupEvents
                                nextBpm
                                nextBeats
                                pendingStop
                                scroll
                                (Shared.roundMs (timeUs / 1000.0) :: anchors)
                                rest

                let nextBpm, nextBeats, pendingStop, scroll, nextAnchors =
                    foldGroupEvents
                        state.CurrentBpm
                        state.CurrentBeats
                        0.0
                        None
                        state.Anchors
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
                }

            let timingState = (initialTiming, groupedEntries) ||> List.fold foldTimingGroup

            let! rawNotes = buildNotes chart mode scanned.Objects timed

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
