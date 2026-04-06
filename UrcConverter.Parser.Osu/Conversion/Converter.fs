module internal UrcConverter.Parser.Osu.Conversion

open System
open UrcConverter.Core
open UrcConverter.Core.Models
open UrcConverter.Core.Models.Enums
open UrcConverter.Parser.Osu.Internal.Models

// #region Helpers

let private clamp lo hi v = max lo (min hi v)

let private preferUnicode unicode ascii =
    if String.IsNullOrWhiteSpace unicode then ascii else unicode

// #endregion

// #region Timing Point Conversion

/// Convert osu! timing points to URC timing points.
/// Uninherited (red line) -> BPM change, SV = 1.0
/// Inherited (green line) -> SV change, BPM carried from previous red line
let private convertTimingPoints (osuTps: OsuTimingPoint list): UrcTiming array =
    let sorted = osuTps |> List.sortBy (fun tp -> tp.Time)

    let groups =
        sorted
        |> List.groupBy (fun tp -> tp.Time)
        |> List.map (fun (time, tps) ->
            let bpmChange = tps |> List.tryFind (fun tp -> tp.Uninherited)
            let svChange = tps |> List.tryFind (fun tp -> not tp.Uninherited)
            (time, bpmChange, svChange))

    groups
    |> List.mapFold
        (fun (prevBpm, prevMeter) (time, bpmChange, svChange) ->
            let bpm, meter =
                match bpmChange with
                | Some r    -> 60000.0 / r.BeatLength, $"{r.Meter}/4"
                | None      -> prevBpm, prevMeter

            let sv =
                match svChange with
                | Some g    -> clamp 0.1 10.0 (-100.0 / g.BeatLength)
                | None      -> 1.0

            UrcTiming(time, bpm, meter, sv), (bpm, meter))
        (120.0, "4/4")
    |> fst
    |> Array.ofList

// #endregion

// #region Note Conversion

/// Convert x-coordinate to lane index.
/// osu!mania formula: lane = floor(x * keyCount / 512)
let private toLane (keyCount: int) (x: int): int =
    x * keyCount / 512 |> min (keyCount - 1) |> max 0

/// Convert an osu! hit object to URC note(s).
/// Normal note -> [N]
/// Hold note   -> [LS, LE]
let private convertHitObject (keyCount: int) (ho: OsuHitObject): UrcNote list =
    let lane = toLane keyCount ho.X

    match ho.NoteType with
    | HitCircle -> [ UrcNote(ho.Time, lane, NoteType.Normal) ]
    | HoldNote ->
        let endTime = ho.EndTime |> Option.defaultValue (ho.Time + 1)
        [ 
            UrcNote(ho.Time, lane, NoteType.LongStart) 
            UrcNote(endTime, lane, NoteType.LongEnd) 
        ]

// #endregion

// #region Judgment Windows

/// Compute osu!mania judgment windows (±ms) from OD.
/// MAX 300 : 16.5 ms (fixed)
/// 300     : 67.5 - 3 x OD
/// 200     : 100.5 - 3 x OD
/// 100     : 130.5 - 3 x OD
/// 50      : 154.5 - 3 x OD
let private judgmentFromOd (od: float): UrcJudgment =
    let windows =
        [| 
            16.5
            67.5 - 3.0 * od
            100.5 - 3.0 * od
            130.5 - 3.0 * od
            154.5 - 3.0 * od 
        |]
        |> Array.map (fun w -> Math.Round(w, 2))

    let rates = [| 100.0; 100.0; 66.67; 33.33; 16.67 |]

    UrcJudgment(windows, rates)

// #endregion

// Public API
let toUrc (chart: OsuChart): UrcChart =
    let metadata =
        UrcMetadata(
            Original    = "osu!mania",
            Title       = (if chart.TitleUnicode <> "" then chart.TitleUnicode else chart.Title),
            Artist      = (if chart.ArtistUnicode <> "" then chart.ArtistUnicode else chart.Artist),
            Creator     = chart.Creator,
            Version     = chart.Version
        )

    let layout = UrcLayout(chart.KeyCount, 0, Array.empty<int>)
    let timings = convertTimingPoints chart.TimingPoints

    let notes =
        chart.HitObjects
        |> List.collect (convertHitObject chart.KeyCount)
        |> List.sortBy (fun n -> n.Timestamp)
        |> Array.ofList

    UrcChart(
        FormatVersion   = UrcFormat.Version,
        Metadata        = metadata,
        Layout          = layout,
        Timings         = timings,
        Notes           = notes,
        Judgment       = judgmentFromOd chart.OverallDifficulty
    )
