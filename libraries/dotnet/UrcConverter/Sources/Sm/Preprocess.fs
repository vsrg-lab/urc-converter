namespace UrcConverter.Sources.Sm

module internal Preprocess =

    open UrcConverter
    open UrcConverter.Sources.Sm.Model

    [<Literal>]
    let FastBpmWarp = 9999999.0

    let rowsOf (beats: float) : int =
        let value = beats * 48.0

        if value >= 0.0 then
            int (value + 0.5)
        else
            int (value - 0.5)

    /// Merges warp segments into [start, dest) row intervals; overlapping
    /// warps adopt the greater destination (StepMania semantics).
    let warpIntervals (warpSegs: (float * float) list) : (int * int) list =
        warpSegs
        |> List.map (fun (beat, length) -> let start = rowsOf beat in (start, start + rowsOf length))
        |> List.sortBy fst
        |> List.fold
            (fun (merged: (int * int) list) (start, dest) ->
                match merged with
                | (lastStart, lastDest) :: tail when start < lastDest ->
                    (lastStart, max lastDest dest) :: tail
                | _ -> (start, dest) :: merged)
            []
        |> List.rev

    /// Drops notes strictly inside a warp and truncates tails that end inside
    /// one to the warp start row.
    let filterNotes (notes: SmNote list) (intervals: (int * int) list) : SmNote list =
        notes
        |> List.filter (fun note ->
            intervals |> List.exists (fun (start, dest) -> start < note.Row && note.Row < dest)
            |> not)
        |> List.map (fun note ->
            match note.TailRow with
            | Some tailRow ->
                let truncated =
                    intervals
                    |> List.tryFind (fun (start, dest) -> start < tailRow && tailRow < dest)

                match truncated with
                | Some(start, _) -> { note with TailRow = Some start }
                | None -> note
            | None -> note)

    type private TimingEvent =
        | BpmEvt of beat: float * value: float
        | StopEvt of beat: float * value: float

    type private PreprocessState =
        {
            PrevBeat: float
            Bpm: float
            TimeOfs: float
            WarpStart: float
            PreWarpBpm: float
            OutBpm: (float * float) list
            OutStop: (float * float) list
            OutWarp: (float * float) list
        }

    /// Normalizes negative BPMs and negative stops into warps, matching
    /// StepMania's SMLoader::ProcessBPMsAndStops.
    let preprocess
        (timing: Timing)
        : Result<float * (float * float) list * (float * float) list * (float * float) list, UrcError> =
        let bpms = timing.Bpms |> List.sortBy fst
        let stops = timing.Stops |> List.sortBy fst

        let nonNegativeStops, initialOffset =
            stops
            |> List.fold
                (fun (accStops, accOffset) (beat, pause) ->
                    if beat < 0.0 then
                        (accStops, accOffset - pause)
                    else
                        ((beat, pause) :: accStops, accOffset))
                ([], timing.Offset)

        let sortedStops = nonNegativeStops |> List.rev

        let initialBpm, bpmRest =
            let beforeZero, afterZero = bpms |> List.partition (fun (beat, _) -> beat <= 0.0)

            let bpm =
                match List.tryLast beforeZero with
                | Some(_, value) -> value
                | None ->
                    match afterZero with
                    | (_, value) :: _ -> value
                    | [] -> 0.0

            bpm, afterZero

        if initialBpm = 0.0 then
            Error(UrcError.syntax 1 "no BPM in simfile")
        else
            let events =
                let bpmEvents = bpmRest |> List.map BpmEvt
                let stopEvents = sortedStops |> List.map StopEvt

                (bpmEvents @ stopEvents)
                |> List.sortWith (fun a b ->
                    let beatA, isBpmA =
                        match a with
                        | BpmEvt(b, _) -> b, true
                        | StopEvt(b, _) -> b, false

                    let beatB, isBpmB =
                        match b with
                        | BpmEvt(b, _) -> b, true
                        | StopEvt(b, _) -> b, false

                    if beatA <> beatB then
                        compare beatA beatB
                    elif isBpmA = isBpmB then
                        0
                    elif isBpmA then
                        -1
                    else
                        1)

            let initialState =
                {
                    PrevBeat = 0.0
                    Bpm = initialBpm
                    TimeOfs = 0.0
                    WarpStart = -1.0
                    PreWarpBpm = 0.0
                    OutBpm = if initialBpm > 0.0 && initialBpm <= FastBpmWarp then [ (0.0, initialBpm) ] else []
                    OutStop = []
                    OutWarp = []
                }

            let folder (state: PreprocessState) (event: TimingEvent) =
                let beat =
                    match event with
                    | BpmEvt(b, _)
                    | StopEvt(b, _) -> b

                let stateAfterTime =
                    if state.Bpm <= FastBpmWarp then
                        let nextTimeOfs = state.TimeOfs + ((beat - state.PrevBeat) * 60.0 / state.Bpm)

                        if state.WarpStart >= 0.0 && state.Bpm > 0.0 && nextTimeOfs > 0.0 then
                            let warpEnd = beat - (nextTimeOfs * state.Bpm / 60.0)
                            let outWarp = (state.WarpStart, warpEnd - state.WarpStart) :: state.OutWarp

                            let outBpm =
                                if state.Bpm <> state.PreWarpBpm then
                                    (state.WarpStart, state.Bpm) :: state.OutBpm
                                else
                                    state.OutBpm

                            { state with
                                PrevBeat = beat
                                TimeOfs = nextTimeOfs
                                WarpStart = -1.0
                                OutWarp = outWarp
                                OutBpm = outBpm
                            }
                        else
                            { state with
                                PrevBeat = beat
                                TimeOfs = nextTimeOfs
                            }
                    else
                        { state with PrevBeat = beat }

                match event with
                | BpmEvt(_, value) ->
                    if stateAfterTime.WarpStart < 0.0 && (value < 0.0 || value > FastBpmWarp) then
                        { stateAfterTime with
                            WarpStart = beat
                            PreWarpBpm = stateAfterTime.Bpm
                            TimeOfs = 0.0
                            Bpm = value
                        }
                    elif stateAfterTime.WarpStart < 0.0 then
                        { stateAfterTime with
                            Bpm = value
                            OutBpm = (beat, value) :: stateAfterTime.OutBpm
                        }
                    else
                        { stateAfterTime with Bpm = value }
                | StopEvt(_, value) ->
                    if stateAfterTime.WarpStart < 0.0 && value < 0.0 then
                        { stateAfterTime with
                            WarpStart = beat
                            PreWarpBpm = stateAfterTime.Bpm
                            TimeOfs = value
                        }
                    elif stateAfterTime.WarpStart < 0.0 then
                        { stateAfterTime with OutStop = (beat, value) :: stateAfterTime.OutStop }
                    else
                        let nextTimeOfs = stateAfterTime.TimeOfs + value

                        if value > 0.0 && nextTimeOfs > 0.0 then
                            let outWarp = (stateAfterTime.WarpStart, beat - stateAfterTime.WarpStart) :: stateAfterTime.OutWarp
                            let outStop = (beat, nextTimeOfs) :: stateAfterTime.OutStop

                            if stateAfterTime.Bpm < 0.0 || stateAfterTime.Bpm > FastBpmWarp then
                                { stateAfterTime with
                                    TimeOfs = 0.0
                                    WarpStart = beat
                                    OutWarp = outWarp
                                    OutStop = outStop
                                }
                            else
                                let outBpm =
                                    if stateAfterTime.Bpm <> stateAfterTime.PreWarpBpm then
                                        (stateAfterTime.WarpStart, stateAfterTime.Bpm) :: stateAfterTime.OutBpm
                                    else
                                        stateAfterTime.OutBpm

                                { stateAfterTime with
                                    TimeOfs = nextTimeOfs
                                    WarpStart = -1.0
                                    OutBpm = outBpm
                                    OutWarp = outWarp
                                    OutStop = outStop
                                }
                        else
                            { stateAfterTime with TimeOfs = nextTimeOfs }

            let finalState = events |> List.fold folder initialState

            let outBpm, outWarp =
                if finalState.WarpStart >= 0.0 then
                    let neverEnds = finalState.Bpm < 0.0 || finalState.Bpm > FastBpmWarp
                    let warpEnd =
                        if neverEnds then
                            99999999.0
                        else
                            finalState.PrevBeat - (finalState.TimeOfs * finalState.Bpm / 60.0)

                    let nextOutWarp = (finalState.WarpStart, warpEnd - finalState.WarpStart) :: finalState.OutWarp

                    let nextOutBpm =
                        if finalState.Bpm <> finalState.PreWarpBpm then
                            (finalState.WarpStart, finalState.Bpm) :: finalState.OutBpm
                        else
                            finalState.OutBpm

                    nextOutBpm, nextOutWarp
                else
                    finalState.OutBpm, finalState.OutWarp

            Ok(initialOffset, List.rev outBpm, List.rev finalState.OutStop, List.rev outWarp)
