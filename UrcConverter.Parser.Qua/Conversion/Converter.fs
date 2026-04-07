module internal UrcConverter.Parser.Qua.Conversion

open UrcConverter.Core
open UrcConverter.Core.Models
open UrcConverter.Core.Models.Enums
open UrcConverter.Parser.Qua.Internal.Models

// #region Timing Points

let private meterFromSignature (signature: int): string = $"{signature}/4"

let private convertTimingPoints (chart: QuaChart): UrcTiming array =
    // Build timing points from TimingPoints (BPM change) and SliderVelocities (SV change)
    let bpmEvents =
        chart.TimingPoints
        |> List.map (fun tp ->
            (tp.StartTime |> round |> int, Some tp.Bpm, Some(meterFromSignature tp.Signature), None))

    let svEvents =
        chart.SliderVelocities
        |> List.map (fun sv ->
            (sv.StartTime |> round |> int, None, None, Some sv.Multiplier))

    // Merge by timestamp, fold to tract current state
    let allEvents =
        (bpmEvents @ svEvents)
        |> List.sortBy (fun (t, _, _, _) -> t)
        |> List.groupBy (fun (t, _, _, _) -> t)
        |> List.map (fun (t, group) ->
            let bpm = group |> List.tryPick (fun (_, b, _, _) -> b)
            let meter = group |> List.tryPick (fun (_, _, m, _) -> m)
            let sv = group |> List.tryPick (fun (_, _, _, s) -> s)
            (t, bpm, meter, sv))

    let initial = (120.0, "4/4", 1.0)

    allEvents
    |> List.mapFold
        (fun (prevBpm, prevMeter, prevSv) (t, bpm, meter, sv) ->
            let b = bpm |> Option.defaultValue prevBpm
            let m = meter |> Option.defaultValue prevMeter
            let s = sv |> Option.defaultValue prevSv
            UrcTiming(t, b, m, s), (b, m, s))
        initial
    |> fst
    |> Array.ofList

// #endregion

// #region Notes

let private convertHitObject (h: QuaHitObject): UrcNote list =
    let lane = h.Lane - 1   // 1-indexed -> 0-indexed

    if h.EndTime > 0 then
        [
            UrcNote(h.StartTime, lane, NoteType.LongStart)
            UrcNote(h.EndTime, lane, NoteType.LongEnd)
        ]
    else
        [ UrcNote(h.StartTime, lane, NoteType.Normal) ]

// #endregion

// Public API
let toUrc (chart: QuaChart): UrcChart =
    let keyCount =
        match chart.Mode with
        | 2 -> 7    // Keys7
        | _ -> 4    // Keys4

    let specialCount = if chart.HasScratchKey then 1 else 0
    let specialLanes = if chart.HasScratchKey then [| 0 |] else Array.empty<int>

    let notes =
        chart.HitObjects
        |> List.collect convertHitObject
        |> List.sortBy (fun n -> n.Timestamp)
        |> Array.ofList

    UrcChart(
        FormatVersion = UrcFormat.Version,
        Metadata = UrcMetadata(
            Original = FormatName,
            Title = chart.Title,
            Artist = chart.Artist,
            Creator = (if chart.Creator <> "" then chart.Creator else "Unknown"),
            Version = chart.DifficultyName
        ),
        Layout = UrcLayout(keyCount, specialCount, specialLanes),
        Timings = convertTimingPoints chart,
        Notes = notes,
        Judgment = null
    )