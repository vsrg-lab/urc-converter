namespace UrcConverter.Sources

open FsToolkit.ErrorHandling
open UrcConverter

module internal Shared =

    let roundMs (value: float) : int =
        if value >= 0.0 then
            int (value + 0.5)
        else
            int (value - 0.5)

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
            let mutable nextBpm = state.Bpm
            let mutable nextBeats = state.Beats
            let mutable nextMult = state.Multiplier
            for evt in evts do
                match evt with
                | BpmPoint(_, _, bpm, beats) ->
                    nextBpm <- Some bpm
                    nextBeats <- beats
                | SvPoint(_, _, mult) ->
                    nextMult <- mult
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

        (Result.Ok Set.empty, List.sortBy (fun note -> note.TimestampMs, note.Lane) notes)
        ||> List.fold (fun state note ->
            state |> Result.bind (fun openLanes -> folder openLanes note))
        |> Result.map ignore
