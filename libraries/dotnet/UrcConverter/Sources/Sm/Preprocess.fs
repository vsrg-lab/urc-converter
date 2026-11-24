namespace UrcConverter.Sources.Sm

module internal Preprocess =

    open UrcConverter
    open UrcConverter.Sources.Sm.Model

    [<Literal>]
    let FastBpmWarp = 9999999.0

    let rowsOf (beats: float) : int64 =
        let value = beats * 48.0

        if value >= 0.0 then
            int64 (value + 0.5)
        else
            int64 (value - 0.5)

    /// Merges warp segments into [start, dest) row intervals; overlapping
    /// warps adopt the greater destination (StepMania semantics).
    let warpIntervals (warpSegs: (float * float) list) : (int64 * int64) list =
        warpSegs
        |> List.filter (fun (_, length) -> length > 0.0)
        |> List.map (fun (beat, length) ->
            let start = rowsOf beat
            start, start + rowsOf length)
        |> List.filter (fun (start, dest) -> dest > start)
        |> List.sortBy fst
        |> List.fold
            (fun (merged: (int64 * int64) list) (start, dest) ->
                match merged with
                | (lastStart, lastDest) :: tail when start < lastDest -> (lastStart, max lastDest dest) :: tail
                | _ -> (start, dest) :: merged)
            []
        |> List.rev

    /// Drops notes strictly inside a warp and truncates tails that end inside
    /// one to the warp start row.
    let filterNotes (notes: SmNote list) (intervals: (int64 * int64) list) : SmNote list =
        notes
        |> List.filter (fun note ->
            let noteRow = int64 note.Row

            intervals
            |> List.exists (fun (start, dest) -> start < noteRow && noteRow < dest)
            |> not)
        |> List.map (fun note ->
            match note.TailRow with
            | Some tailRow ->
                let tailRow64 = int64 tailRow

                match intervals |> List.tryFind (fun (start, dest) -> start < tailRow64 && tailRow64 < dest) with
                | Some(start, _) -> { note with TailRow = Some(int start) }
                | None -> note
            | None -> note)

    type private TimingEvent =
        | BpmEvt of beat: float * value: float
        | StopEvt of beat: float * value: float

    [<RequireQualifiedAccess>]
    type private WarpState =
        | NotWarping
        | Warping of startBeat: float * preWarpBpm: float

    type private PreprocessState =
        {
            PrevBeat: float
            Bpm: float
            TimeOfs: float
            Warp: WarpState
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
                        accStops, accOffset - pause
                    else
                        (beat, pause) :: accStops, accOffset)
                ([], timing.Offset)

        let sortedStops = nonNegativeStops |> List.rev

        let initialBpm, bpmRest =
            let beforeZero, afterZero = bpms |> List.partition (fun (beat, _) -> beat <= 0.0)

            match List.tryLast beforeZero with
            | Some(_, value) -> value, afterZero
            | None ->
                match afterZero with
                | (_, value) :: rest -> value, rest
                | [] -> 0.0, []

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
                        | BpmEvt(beat, _) -> beat, true
                        | StopEvt(beat, _) -> beat, false

                    let beatB, isBpmB =
                        match b with
                        | BpmEvt(beat, _) -> beat, true
                        | StopEvt(beat, _) -> beat, false

                    if beatA <> beatB then compare beatA beatB
                    elif isBpmA = isBpmB then 0
                    elif isBpmA then -1
                    else 1)

            let initialState =
                if initialBpm > 0.0 && initialBpm <= FastBpmWarp then
                    {
                        PrevBeat = 0.0
                        Bpm = initialBpm
                        TimeOfs = 0.0
                        Warp = WarpState.NotWarping
                        OutBpm = [ 0.0, initialBpm ]
                        OutStop = []
                        OutWarp = []
                    }
                else
                    {
                        PrevBeat = 0.0
                        Bpm = initialBpm
                        TimeOfs = 0.0
                        Warp = WarpState.Warping(0.0, 0.0)
                        OutBpm = []
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

                        match state.Warp with
                        | WarpState.Warping(warpStart, preWarpBpm) when state.Bpm > 0.0 && nextTimeOfs > 0.0 ->
                            let warpEnd = beat - (nextTimeOfs * state.Bpm / 60.0)
                            let outWarp = (warpStart, warpEnd - warpStart) :: state.OutWarp

                            let outBpm =
                                if state.Bpm <> preWarpBpm then
                                    (warpStart, state.Bpm) :: state.OutBpm
                                else
                                    state.OutBpm

                            { state with
                                PrevBeat = beat
                                TimeOfs = nextTimeOfs
                                Warp = WarpState.NotWarping
                                OutWarp = outWarp
                                OutBpm = outBpm
                            }
                        | _ ->
                            { state with
                                PrevBeat = beat
                                TimeOfs = nextTimeOfs
                            }
                    else
                        { state with PrevBeat = beat }

                match event, stateAfterTime.Warp with
                | BpmEvt(_, value), WarpState.NotWarping when value < 0.0 || value > FastBpmWarp ->
                    { stateAfterTime with
                        Warp = WarpState.Warping(beat, stateAfterTime.Bpm)
                        TimeOfs = 0.0
                        Bpm = value
                    }
                | BpmEvt(_, value), WarpState.NotWarping ->
                    { stateAfterTime with
                        Bpm = value
                        OutBpm = (beat, value) :: stateAfterTime.OutBpm
                    }
                | BpmEvt(_, value), WarpState.Warping _ ->
                    { stateAfterTime with Bpm = value }
                | StopEvt(_, value), WarpState.NotWarping when value < 0.0 ->
                    { stateAfterTime with
                        Warp = WarpState.Warping(beat, stateAfterTime.Bpm)
                        TimeOfs = value
                    }
                | StopEvt(_, value), WarpState.NotWarping ->
                    { stateAfterTime with
                        OutStop = (beat, value) :: stateAfterTime.OutStop
                    }
                | StopEvt(_, value), WarpState.Warping(warpStart, preWarpBpm) ->
                    let nextTimeOfs = stateAfterTime.TimeOfs + value

                    if value > 0.0 && nextTimeOfs > 0.0 then
                        let outWarp = (warpStart, beat - warpStart) :: stateAfterTime.OutWarp
                        let outStop = (beat, nextTimeOfs) :: stateAfterTime.OutStop

                        if stateAfterTime.Bpm < 0.0 || stateAfterTime.Bpm > FastBpmWarp then
                            { stateAfterTime with
                                TimeOfs = 0.0
                                Warp = WarpState.Warping(beat, preWarpBpm)
                                OutWarp = outWarp
                                OutStop = outStop
                            }
                        else
                            let outBpm =
                                if stateAfterTime.Bpm <> preWarpBpm then
                                    (warpStart, stateAfterTime.Bpm) :: stateAfterTime.OutBpm
                                else
                                    stateAfterTime.OutBpm

                            { stateAfterTime with
                                TimeOfs = nextTimeOfs
                                Warp = WarpState.NotWarping
                                OutBpm = outBpm
                                OutWarp = outWarp
                                OutStop = outStop
                            }
                    else
                        { stateAfterTime with TimeOfs = nextTimeOfs }

            let finalState = events |> List.fold folder initialState

            let outBpm, outWarp =
                match finalState.Warp with
                | WarpState.Warping(warpStart, preWarpBpm) ->
                    let neverEnds = finalState.Bpm < 0.0 || finalState.Bpm > FastBpmWarp

                    let warpEnd =
                        if neverEnds then
                            99999999.0
                        else
                            finalState.PrevBeat - (finalState.TimeOfs * finalState.Bpm / 60.0)

                    let nextOutWarp = (warpStart, warpEnd - warpStart) :: finalState.OutWarp

                    let nextOutBpm =
                        if not neverEnds && finalState.Bpm <> preWarpBpm then
                            (warpStart, finalState.Bpm) :: finalState.OutBpm
                        else
                            finalState.OutBpm

                    nextOutBpm, nextOutWarp
                | WarpState.NotWarping -> finalState.OutBpm, finalState.OutWarp

            if List.isEmpty outBpm then
                Error(UrcError.syntax 1 "no positive BPM in simfile")
            else
                Ok(initialOffset, List.rev outBpm, List.rev finalState.OutStop, List.rev outWarp)
