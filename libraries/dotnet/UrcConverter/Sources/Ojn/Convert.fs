namespace UrcConverter.Sources.Ojn

module Convert =

    open System
    open System.Collections.Generic
    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Ojn.Model

    [<Literal>]
    let private MeasureMs = 240000.0

    [<Literal>]
    let private MeterTolerance = 1e-6

    let private versions = [| "Easy"; "Normal"; "Hard" |]

    let private scales =
        [| 1.0; 10.0; 100.0; 1000.0; 10000.0; 100000.0; 1000000.0; 10000000.0; 100000000.0; 1000000000.0 |]

    let private typeRank =
        function
        | NoteType.N -> 0
        | NoteType.LS -> 1
        | NoteType.LE -> 2
        | NoteType.M -> 3
        | NoteType.F -> 4

    let private decimalOf (candidate: int64) (precision: int) : string =
        let sign = if candidate < 0L then "-" else ""
        let digits = string (abs candidate)

        if precision = 0 then
            sign + digits
        else
            let padded = digits.PadLeft(precision + 1, '0')
            let split = padded.Length - precision
            let text = $"{sign}{padded[.. split - 1]}.{padded[split..]}"
            text.TrimEnd('0').TrimEnd('.')

    /// Shortest decimal that round-trips the f32 BPM, widened to f64.
    /// Candidates are enumerated explicitly instead of relying on
    /// formatter-specific tie-breaking, so every language agrees.
    let private canonicalBpm (value: float) : float =
        let invariant = Globalization.CultureInfo.InvariantCulture

        let matched =
            scales
            |> Seq.indexed
            |> Seq.tryPick (fun (precision, scale) ->
                let baseValue = int64 (Math.Truncate(value * scale))
                let candidates = [ baseValue; baseValue + 1L; baseValue - 1L ]

                candidates
                |> List.tryFind (fun candidate ->
                    let text = decimalOf candidate precision
                    float (float32 (Double.Parse(text, invariant))) = value)
                |> Option.map (fun candidate -> candidate, precision))

        match matched with
        | Some(candidate, precision) -> Double.Parse(decimalOf candidate precision, invariant)
        | None -> value

    let private gcd (a: int) (b: int) =
        let rec loop current remainder =
            if remainder = 0 then current else loop remainder (current % remainder)

        loop a b

    /// Meter (beats, note_value) matching a measure fraction, if clean.
    let private fractionMeter (value: float) : (int * int) option =
        let rec find noteValue =
            if noteValue > 64 then
                None
            else
                let beats = int (Math.Round(value * float noteValue))

                if beats < 1 then
                    find (noteValue + 1)
                elif abs (value - float beats / float noteValue) <= MeterTolerance then
                    let divisor = gcd beats noteValue
                    let reduced = (beats / divisor, noteValue / divisor)

                    if reduced = (1, 1) then None else Some reduced
                else
                    find (noteValue + 1)

        find 1

    let private nonEmpty (value: string) (fallback: string) =
        if value = "" then fallback else value

    type private WalkState =
        {
            Time: float
            Bpm: float
            Fraction: float
            Pointer: float
            Measure: int
            Meter: int * int
            MeterDirty: bool
            BpmPoints: (int * float * int * int) list
            Anchors: int list
            Notes: ResizeArray<int * int * NoteType>
            Holds: Dictionary<int, int>
        }

    let private addNote
        (notes: ResizeArray<int * int * NoteType>)
        (holds: Dictionary<int, int>)
        (ms: int)
        (lane: int)
        (kind: int)
        : unit =
        if kind = 3 then
            match holds.TryGetValue lane with
            | true, index ->
                holds.Remove(lane) |> ignore
                let headMs, headLane, _ = notes[index]

                if ms <= headMs then
                    notes[index] <- (headMs, headLane, NoteType.N)
                else
                    notes.Add((ms, lane, NoteType.LE))
            | false, _ -> ()
        elif holds.ContainsKey lane then
            ()
        elif kind = 2 then
            holds[lane] <- notes.Count
            notes.Add((ms, lane, NoteType.LS))
        else
            notes.Add((ms, lane, NoteType.N))

    let rec private walk (rest: OjnEvent list) (state: WalkState) : Result<WalkState, UrcError> =
        match rest with
        | [] -> Ok state
        | event :: tail ->
            result {
                let! advanced = advance state event.Measure event.Offset
                let time = advanced.Time + (MeasureMs * (event.Position - advanced.Pointer)) / advanced.Bpm
                let stepped = { advanced with Time = time; Pointer = event.Position }

                let next =
                    if event.Channel = 0 then
                        match fractionMeter event.Value with
                        | Some meter when meter <> stepped.Meter ->
                            { stepped with
                                Fraction = event.Value
                                Meter = meter
                                MeterDirty = true
                                BpmPoints = (Shared.roundMs time, canonicalBpm stepped.Bpm, fst meter, snd meter) :: stepped.BpmPoints }
                        | _ -> { stepped with Fraction = event.Value }
                    elif event.Channel = 1 then
                        { stepped with
                            BpmPoints = (Shared.roundMs time, canonicalBpm event.Value, fst stepped.Meter, snd stepped.Meter) :: stepped.BpmPoints
                            Bpm = event.Value }
                    else
                        addNote stepped.Notes stepped.Holds (Shared.roundMs time) (event.Channel - 2) event.Kind
                        stepped

                return! walk tail next
            }

    and private advance (state: WalkState) (target: int) (eventOffset: int) : Result<WalkState, UrcError> =
        if state.Measure >= target then
            Ok state
        elif state.Fraction - state.Pointer < 0.0 then
            Error(UrcError.syntax eventOffset "measure fraction cuts before the current position")
        else
            let time = state.Time + (MeasureMs * (state.Fraction - state.Pointer)) / state.Bpm
            let anchor = Shared.roundMs time

            let nextState =
                if state.MeterDirty then
                    { state with
                        Time = time
                        Measure = state.Measure + 1
                        Fraction = 1.0
                        Pointer = 0.0
                        Meter = (4, 4)
                        MeterDirty = false
                        BpmPoints = (anchor, canonicalBpm state.Bpm, 4, 4) :: state.BpmPoints
                        Anchors = anchor :: state.Anchors }
                else
                    { state with
                        Time = time
                        Measure = state.Measure + 1
                        Fraction = 1.0
                        Pointer = 0.0
                        Anchors = anchor :: state.Anchors }

            advance nextState target eventOffset

    let private convertChart (file: OjnFile) (difficulty: OjnDifficulty) : Result<Chart, UrcError> =
        result {
            // The explicit index keeps the ordering stable like in the other
            // languages (F# List.sortBy is not stable).
            let events =
                difficulty.Events
                |> List.indexed
                |> List.sortBy (fun (index, event) -> event.Measure, event.Position, index)
                |> List.map snd

            let initial =
                {
                    Time = 0.0
                    Bpm = file.Bpm
                    Fraction = 1.0
                    Pointer = 0.0
                    Measure = 0
                    Meter = (4, 4)
                    MeterDirty = false
                    BpmPoints = [ (0, canonicalBpm file.Bpm, 4, 4) ]
                    Anchors = [ 0 ]
                    Notes = ResizeArray()
                    Holds = Dictionary()
                }

            let! state = walk events initial

            for KeyValue(_lane, index) in state.Holds do
                let ms, lane, _ = state.Notes[index]
                state.Notes[index] <- (ms, lane, NoteType.N)

            let firstNoteTime =
                match state.Notes |> Seq.filter (fun (_, _, noteType) -> noteType <> NoteType.LE) |> Seq.toList with
                | [] -> 0
                | notes -> notes |> List.map (fun (ms, _, _) -> ms) |> List.min

            let anchor =
                state.Anchors |> List.rev |> List.tryFind (fun time -> time >= firstNoteTime)

            let! timingPoints = Shared.buildTiming (List.rev state.BpmPoints) [] firstNoteTime ".ojn" anchor

            let finalNotes =
                state.Notes
                |> Seq.toList
                |> List.sortBy (fun (ms, lane, noteType) -> ms, lane, typeRank noteType)
                |> List.map (fun (ms, lane, noteType) ->
                    { TimestampMs = ms - firstNoteTime; Lane = lane; Type = noteType })

            do! Shared.checkHoldOverlap finalNotes

            return
                {
                    FormatVersion = { Major = 1; Minor = 1 }
                    Metadata =
                        {
                            Original = "O2Jam"
                            Title = nonEmpty file.Title "Unknown"
                            Artist = nonEmpty file.Artist "Unknown"
                            Creator = nonEmpty file.Noter "Unknown"
                            Version = versions[difficulty.Index]
                        }
                    Judgment = None
                    Layout = { Keys = 7; SpecialKeys = 0; SpecialLanes = None }
                    TimingPoints = timingPoints
                    Notes = finalNotes
                }
        }

    /// Converts every difficulty of an OJN file into URC charts.
    let convertOjn (file: OjnFile) : Result<Chart list, UrcError> =
        file.Difficulties |> List.map (convertChart file) |> Shared.sequence
