namespace UrcConverter.Sources.Qua

[<RequireQualifiedAccess>]
module Convert =

    open System.Collections.Generic
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
            let mutable lastTime =
                hitObjects
                |> List.map (fun obj -> if not obj.Mine && obj.EndTime > 0 then obj.EndTime else obj.StartTime)
                |> List.max

            let durations = Dictionary<float, int>()
            let order = ResizeArray<float>()
            let tps = List.toArray nonzeroTps

            for index = tps.Length - 1 downto 0 do
                let point = tps[index]

                if point.StartTime <= lastTime then
                    let duration = lastTime - (if index = 0 then 0 else point.StartTime)
                    lastTime <- point.StartTime
                    let bpm = abs point.Bpm

                    match durations.TryGetValue(bpm) with
                    | true, existing -> durations[bpm] <- existing + duration
                    | false, _ ->
                        durations[bpm] <- duration
                        order.Add(bpm)

            if order.Count = 0 then
                abs nonzeroTps.Head.Bpm
            else
                let mutable bestBpm = abs nonzeroTps.Head.Bpm
                let mutable bestDur = -1

                for bpm in order do
                    let dur = durations[bpm]

                    if dur > bestDur then
                        bestDur <- dur
                        bestBpm <- bpm

                bestBpm

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

                let initBpms, initSvs, initLastBpm, initLastSv =
                    match allTimes with
                    | firstTime :: _ when qua.BpmDoesNotAffectSv && firstTime > firstNoteTime ->
                        let rawMult =
                            if initEffBpm = baseBpm then
                                initRawSv
                            else
                                initRawSv * (baseBpm / initEffBpm)

                        let initMult = if rawMult = 0.0 then 0.0 else rawMult

                        if initMult <> 1.0 then
                            [ firstNoteTime, initEffBpm, initBeats, 4 ],
                            [ firstNoteTime, initMult ],
                            Some(initEffBpm, initBeats),
                            initMult
                        else
                            [], [], None, 1.0
                    | _ -> [], [], None, 1.0

                let _, _, _, _, _, _, revBpms, revSvs =
                    allTimes
                    |> List.fold
                        (fun (effBpm, bpmFactor, beats, rawSv, lastBpm, lastSv, accBpms, accSvs) time ->
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
                            let bpmState = Some(nextEffBpm, nextBeats)

                            let nextAccBpms, nextLastBpm =
                                if bpmState <> lastBpm then
                                    (time, nextEffBpm, nextBeats, 4) :: accBpms, bpmState
                                else
                                    accBpms, lastBpm

                            let nextAccSvs, nextLastSv =
                                if effMult <> lastSv then
                                    (time, effMult) :: accSvs, effMult
                                else
                                    accSvs, lastSv

                            nextEffBpm,
                            nextBpmFactor,
                            nextBeats,
                            nextRawSv,
                            nextLastBpm,
                            nextLastSv,
                            nextAccBpms,
                            nextAccSvs)
                        (initEffBpm,
                         initBpmFactor,
                         initBeats,
                         initRawSv,
                         initLastBpm,
                         initLastSv,
                         List.rev initBpms,
                         List.rev initSvs)

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
