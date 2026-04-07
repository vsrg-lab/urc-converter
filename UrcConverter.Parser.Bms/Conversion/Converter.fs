module internal UrcConverter.Parser.Bms.Conversion

open System
open UrcConverter.Core
open UrcConverter.Core.Models
open UrcConverter.Core.Models.Enums
open UrcConverter.Parser.Bms.Internal.Models

// #region Timeline Events

type private TimelineEvent =
    | MeterChange of float      // channel 02: measure length ratio
    | BpmChangeHex of string    // channel 03: raw 2-char hex BPM
    | BpmChangeExt of int       // channel 08: key into ExtBpms
    | StopRef of int            // channel 09: key into Stops
    | ScrollRef of int          // channel SC: key into Scrolls
    | NoteHit of ch: int * value: string

/// Processing priority within the same beat position.
/// BPM/Scroll first, notes second, stops last.
let private eventPriority = function
    | MeterChange _ -> 0
    | BpmChangeHex _ | BpmChangeExt _ -> 1
    | ScrollRef _ -> 2
    | NoteHit _ -> 3
    | StopRef _ -> 4

// #endregion

// #region Beat Position Computation

let private measureLengthOf (chart: BmsChart) m =
    chart.MeasureLengths |> Map.tryFind m |> Option.defaultValue 1.0

/// Precompute cumulative beat positions at the start of each measure.
let private computeMeasureStarts (chart: BmsChart) (maxMeasure: int): float[] =
    let starts = Array.zeroCreate (maxMeasure + 2)
    for m in 0 .. maxMeasure do
        starts[m + 1] <- starts[m] + measureLengthOf chart m * 4.0
    starts

let private beatPosOf (starts: float[]) (chart: BmsChart) (obj: BmsObject): float =
    starts[obj.Measure] + obj.Position * measureLengthOf chart obj.Measure * 4.0 

// #endregion

// #region Meter Inference

let private inferMeter (ratio: float): string =
    let tryDenominator d =
        let numerator = ratio * float d
        let rounded = Math.Round numerator
        if abs (numerator - rounded) < 0.001 then
            Some $"{int rounded}/{d}"
        else
            None

    [ 4; 8; 16 ]
    |> List.tryPick tryDenominator
    |> Option.defaultValue "4/4"

// #endregion

// #region Timeline Construction

let private buildTimeline (chart: BmsChart) (measureStarts: float[]): (float * TimelineEvent) list =
    let maxMeasure = measureStarts.Length - 2
    let meterEvents =
        [
            for m in 0 .. maxMeasure do
                let curr = measureLengthOf chart m
                let prev = if m = 0 then 1.0 else measureLengthOf chart (m - 1)
                if m = 0 || abs (curr - prev) > 0.001 then
                    (measureStarts[m], MeterChange curr)
        ]

    let objectEvents =
        chart.Objects
        |> List.choose (fun o ->
            let bp = beatPosOf measureStarts chart o
            let key = Base36.decode o.Value

            if Channel.isPlayable o.Channel then
                Some(bp, NoteHit(o.Channel, o.Value))
            elif o.Channel = Channel.BpmHex then
                Some(bp, BpmChangeHex o.Value)
            elif o.Channel = Channel.ExtBpm then
                Some(bp, BpmChangeExt key)
            elif o.Channel = Channel.Stop then
                Some(bp, StopRef key)
            elif o.Channel = Channel.Scroll then
                Some(bp, ScrollRef key)
            else
                None)

    (meterEvents @ objectEvents) 
    |> List.sortBy (fun (bp, ev) -> bp, eventPriority ev)

// #endregion

// #region Timestamp Computation

type private WalkState = { Time: float; Bpm: float; Sv: float; PrevBeatPos: float }

type private TimestampEvent = { Time: int; Bpm: float; Sv: float; BeatPos: float; Event: TimelineEvent }

