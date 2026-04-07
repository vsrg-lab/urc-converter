module internal UrcConverter.Parser.Sm.Conversion

open System
open UrcConverter.Core
open UrcConverter.Core.Models
open UrcConverter.Core.Models.Enums
open UrcConverter.Parser.Sm.Internal.Models

// #region Note Data Parsing

let private isNoteRow (s: string) =
    let t = s.Trim()
    t.Length > 0 && not (t.StartsWith "//")

let private parseNoteData (noteData: string): (float * int * char) list =
    let measures =
        noteData.Split ','
        |> Array.map (fun s ->
            s.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun l -> l.Trim())
            |> Array.filter isNoteRow)

    [
        for m in 0 .. measures.Length - 1 do
            let rows = measures[m]
            let rowCount = rows.Length
            if rowCount > 0 then
                for r in 0 .. rowCount - 1 do
                    let beat = float m * 4.0 + float r / float rowCount * 4.0
                    for c in 0 .. rows[r].Length - 1 do
                        if rows[r][c] <> '0' then
                            (beat, c, rows[r][c])
    ]

let private mapNoteType (c: char): NoteType option =
    match c with
    | '1' | 'L' -> Some NoteType.Normal     // tap, lift
    | '2' | '4' -> Some NoteType.LongStart  // hold head, roll head
    | '3'       -> Some NoteType.LongEnd    // hold/roll tail
    | 'M'       -> Some NoteType.Mine
    | 'F'       -> Some NoteType.Fake
    | _         -> None

// #endregion

// #region Timeline Events

type private TimelineEvent =
    | BpmChange of float
    | StopEvent of float    // Duration in seconds.
    | ScrollChange of float
    | NoteEvent of col: int * noteType: NoteType

let private eventPriority = function
    | BpmChange _       -> 0
    | ScrollChange _    -> 1
    | NoteEvent _       -> 2
    | StopEvent _       -> 3

let private buildTimeline 
    (bpms: SmBeatValue list)
    (stops: SmBeatValue list)
    (scrolls: SmBeatValue list)
    (notes: (float * int * char) list)
    : (float * TimelineEvent) list =
    let fromBeatValues ev = List.map (fun bv -> (bv.Beat, ev bv.Value))
    let noteEvents =
        notes
        |> List.choose (fun (beat, col, ch) ->
            mapNoteType ch |> Option.map (fun nt -> (beat, NoteEvent(col, nt))))

    [
        fromBeatValues BpmChange bpms
        fromBeatValues StopEvent stops
        fromBeatValues ScrollChange scrolls
        noteEvents
    ]
    |> List.concat
    |> List.sortBy (fun (beat, ev) -> beat, eventPriority ev)

// #endregion

// #region Timestamp Computation

type private WalkState = { Time: float; Bpm: float; Sv: float; PrevBeat: float }
type private TimestampEvent = { Time: int; Bpm: float; Sv: float; Event: TimelineEvent }

let private computeTimeStamps (initialBpm: float) (events: (float * TimelineEvent) list): TimestampEvent list =
    let initial = { Time = 0.0; Bpm = initialBpm; Sv = 1.0; PrevBeat = 0.0 }

    events
    |> List.fold 
        (fun (st, acc) (beat, ev) ->
            let dt = (beat - st.PrevBeat) * 60000.0 / st.Bpm
            let tNow = st.Time + dt

            let bpm', sv', extra =
                match ev with
                | BpmChange b -> (if b > 0.0 then b else st.Bpm), st.Sv, 0.0
                | StopEvent secs -> st.Bpm, st.Sv, secs * 1000.0
                | ScrollChange m -> st.Bpm, m, 0.0
                | NoteEvent _ -> st.Bpm, st.Sv, 0.0

            let result = { Time = tNow |> Math.Round |> int; Bpm = bpm'; Sv = sv'; Event = ev }
            let st' = { Time = tNow + extra; Bpm = bpm'; Sv = sv'; PrevBeat = beat }

            (st', result :: acc))
        (initial, [])
    |> snd
    |> List.rev

// #endregion

// #region Timing Points Extraction

let private extractTimingPoints (initialBpm: float) (events: TimestampEvent list): UrcTiming array =
    let initial = UrcTiming(0, initialBpm, "4/4", 1.0)

    let changes =
        events
        |> List.fold 
            (fun (sv, acc) e ->
                match e.Event with
                | BpmChange _ | ScrollChange _ -> (e.Sv, UrcTiming(e.Time, e.Bpm, "4/4", e.Sv) :: acc)
                | _ -> (sv, acc))
            (1.0, [])
        |> snd
        |> List.rev

    (initial :: changes)
    |> List.groupBy (fun t -> t.Timestamp)
    |> List.map (fun (_, group) -> group |> List.last)
    |> List.sortBy (fun t -> t.Timestamp)
    |> Array.ofList

// #endregion

// #region Note Extraction

let private extractNotes (events: TimestampEvent list): UrcNote array =
    events
    |> List.choose (fun e ->
        match e.Event with
        | NoteEvent(col, nt) -> Some(UrcNote(e.Time, col, nt))
        | _ -> None)
    |> Array.ofList

// #endregion

// Public API
let toUrc (file: SmFile) (chart: SmChart): UrcChart =
    // Resolve timing: per-chart (SSC) overrides global
    let bpms = chart.ChartBpms |> Option.defaultValue file.Bpms
    let stops = chart.ChartStops |> Option.defaultValue file.Stops
    let scrolls = chart.ChartScrolls |> Option.defaultValue file.Scrolls

    let initialBpm = bpms |> List.tryHead |> Option.map (fun bv -> bv.Value) |> Option.defaultValue 120.0
    let keyCount = keyCountFromSteps chart.StepsType

    // Parse and convert
    let rawNotes = parseNoteData chart.NoteData
    let timeline = buildTimeline bpms stops scrolls rawNotes
    let timestamped = computeTimeStamps initialBpm timeline

    let title =
        let t = file.TitleTranslit |> fun s -> if s <> "" then s else file.Title
        if file.SubTitle <> "" then $"{t} {file.SubTitle}" else t

    let artist = file.ArtistTranslit |> fun s -> if s <> "" then s else file.Artist

    UrcChart(
        FormatVersion = UrcFormat.Version,
        Metadata = UrcMetadata(
            Original = FormatName,
            Title = title,
            Artist = artist,
            Creator = (if file.Credit <> "" then file.Credit else "Unknown"),
            Version = $"{chart.Difficulty} {chart.Meter}"
        ),
        Layout = UrcLayout(keyCount, 0, Array.empty<int>),
        Timings = extractTimingPoints initialBpm timestamped,
        Notes = extractNotes timestamped,
        Judgment = null
    )