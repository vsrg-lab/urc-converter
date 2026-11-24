namespace UrcConverter.Sources.Qua

[<RequireQualifiedAccess>]
module Convert =

    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Qua.Model

    let private modeKeys =
        function
        | 1 -> Some 4
        | 2 -> Some 7
        | 3 -> Some 1
        | 4 -> Some 2
        | 5 -> Some 3
        | 6 -> Some 5
        | 7 -> Some 6
        | 8 -> Some 8
        | 9 -> Some 9
        | 10 -> Some 10
        | _ -> None

    let private nonEmpty (value: string option) =
        value |> Option.filter (fun text -> text <> "")

    let private commonBpm (nonzeroTps: TimingPoint list) (hitObjects: HitObject list) : float =
        match hitObjects with
        | [] -> abs nonzeroTps.Head.Bpm
        | _ ->
            let initialLastTime =
                hitObjects
                |> List.map (fun obj -> if not obj.Mine && obj.EndTime > 0 then obj.EndTime else obj.StartTime)
                |> List.max

            let addDuration (acc: (float * int) list) (bpm: float) (duration: int) =
                if List.exists (fun (b, _) -> b = bpm) acc then
                    acc |> List.map (fun (b, d) -> if b = bpm then b, d + duration else b, d)
                else
                    acc @ [ bpm, duration ]

            let indexedRev = nonzeroTps |> List.indexed |> List.rev

            let durations, _ =
                indexedRev
                |> List.fold
                    (fun (acc, lastTime) (index, point) ->
                        if point.StartTime > lastTime then
                            acc, lastTime
                        else
                            let duration = lastTime - (if index = 0 then 0 else point.StartTime)
                            addDuration acc (abs point.Bpm) duration, point.StartTime)
                    ([], initialLastTime)

            match durations with
            | [] -> abs nonzeroTps.Head.Bpm
            | entries ->
                entries
                |> List.fold
                    (fun (bestBpm, bestDur) (bpm, dur) ->
                        if dur > bestDur then bpm, dur else bestBpm, bestDur)
                    (abs nonzeroTps.Head.Bpm, -1)
                |> fst

    let private resolveTiming
        (qua: QuaMap)
        (firstNoteTime: int)
        : Result<(int * float * int * int) list * (int * float) list * (int * float * int * int) list, UrcError> =
        if List.isEmpty qua.TimingPoints then
            Error(UrcError.syntax 1 ".qua: no BPM timing point")
        else
            let tpByTime =
                qua.TimingPoints
                |> List.indexed
                |> List.sortBy (fun (i, tp) -> tp.StartTime, i)
                |> List.map (fun (_, tp) -> tp.StartTime, tp)
                |> Map.ofList

            let svByTime =
                qua.SvPoints
                |> List.indexed
                |> List.sortBy (fun (i, sv) -> sv.StartTime, i)
                |> List.map (fun (_, sv) -> sv.StartTime, sv.Multiplier)
                |> Map.ofList

            let dedupTps = tpByTime |> Map.toList |> List.map snd
            let nonzeroTps = dedupTps |> List.filter (fun tp -> tp.Bpm <> 0.0)

            match nonzeroTps with
            | [] -> Error(UrcError.syntax 1 ".qua: no positive BPM timing point")
            | firstNonZero :: _ ->
                let fallbackBpm = abs firstNonZero.Bpm

                let baseBpm =
                    if qua.BpmDoesNotAffectSv then
                        commonBpm nonzeroTps qua.HitObjects
                    else
                        0.0

                let firstTp = List.head dedupTps
                let initEffBpm = if firstTp.Bpm <> 0.0 then abs firstTp.Bpm else fallbackBpm

                let initBpmFactor =
                    if firstTp.Bpm > 0.0 then 1.0
                    elif firstTp.Bpm < 0.0 then -1.0
                    else 0.0

                let initBeats = firstTp.Signature
                let initRawSv = if qua.BpmDoesNotAffectSv then qua.InitialScrollVelocity else 1.0

                let allTimes =
                    (tpByTime |> Map.keys |> Seq.toList) @ (svByTime |> Map.keys |> Seq.toList)
                    |> List.distinct
                    |> List.sort

                let initBpms, initSvs =
                    match allTimes with
                    | firstTime :: _ when qua.BpmDoesNotAffectSv && firstTime > firstNoteTime ->
                        let rawMult =
                            if initEffBpm = baseBpm then
                                initRawSv
                            else
                                initRawSv * (baseBpm / initEffBpm)

                        let initMult = if rawMult = 0.0 then 0.0 else rawMult

                        if initMult <> 1.0 then
                            [ firstNoteTime, initEffBpm, initBeats, 4 ], [ firstNoteTime, initMult ]
                        else
                            [], []
                    | _ -> [], []

                let _, _, _, _, revBpms, revSvs =
                    allTimes
                    |> List.fold
                        (fun (effBpm, bpmFactor, beats, rawSv, accBpms, accSvs) time ->
                            let tp = Map.tryFind time tpByTime

                            let nextRawSv =
                                match Map.tryFind time svByTime with
                                | Some mult -> mult
                                | None when tp.IsSome && not qua.BpmDoesNotAffectSv -> 1.0
                                | None -> rawSv

                            let nextEffBpm, nextBpmFactor, nextBeats =
                                match tp with
                                | Some point ->
                                    if point.Bpm > 0.0 then
                                        point.Bpm, 1.0, point.Signature
                                    elif point.Bpm < 0.0 then
                                        abs point.Bpm, -1.0, point.Signature
                                    else
                                        effBpm, 0.0, point.Signature
                                | None -> effBpm, bpmFactor, beats

                            let rawMult =
                                if qua.BpmDoesNotAffectSv then
                                    if nextEffBpm = baseBpm then
                                        nextRawSv
                                    else
                                        nextRawSv * (baseBpm / nextEffBpm)
                                else
                                    nextRawSv * nextBpmFactor

                            let effMult = if rawMult = 0.0 then 0.0 else rawMult

                            nextEffBpm,
                            nextBpmFactor,
                            nextBeats,
                            nextRawSv,
                            (time, nextEffBpm, nextBeats, 4) :: accBpms,
                            (time, effMult) :: accSvs)
                        (initEffBpm, initBpmFactor, initBeats, initRawSv, List.rev initBpms, List.rev initSvs)

                let anchorPoints =
                    nonzeroTps |> List.map (fun tp -> tp.StartTime, abs tp.Bpm, tp.Signature, 4)

                Ok(List.rev revBpms, List.rev revSvs, anchorPoints)

    /// Maps a Quaver chart onto a URC chart.
    let convert (qua: QuaMap) : Result<Chart, UrcError> =
        result {
            let! keys =
                modeKeys qua.Mode
                |> Result.requireSome (UrcError.unsupportedVersion 1 $"unsupported Quaver mode: {qua.Mode}")

            let specialKeys = if qua.HasScratchKey then 1 else 0

            let firstNoteTime =
                match qua.HitObjects with
                | [] -> 0
                | objects -> objects |> List.map _.StartTime |> List.min

            let! bpmPoints, svPoints, anchorPoints = resolveTiming qua firstNoteTime

            let! timing =
                Shared.buildTiming
                    bpmPoints
                    svPoints
                    firstNoteTime
                    ".qua"
                    (Shared.firstDownbeatAfter anchorPoints firstNoteTime)

            let total = keys + specialKeys

            let! notes =
                qua.HitObjects
                |> List.traverseResultM (fun obj ->
                    let lane = obj.Lane - 1

                    if lane < 0 || lane >= total then
                        Error(UrcError.syntax 1 $"lane out of range: {obj.Lane}")
                    elif obj.Mine then
                        Ok
                            [
                                {
                                    TimestampMs = obj.StartTime - firstNoteTime
                                    Lane = lane
                                    Type = NoteType.M
                                }
                            ]
                    elif obj.EndTime <> 0 then
                        if obj.EndTime < obj.StartTime then
                            Error(UrcError.syntax 1 $"hold ends before it starts: {obj.EndTime} < {obj.StartTime}")
                        elif obj.EndTime = obj.StartTime then
                            Error(UrcError.zeroLengthHold 1 $"zero-length hold on lane {lane} at {obj.StartTime}ms")
                        else
                            Ok
                                [
                                    {
                                        TimestampMs = obj.StartTime - firstNoteTime
                                        Lane = lane
                                        Type = NoteType.LS
                                    }
                                    {
                                        TimestampMs = obj.EndTime - firstNoteTime
                                        Lane = lane
                                        Type = NoteType.LE
                                    }
                                ]
                    else
                        Ok
                            [
                                {
                                    TimestampMs = obj.StartTime - firstNoteTime
                                    Lane = lane
                                    Type = NoteType.N
                                }
                            ])
                |> Result.map List.concat

            do! Shared.checkHoldOverlap notes

            let title = nonEmpty qua.Title
            let artist = nonEmpty qua.Artist
            let creator = nonEmpty qua.Creator
            let difficultyName = nonEmpty qua.DifficultyName

            match title, artist, creator, difficultyName with
            | Some title, Some artist, Some creator, Some version ->
                return
                    {
                        FormatVersion = { Major = 1; Minor = 1 }
                        Metadata =
                            {
                                Original = "Quaver"
                                Title = title
                                Artist = artist
                                Creator = creator
                                Version = version
                            }
                        Judgment = None
                        Layout =
                            {
                                Keys = keys
                                SpecialKeys = specialKeys
                                SpecialLanes = if qua.HasScratchKey then Some [ keys ] else None
                            }
                        TimingPoints = timing
                        Notes = notes
                    }
            | _ ->
                let missing =
                    [
                        "Title", title
                        "Artist", artist
                        "Creator", creator
                        "DifficultyName", difficultyName
                    ]
                    |> List.choose (fun (name, value) -> if Option.isNone value then Some name else None)
                    |> String.concat ", "

                return! Error(UrcError.syntax 1 $"missing metadata: {missing}")
        }
