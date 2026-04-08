module internal UrcConverter.Parser.Ojn.Conversion

open System
open UrcConverter.Core
open UrcConverter.Core.Models
open UrcConverter.Core.Models.Enums
open UrcConverter.Parser.Ojn.Internal.Models

// #region Timeline Events

type private TimelineEvent =
    | MeterChange of float
    | BpmChangeEvent of float
    | NoteHit of lane: int * noteType: int

let private eventPriority = function
    | MeterChange _ -> 0
    | BpmChangeEvent _ -> 1
    | NoteHit _ -> 2

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

// #region Measure Fractions (channel 0)

let private measureFractions (packages: OjnPackage list): Map<int, float> =
    packages
    |> List.filter (fun p -> p.Channel = 0)
    |> List.collect (fun p ->
        p.Events
        |> List.choose (function
            | MeasureFraction f -> Some(p.Measure, f)
            | _ -> None))
    |> Map.ofList

let private measureLengthOf (fractions: Map<int, float>) m =
    fractions |> Map.tryFind m |> Option.defaultValue 1.0

// #endreigon

// #region Beat Position Computation

let private computeMeasureStarts (fractions: Map<int, float>) (maxMeasure: int): float[] =
    let starts = Array.zeroCreate (maxMeasure + 2)
    for m in 0 .. maxMeasure do
        starts[m + 1] <- starts[m] + measureLengthOf fractions m * 4.0
    starts

// #endregion

// #region Timeline Construction

let private buildTimeline
    (packages: OjnPackage list)
    (fractions: Map<int, float>)
    (measureStarts: float[])
    : (float * TimelineEvent) list =
    let maxMeasure = measureStarts.Length - 2

    let meterEvents =
        [
            for m in 0 .. maxMeasure do
                let curr = measureLengthOf fractions m
                let prev = if m = 0 then 1.0 else measureLengthOf fractions (m - 1)
                if m = 0 || abs (curr - prev) > 0.001 then
                    (measureStarts[m], MeterChange curr)
        ]

    let objectEvents =
        packages
        |> List.filter (fun p -> p.Channel >= 1 && p.Channel <= 8)
        |> List.collect (fun p ->
            let count = p.Events.Length
            if count = 0 then []
            else
                p.Events
                |> List.mapi (fun i e ->
                    let pos = float i / float count
                    let bp = measureStarts[p.Measure] + pos * measureLengthOf fractions p.Measure * 4.0
                    (bp, e))
                |> List.choose (fun (bp, e) ->
                    match e with
                    | BpmChange bpm -> Some(bp, BpmChangeEvent bpm)
                    | Note n -> Some(bp, NoteHit(p.Channel - 2, n.NoteType))
                    | _ -> None))

    (meterEvents @ objectEvents)
    |> List.sortBy (fun (bp, ev) -> bp, eventPriority ev)

// #endregion

// #region Timestamp Computation

type private WalkState = { Time: float; Bpm: float; PrevBeatPos: float }
type private TimestampEvent = { Time: int; Bpm: float; Event: TimelineEvent }

let private computeTimestamps (initialBpm: float) (events: (float * TimelineEvent) list): TimestampEvent list =
    let initial = { Time = 0.0; Bpm = initialBpm; PrevBeatPos = 0.0 }

    events
    |> List.fold
        (fun (st, acc) (bp, ev) ->
            let dt = (bp - st.PrevBeatPos) * 60000.0 / st.Bpm
            let tNow = st.Time + dt

            let bpm' =
                match ev with
                | BpmChangeEvent b -> b
                | _ -> st.Bpm

            let result = { Time = tNow |> Math.Round |> int; Bpm = bpm'; Event = ev }
            let st' = { Time = tNow; Bpm = bpm'; PrevBeatPos = bp }

            (st', result :: acc))
        (initial, [])
    |> snd
    |> List.rev

// #endregion

// #region Timing Points Extraction

let private extractTimingPoints (fractions: Map<int, float>) (initialBpm: float) (events: TimestampEvent list): UrcTiming array =
    let initialMeter = measureLengthOf fractions 0 |> inferMeter
    let initial = UrcTiming(0, initialBpm, initialMeter, 1.0)

    let changes =
        events
        |> List.fold
            (fun (meter, acc) e ->
                match e.Event with
                | MeterChange ratio ->
                    let m = inferMeter ratio
                    (m, UrcTiming(e.Time, e.Bpm, m, 1.0) :: acc)
                | BpmChangeEvent _ ->
                    (meter, UrcTiming(e.Time, e.Bpm, meter, 1.0) :: acc)
                | _ ->
                    (meter, acc))
            (initialMeter, [])
        |> snd
        |> List.rev

    (initial :: changes)
    |> List.groupBy (fun t -> t.Timestamp)
    |> List.map (fun (_, group) -> group |> List.last)
    |> List.sortBy (fun t -> t.Timestamp)
    |> Array.ofList

// #endregion

// #regin Note Extraction

let private extractNotes (events: TimestampEvent list): UrcNote array =
    events
    |> List.choose (fun e ->
        match e.Event with
        | NoteHit(lane, noteType) ->
            let nt =
                match noteType with
                | 2 -> NoteType.LongStart
                | 3 -> NoteType.LongEnd
                | _ -> NoteType.Normal
            Some(UrcNote(e.Time, lane, nt))
        | _ -> None)
    |> Array.ofList

// #endregion

// Public API
let toUrc (file: OjnFile) (difficultyIndex: int): UrcChart = 
    let packages = file.Packages[difficultyIndex]
    let header = file.Header

    let fractions = measureFractions packages
    let maxMeasure =
        packages
        |> List.map (fun p -> p.Measure)
        |> function [] -> 0 | ms -> List.max ms

    let measureStarts = computeMeasureStarts fractions maxMeasure
    let timeline = buildTimeline packages fractions measureStarts
    let timestamped = computeTimestamps header.Bpm timeline

    UrcChart(
        FormatVersion = UrcFormat.Version,
        Metadata = UrcMetadata(
            Original = FormatName,
            Title = header.Title,
            Artist = header.Artist,
            Creator = (if header.Noter <> "" then header.Noter else "Unknown"),
            Version = $"{difficultyNames[difficultyIndex]} {header.Level[difficultyIndex]}"
        ),
        Layout = UrcLayout(7, 0, Array.empty<int>),
        Timings = extractTimingPoints fractions header.Bpm timestamped,
        Notes = extractNotes timestamped,
        Judgment = null
    )