let private computeTimestamps (chart: BmsChart) (events: (float * TimelineEvent) list): TimestampEvent list =
    let initial = { Time = 0.0; Bpm = chart.Bpm; Sv = 1.0; PrevBeatPos = 0.0 }

    let (_, revResults) =
        events
        |> List.fold
            (fun (st, acc) (bp, ev) ->
                let dt = (bp - st.PrevBeatPos) * 60000.0 / st.Bpm
                let tNow = st.Time + dt

                let bpm', sv', extra =
                    match ev with
                    | MeterChange _ ->
                        st.Bpm, st.Sv, 0.0

                    | BpmChangeHex hex ->
                        let b = float (Base36.decodeHex hex)
                        (if b > 0.0 then b else st.Bpm), st.Sv, 0.0

                    | BpmChangeExt key ->
                        let b = chart.ExtBpms |> Map.tryFind key
                                |> Option.filter (fun b -> b > 0.0)
                                |> Option.defaultValue st.Bpm
                        b, st.Sv, 0.0

                    | StopRef key ->
                        let v = chart.Stops |> Map.tryFind key |> Option.defaultValue 0.0
                        st.Bpm, st.Sv, v / 48.0 * 60000.0 / st.Bpm

                    | ScrollRef key ->
                        st.Bpm, (chart.Scrolls |> Map.tryFind key |> Option.defaultValue st.Sv), 0.0

                    | NoteHit _ ->
                        st.Bpm, st.Sv, 0.0

                let result = { Time = tNow |> Math.Round |> int; Bpm = bpm'; Sv = sv'; BeatPos = bp; Event = ev }
                let st' = { Time = tNow + extra; Bpm = bpm'; Sv = sv'; PrevBeatPos = bp }

                (st', result :: acc))
            (initial, [])

    revResults |> List.rev

// #endregion

// #region Timing Points Extraction

let private extractTimingPoints (chart: BmsChart) (initialBpm: float) (events: TimestampEvent list): UrcTiming array =
    let defaultMeter = UrcTiming(0, initialBpm, "4/4", 1.0)
    let initialMeter = measureLengthOf chart 0 |> inferMeter

    let changes =
        events
        |> List.fold
            (fun (meter, acc) e ->
                match e.Event with
                | MeterChange ratio ->
                    let m = inferMeter ratio
                    (m, UrcTiming(e.Time, e.Bpm, m, e.Sv) :: acc)
                | BpmChangeHex _ | BpmChangeExt _ | ScrollRef _ ->
                    (meter, UrcTiming(e.Time, e.Bpm, meter, e.Sv) :: acc)
                | _ ->
                    (meter, acc))
            (initialMeter, [])
        |> snd
        |> List.rev

    // Merge events at the same timestamp - last one wins for BPM / SV state
    (defaultMeter :: changes)
    |> List.groupBy (fun t -> t.Timestamp)
    |> List.map (fun (_, group) -> group |> List.last)
    |> List.sortBy (fun t -> t.Timestamp)
    |> Array.ofList

// #endregion

// #region Layout Detection & Lane Mapping

/// Standard IIDX key order: Scratch, Key 1~5, Key 6~7
let private channelOrderWithScratch = [| 16; 11; 12; 13; 14; 15; 18; 19 |]
let private channelOrderWithoutScratch = [| 11; 12; 13; 14; 15; 18; 19 |]

let channelToLane (hasScratch: bool) (ch: int): int option =
    let order = if hasScratch then channelOrderWithScratch else channelOrderWithoutScratch
    order |> Array.tryFindIndex ((=) ch)

let detectLayout (objects: BmsObject list): int * bool =
    let usedChannels =
        objects
        |> List.filter (fun o -> Channel.isPlayable o.Channel)
        |> List.map (fun o -> if Channel.isLn o.Channel then Channel.lnToNote o.Channel else o.Channel)
        |> Set.ofList

    let hasScratch = Set.contains 16 usedChannels
    let has67 = Set.contains 18 usedChannels || Set.contains 19 usedChannels
    let keyCount = if has67 then 7 else 5

    (keyCount, hasScratch)

// #endregion

// #region LongNote Pairing

/// LNTYPE 1 (RDM): consecutive pairs in LN channels 51 ~ 59.
let private pairLnType1 (lnNotes: (int * int) list): (int * int * int) list =
    lnNotes
    |> List.groupBy snd
    |> List.collect (fun (_lane, notes) ->
        notes
        |> List.sortBy fst
        |> List.chunkBySize 2
        |> List.choose (function
            | [ (t1, lane); (t2, _) ] -> Some(lane, t1, t2)
            | _ -> None))

/// LNOBJ: specific objects ID marks LN end; previous note on same lane becomes LN start.
/// Returns (normal notes, Ln pairs).
let private pairLnObj (lnObjId: int) (notes: (int * int * string) list)
    : (int * int) list * (int * int * int) list =
    let normals = ResizeArray<int * int>()
    let lnPairs = ResizeArray<int * int * int>()
    let grouped = notes |> List.groupBy (fun (_, lane, _) -> lane)

    for (_lane, laneNotes) in grouped do
        let sorted = laneNotes |> List.sortBy (fun (t, _, _) -> t)
        let mutable prev: (int * int) option = None

        for (t, lane, value) in sorted do
            if Base36.decode value = lnObjId then
                match prev with
                | Some(startT, l) ->
                    lnPairs.Add(l, startT, t)
                    prev <- None
                | None ->
                    normals.Add(t, lane)
            else
                match prev with
                | Some(pt, pl) -> normals.Add(pt, pl)
                | None -> ()
                prev <- Some(t, lane)

        match prev with
        | Some(pt, pl) -> normals.Add(pt, pl)
        | None -> ()

    (Seq.toList normals, Seq.toList lnPairs)

// #endregion

// #region Note Extraction

let private extractNotes (chart: BmsChart) (hasScratch: bool) (events: TimestampEvent list): UrcNote array =
    let rawNotes =
        events
        |> List.choose (fun e ->
            match e.Event with
            | NoteHit(ch, value) ->
                let noteCh = if Channel.isLn ch then Channel.lnToNote ch else ch
                channelToLane hasScratch noteCh
                |> Option.map (fun lane -> (e.Time, lane, ch, value))
            | _ -> None)

    let toLnPairNotes pairs =
        pairs |> List.collect (fun (lane, s, e) -> [ UrcNote(s, lane, NoteType.LongStart); UrcNote(e, lane, NoteType.LongEnd) ])

    let notes =
        match chart.LnObj with
        | Some lnObjId ->
            // LNOBJ Mode: all notes are on normal channels, LN end is marked by object ID
            let mapped = rawNotes |> List.map (fun (t, lane, _, value) -> (t, lane, value))
            let normals, lnPairs = pairLnObj lnObjId mapped
            let normalNotes = normals |> List.map (fun (t, lane) -> UrcNote(t, lane, NoteType.Normal))

            normalNotes @ toLnPairNotes lnPairs

        | None when chart.LnType = 1 ->
            // LNTYPE 1 (RDM): LN channels 51 ~ 59
            let normals, lnChanNotes = rawNotes |> List.partition (fun (_, _, ch, _) -> Channel.isNote ch)
            let lnMapped = lnChanNotes |> List.map (fun (t, lane, _, _) -> (t, lane))
            let normalNotes = normals |> List.map (fun (t, lane, _, _) -> UrcNote(t, lane, NoteType.Normal))
            
            normalNotes @ (pairLnType1 lnMapped |> toLnPairNotes)

        | _ ->
            // No LN support - treat all as normal notes
            rawNotes |> List.map (fun (t, lane, _, _) -> UrcNote(t, lane, NoteType.Normal))

    notes |> List.sortBy (fun n -> n.Timestamp) |> Array.ofList

// #endregion

// Public API
let toUrc (chart: BmsChart): UrcChart =
    // Layout
    let keyCount, hasScratch = detectLayout chart.Objects
    let specialLanes = if hasScratch then [| 0 |] else Array.empty<int>
    let specialCount = if hasScratch then 1 else 0

    // Timeline
    let maxMeasure = chart.Objects |> List.map (fun o -> o.Measure) |> List.max
    let measureStarts = computeMeasureStarts chart maxMeasure
    let timeline = buildTimeline chart measureStarts
    let timestamp = computeTimestamps chart timeline

    // Build outputs
    let title = if chart.SubTitle <> "" then $"{chart.Title} {chart.SubTitle}" else chart.Title

    UrcChart(
        FormatVersion = UrcFormat.Version,
        Metadata = UrcMetadata(
            Original = FormatName,
            Title = title,
            Artist = chart.Artist,
            Creator = (if chart.SubArtist <> "" then chart.SubArtist else "Unknown"),
            Version = string chart.PlayLevel
        ),
        Layout = UrcLayout(keyCount, specialCount, specialLanes),
        Timings = extractTimingPoints chart chart.Bpm timestamp,
        Notes = extractNotes chart hasScratch timestamp,
        Judgment = null // BMS doesn't have judgment information.
    )
