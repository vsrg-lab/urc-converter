namespace UrcConverter.Sources

open System.Text
open UrcConverter

module internal Shared =

    let private codePages =
        lazy (Encoding.RegisterProvider(CodePagesEncodingProvider.Instance))

    let ensureCodePages () = codePages.Value

    let roundMs (value: float) : int =
        if value >= 0.0 then
            int (value + 0.5)
        else
            int (value - 0.5)

    /// First measure boundary at or after timeMs; each BPM point anchors a grid.
    /// Overshooting the next point snaps to that point (the grid re-anchors there).
    let firstDownbeatAfter (bpmPoints: (int * float * int * int) list) (timeMs: int) : int option =
        let points = bpmPoints |> List.sortBy (fun (time, _, _, _) -> time)

        let rec loop rest =
            match rest with
            | [] -> None
            | (time, bpm, beats, noteValue) :: tail ->
                let nextTime =
                    match tail with
                    | (next, _, _, _) :: _ -> Some next
                    | [] -> None

                if timeMs <= time then
                    Some(roundMs (float time))
                else
                    let inSegment =
                        match nextTime with
                        | Some next -> timeMs < next
                        | None -> true

                    if inSegment && bpm > 0.0 && beats > 0 && noteValue > 0 then
                        let measureMs = float beats * (4.0 / float noteValue) * 60000.0 / bpm

                        if measureMs > 0.0 then
                            let k = ceil (float (timeMs - time) / measureMs - 1e-9)
                            let anchor = float time + k * measureMs

                            Some(
                                roundMs (
                                    match nextTime with
                                    | Some next -> min anchor (float next)
                                    | None -> anchor
                                )
                            )
                        else
                            loop tail
                    else
                        loop tail

        loop points

    type private TimingEvent =
        | BpmPoint of time: int * index: int * bpm: float * beats: int * noteValue: int
        | SvPoint of time: int * index: int * multiplier: float

    type private EmittedTimingPoint =
        {
            Time: int
            Bpm: float
            Multiplier: float
            Beats: int
            NoteValue: int
        }

    type private ScanState =
        {
            Bpm: float option
            Beats: int
            NoteValue: int
            Multiplier: float
            Last: (float * float * int * int) option
            Emitted: EmittedTimingPoint list
        }

    let buildTiming
        (bpmPoints: (int * float * int * int) list)
        (svPoints: (int * float) list)
        (firstNoteTime: int)
        (source: string)
        (measureAnchorMs: int option)
        : Result<TimingPoint list, UrcError> =
        let events =
            (bpmPoints
             |> List.indexed
             |> List.map (fun (index, (time, bpm, beats, noteValue)) ->
                 BpmPoint(time, index, bpm, beats, noteValue)))
            @ (svPoints
               |> List.indexed
               |> List.map (fun (index, (time, multiplier)) -> SvPoint(time, index, multiplier)))
            |> List.sortBy (function
                | BpmPoint(time, index, _, _, _)
                | SvPoint(time, index, _) -> time, index)

        let initial =
            {
                Bpm = None
                Beats = 4
                NoteValue = 4
                Multiplier = 1.0
                Last = None
                Emitted = []
            }

        let groupedEvents =
            events
            |> List.groupBy (function
                | BpmPoint(time, _, _, _, _)
                | SvPoint(time, _, _) -> time)

        let folder (state: ScanState) (time, evts) =
            let nextBpm, nextBeats, nextNoteValue, nextMult =
                evts
                |> List.fold
                    (fun (bpm, beats, noteValue, mult) ->
                        function
                        | BpmPoint(_, _, b, bt, nv) -> Some b, bt, nv, mult
                        | SvPoint(_, _, m) -> bpm, beats, noteValue, m)
                    (state.Bpm, state.Beats, state.NoteValue, state.Multiplier)

            let nextState =
                { state with
                    Bpm = nextBpm
                    Beats = nextBeats
                    NoteValue = nextNoteValue
                    Multiplier = nextMult
                }

            match nextBpm with
            | None -> nextState
            | Some bpm when state.Last = Some(bpm, nextMult, nextBeats, nextNoteValue) -> nextState
            | Some bpm ->
                { nextState with
                    Last = Some(bpm, nextMult, nextBeats, nextNoteValue)
                    Emitted =
                        {
                            Time = time
                            Bpm = bpm
                            Multiplier = nextMult
                            Beats = nextBeats
                            NoteValue = nextNoteValue
                        }
                        :: state.Emitted
                }

        let scanState = List.fold folder initial groupedEvents

        match List.rev scanState.Emitted with
        | [] -> Error(UrcError.Syntax(1, $"{source}: no BPM timing point"))
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
                    before @ { active with Time = anchor } :: after
                | _ -> emitted

            let points =
                emitted
                |> List.fold
                    (fun (acc: TimingPoint list) point ->
                        let time = max (point.Time - firstNoteTime) 0

                        let tp: TimingPoint =
                            {
                                TimestampMs = time
                                Bpm = point.Bpm
                                Meter = { Beats = point.Beats; NoteValue = point.NoteValue }
                                Multiplier = if point.Multiplier = 1.0 then None else Some point.Multiplier
                            }

                        match acc with
                        | head :: tail when head.TimestampMs = time -> tp :: tail
                        | _ -> tp :: acc)
                    []
                |> List.rev

            match points with
            | first :: _ when first.TimestampMs <> 0 ->
                Ok(
                    {
                        TimestampMs = 0
                        Bpm = first.Bpm
                        Meter = first.Meter
                        Multiplier = None
                    }
                    :: points
                )
            | _ -> Ok points

    let checkHoldOverlap (notes: Note list) : Result<unit, UrcError> =
        let rec isSorted (rest: Note list) =
            match rest with
            | []
            | [ _ ] -> true
            | first :: (second :: _ as tail) ->
                (first.TimestampMs < second.TimestampMs
                 || (first.TimestampMs = second.TimestampMs && first.Lane <= second.Lane))
                && isSorted tail

        let rec loop (openLanes: Set<int>) (rest: Note list) =
            match rest with
            | [] -> Ok()
            | note :: tail ->
                match note.Type with
                | NoteType.LS when Set.contains note.Lane openLanes ->
                    Error(UrcError.Syntax(1, $"overlapping holds on lane {note.Lane}"))
                | NoteType.LS -> loop (Set.add note.Lane openLanes) tail
                | NoteType.LE -> loop (Set.remove note.Lane openLanes) tail
                | _ -> loop openLanes tail

        let ordered =
            if isSorted notes then
                notes
            else
                notes
                |> List.indexed
                |> List.sortBy (fun (index, note) -> note.TimestampMs, note.Lane, index)
                |> List.map snd

        loop Set.empty ordered
