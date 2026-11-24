namespace UrcConverter.Sources.Osu

[<RequireQualifiedAccess>]
module Convert =

    open System
    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Osu.Model

    let private keyMin, keyMax = 1, 18
    let private judgmentRates = [ 100.0; 100.0; 66.67; 33.33; 16.67; 0.0 ]

    let private typeOrder =
        function
        | NoteType.N -> 0
        | NoteType.LS -> 1
        | NoteType.LE -> 2
        | NoteType.M -> 3
        | NoteType.F -> 4

    let private nonEmpty (value: string option) =
        value |> Option.filter (fun text -> text <> "")

    let private resolveTiming
        (beatmap: Beatmap)
        : Result<(int * float * int * int) list * (int * float) list * (int * float * int * int) list, UrcError> =
        let classify
            (tpMap: Map<int, float * int>, svMap: Map<int, float>)
            (point: TimingPoint)
            : Result<Map<int, float * int> * Map<int, float>, UrcError> =
            if not (Double.IsFinite point.BeatLength) || point.BeatLength = 0.0 then
                Error(UrcError.syntax 1 "timing point with zero beat length")
            else
                let isSv = not point.Uninherited || point.BeatLength < 0.0

                if isSv then
                    if point.BeatLength > 0.0 then
                        Error(UrcError.syntax 1 "inherited timing point with positive beat length")
                    else
                        Ok(tpMap, Map.add point.Time (-100.0 / point.BeatLength) svMap)
                else
                    let meter = if point.Meter > 0 then point.Meter else 4
                    Ok(Map.add point.Time (60000.0 / point.BeatLength, meter) tpMap, svMap)

        let rec foldPoints state rest =
            match rest with
            | [] -> Ok state
            | point :: tail -> classify state point |> Result.bind (fun next -> foldPoints next tail)

        foldPoints (Map.empty, Map.empty) beatmap.TimingPoints
        |> Result.bind (fun (tpByTime, svByTime) ->
            if Map.isEmpty tpByTime then
                Error(UrcError.syntax 1 ".osu: no BPM timing point")
            else
                let allTimes =
                    (tpByTime |> Map.keys |> Seq.toList) @ (svByTime |> Map.keys |> Seq.toList)
                    |> List.distinct
                    |> List.sort

                let _, _, _, revBpms, revSvs =
                    allTimes
                    |> List.fold
                        (fun (curBpm, curMeter, curSv, accBpms, accSvs) time ->
                            let tp = Map.tryFind time tpByTime

                            let nextSv =
                                match Map.tryFind time svByTime with
                                | Some sv -> sv
                                | None when tp.IsSome -> 1.0
                                | None -> curSv

                            let nextBpm, nextMeter =
                                match tp with
                                | Some(bpm, meter) -> Some bpm, meter
                                | None -> curBpm, curMeter

                            match nextBpm with
                            | None -> nextBpm, nextMeter, nextSv, accBpms, accSvs
                            | Some bpm ->
                                nextBpm,
                                nextMeter,
                                nextSv,
                                (time, bpm, nextMeter, 4) :: accBpms,
                                (time, nextSv) :: accSvs)
                        (None, 4, 1.0, [], [])

                let anchorPoints =
                    tpByTime
                    |> Map.toList
                    |> List.map (fun (time, (bpm, meter)) -> time, bpm, meter, 4)

                Ok(List.rev revBpms, List.rev revSvs, anchorPoints))

    /// Maps an osu!mania beatmap onto a URC chart.
    let convert (beatmap: Beatmap) : Result<Chart, UrcError> =
        result {
            if beatmap.Mode <> 3 then
                return! Error(UrcError.unsupportedVersion 1 $"unsupported game mode: {beatmap.Mode}")

            let! circleSize =
                beatmap.CircleSize
                |> Result.requireSome (UrcError.syntax 1 "missing CircleSize")

            if not (Double.IsFinite circleSize) || circleSize <> truncate circleSize then
                return! Error(UrcError.syntax 1 $"CircleSize must be an integer: {circleSize}")

            let keys = int circleSize

            if keys < keyMin || keys > keyMax then
                return! Error(UrcError.syntax 1 $"CircleSize out of range: {keys}")

            let firstNoteTime =
                match beatmap.HitObjects with
                | [] -> 0
                | objects -> objects |> List.map _.Time |> List.min

            let! bpmPoints, svPoints, anchorPoints = resolveTiming beatmap

            let! timing =
                Shared.buildTiming
                    bpmPoints
                    svPoints
                    firstNoteTime
                    ".osu"
                    (Shared.firstDownbeatAfter anchorPoints firstNoteTime)

            let! rawNotes =
                beatmap.HitObjects
                |> List.traverseResultM (fun obj ->
                    let lane = min (max (int (Math.Floor(float (obj.X * keys) / 512.0))) 0) (keys - 1)

                    match obj.Kind with
                    | HitObjectKind.Hold endTime when endTime < obj.Time ->
                        Error(UrcError.syntax 1 $"hold ends before it starts: {endTime} < {obj.Time}")
                    | HitObjectKind.Hold endTime when endTime = obj.Time ->
                        Error(UrcError.zeroLengthHold 1 $"zero-length hold on lane {lane} at {obj.Time}ms")
                    | HitObjectKind.Hold endTime ->
                        Ok
                            [
                                {
                                    TimestampMs = obj.Time - firstNoteTime
                                    Lane = lane
                                    Type = NoteType.LS
                                }
                                {
                                    TimestampMs = endTime - firstNoteTime
                                    Lane = lane
                                    Type = NoteType.LE
                                }
                            ]
                    | HitObjectKind.Tap ->
                        Ok
                            [
                                {
                                    TimestampMs = obj.Time - firstNoteTime
                                    Lane = lane
                                    Type = NoteType.N
                                }
                            ])
                |> Result.map List.concat

            let notes =
                rawNotes
                |> List.sortBy (fun note -> note.TimestampMs, note.Lane, typeOrder note.Type)

            do! Shared.checkHoldOverlap notes

            let judgment =
                beatmap.OverallDifficulty
                |> Option.map (fun od ->
                    let windows =
                        [ 64.0; 97.0; 127.0; 151.0; 188.0 ]
                        |> List.fold
                            (fun acc baseline ->
                                let prev = List.head acc
                                max prev (baseline - 3.0 * od + 0.5) :: acc)
                            [ 16.5 ]
                        |> List.rev

                    {
                        Windows = windows
                        Rates = judgmentRates
                    })

            let title = nonEmpty beatmap.TitleUnicode |> Option.orElse (nonEmpty beatmap.Title)
            let artist = nonEmpty beatmap.ArtistUnicode |> Option.orElse (nonEmpty beatmap.Artist)
            let creator = nonEmpty beatmap.Creator
            let version = nonEmpty beatmap.Version

            match title, artist, creator, version with
            | Some title, Some artist, Some creator, Some version ->
                return
                    {
                        FormatVersion = { Major = 1; Minor = 1 }
                        Metadata =
                            {
                                Original = "osu!mania"
                                Title = title
                                Artist = artist
                                Creator = creator
                                Version = version
                            }
                        Judgment = judgment
                        Layout =
                            {
                                Keys = keys
                                SpecialKeys = 0
                                SpecialLanes = None
                            }
                        TimingPoints = timing
                        Notes = notes
                    }
            | _ ->
                let missing =
                    [ "Title", title; "Artist", artist; "Creator", creator; "Version", version ]
                    |> List.choose (fun (name, value) -> if Option.isNone value then Some name else None)
                    |> String.concat ", "

                return! Error(UrcError.syntax 1 $"missing metadata: {missing}")
        }
