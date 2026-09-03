namespace UrcConverter.Sources

open FsToolkit.ErrorHandling
open UrcConverter

module internal Shared =

    let roundMs (value: float) : int =
        if value >= 0.0 then
            int (value + 0.5)
        else
            int (value - 0.5)

    /// First measure boundary at or after timeMs; each BPM point anchors a grid.
    /// Overshooting the next point snaps to that point (the grid re-anchors there).
    let firstDownbeatAfter (bpmPoints: (int * float * int) list) (timeMs: int) : int option =
        let points = bpmPoints |> List.sortBy (fun (time, _, _) -> time)

        let rec loop rest =
            match rest with
            | [] -> None
            | (time, bpm, beats) :: tail ->
                let nextTime = match tail with (next, _, _) :: _ -> Some next | [] -> None

                if timeMs <= time then Some(roundMs (float time))
                else
                    let inSegment = match nextTime with Some next -> timeMs < next | None -> true
                    let measureMs = float beats * 60000.0 / bpm

                    if inSegment && measureMs > 0.0 then
                        let k = ceil (float (timeMs - time) / measureMs - 1e-9)
                        let anchor = float time + k * measureMs

                        Some(roundMs (match nextTime with Some next -> min anchor (float next) | None -> anchor))
                    else
                        loop tail

        loop points

    let sequence (results: Result<'a, UrcError> list) : Result<'a list, UrcError> =
        List.sequenceResultM results

    type private TimingEvent =
        | BpmPoint of time: int * index: int * bpm: float * beats: int
        | SvPoint of time: int * index: int * multiplier: float

    type private EmittedTimingPoint =
        {
            Time: int
            Bpm: float
            Multiplier: float
            Beats: int
        }

    type private ScanState =
        {
            Bpm: float option
            Beats: int
            Multiplier: float
            Last: (float * float * int) option
            Emitted: EmittedTimingPoint list
        }

    let buildTiming
        (bpmPoints: (int * float * int) list)
        (svPoints: (int * float) list)
        (firstNoteTime: int)
        (source: string)
        (measureAnchorMs: int option)
        : Result<TimingPoint list, UrcError> =
        let events =
            (bpmPoints
             |> List.indexed
             |> List.map (fun (index, (time, bpm, beats)) -> BpmPoint(time, index, bpm, beats)))
            @ (svPoints
               |> List.indexed
               |> List.map (fun (index, (time, multiplier)) -> SvPoint(time, index, multiplier)))
            |> List.sortBy (function
                | BpmPoint(time, index, _, _)
                | SvPoint(time, index, _) -> time, index)

        let initial =
            {
                Bpm = None
                Beats = 4
                Multiplier = 1.0
                Last = None
                Emitted = []
            }

        let groupedEvents =
            events
            |> List.groupBy (function
                | BpmPoint(time, _, _, _)
                | SvPoint(time, _, _) -> time)
        let folder (state: ScanState) (time, evts) =
            let nextBpm, nextBeats, nextMult =
                evts
                |> List.fold
                    (fun (bpm, beats, mult) -> function
                        | BpmPoint(_, _, b, bt) -> Some b, bt, mult
                        | SvPoint(_, _, m) -> bpm, beats, m)
                    (state.Bpm, state.Beats, state.Multiplier)

            let nextState = { state with Bpm = nextBpm; Beats = nextBeats; Multiplier = nextMult }
            match nextBpm with
            | None -> nextState
            | Some bpm when state.Last = Some(bpm, nextMult, nextBeats) -> nextState
            | Some bpm ->
                { nextState with
                    Last = Some(bpm, nextMult, nextBeats)
                    Emitted =
                        {
                            Time = time
                            Bpm = bpm
                            Multiplier = nextMult
                            Beats = nextBeats
                        }
                        :: state.Emitted
                }
        let scanState = List.fold folder initial groupedEvents

        match List.rev scanState.Emitted with
        | [] -> Result.Error(UrcError.Syntax(1, $"{source}: no BPM timing point"))
        | emitted ->
            // A point is forced at the anchor even without a state change so
            // the measure grid survives the 0-clamp of the shift.
            let emitted =
                match measureAnchorMs with
                | Some anchor when not (List.exists (fun point -> point.Time = anchor) emitted) ->
                    let active =
                        emitted
                        |> List.tryFindBack (fun point -> point.Time < anchor)
                        |> Option.defaultValue (List.head emitted)

                    let before, after = List.partition (fun point -> point.Time < anchor) emitted
                    before @ { Time = anchor; Bpm = active.Bpm; Multiplier = active.Multiplier; Beats = active.Beats } :: after
                | _ -> emitted

            let shifted =
                emitted
                |> List.map (fun point ->
                    max (point.Time - firstNoteTime) 0, (point.Bpm, point.Multiplier, point.Beats))
                |> Map.ofList

            let points =
                [
                    for time, (bpm, multiplier, beats) in Map.toList shifted ->
                        {
                            TimestampMs = time
                            Bpm = bpm
                            Meter = { Beats = beats; NoteValue = 4 }
                            Multiplier = if multiplier = 1.0 then None else Some multiplier
                        }
                ]

            match points with
            | first :: _ when first.TimestampMs <> 0 ->
                Result.Ok(
                    {
                        TimestampMs = 0
                        Bpm = first.Bpm
                        Meter = first.Meter
                        Multiplier = None
                    }
                    :: points
                )
            | _ -> Result.Ok points

    let checkHoldOverlap (notes: Note list) : Result<unit, UrcError> =
        let folder (openLanes: Set<int>) (note: Note) =
            match note.Type with
            | NoteType.LS when Set.contains note.Lane openLanes ->
                Result.Error(UrcError.Syntax(1, $"overlapping holds on lane {note.Lane}"))
            | NoteType.LS -> Result.Ok(Set.add note.Lane openLanes)
            | NoteType.LE -> Result.Ok(Set.remove note.Lane openLanes)
            | _ -> Result.Ok openLanes

        notes
        |> List.sortBy (fun note -> note.TimestampMs, note.Lane)
        |> List.fold (fun res note -> res |> Result.bind (fun lanes -> folder lanes note)) (Ok Set.empty)
        |> Result.map ignore
