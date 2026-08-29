namespace UrcConverter.Sources

open UrcConverter

module internal Shared =

    let roundMs (value: float) : int =
        if value >= 0.0 then
            int (value + 0.5)
        else
            int (value - 0.5)

    let sequence (results: Result<'a, UrcError> list) : Result<'a list, UrcError> =
        let add result value =
            match result, value with
            | Result.Ok values, Result.Ok value -> Result.Ok(value :: values)
            | Result.Error error, _ -> Result.Error error
            | _, Result.Error error -> Result.Error error

        results |> List.fold add (Result.Ok []) |> Result.map List.rev

    let tryAt (index: int) (items: 'a list) : 'a option =
        List.tryItem index items

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
            Last: (float * float) option
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

        let folder (state: ScanState) event =
            let time, state =
                match event with
                | BpmPoint(time, _, bpm, beats) ->
                    time,
                    { state with
                        Bpm = Some bpm
                        Beats = beats
                    }
                | SvPoint(time, _, multiplier) -> time, { state with Multiplier = multiplier }

            match state.Bpm with
            | None -> state
            | Some bpm when state.Last = Some(bpm, state.Multiplier) -> state
            | Some bpm ->
                { state with
                    Last = Some(bpm, state.Multiplier)
                    Emitted =
                        {
                            Time = time
                            Bpm = bpm
                            Multiplier = state.Multiplier
                            Beats = state.Beats
                        }
                        :: state.Emitted
                }

        let scanState = List.fold folder initial events

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
