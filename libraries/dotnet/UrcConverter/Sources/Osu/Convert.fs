namespace UrcConverter.Sources.Osu

module Convert =

    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Osu.Model

    let private keyMin, keyMax = 1, 18
    let private judgmentRates = [ 100.0; 100.0; 66.67; 33.33; 16.67; 0.0 ]

    let convert (beatmap: Beatmap) : Result<Chart, UrcError> =
        result {
            if beatmap.Mode <> 3 then
                return!
                    Result.Error(
                        UrcError.UnsupportedVersion(1, $"unsupported game mode: {beatmap.Mode}")
                    )

            match beatmap.CircleSize with
            | None -> return! Result.Error(UrcError.Syntax(1, "missing CircleSize"))
            | Some circleSize when circleSize <> truncate circleSize ->
                return!
                    Result.Error(UrcError.Syntax(1, $"CircleSize must be an integer: {circleSize}"))
            | Some circleSize ->
                let keys = int circleSize

                if keys < keyMin || keys > keyMax then
                    return! Result.Error(UrcError.Syntax(1, $"CircleSize out of range: {keys}"))

                if beatmap.TimingPoints |> List.exists (fun point -> point.BeatLength = 0.0) then
                    return! Result.Error(UrcError.Syntax(1, "timing point with zero beat length"))

                let firstNoteTime =
                    match beatmap.HitObjects with
                    | [] -> 0
                    | objects -> objects |> List.map _.Time |> List.min

                let bpmPoints =
                    beatmap.TimingPoints
                    |> List.choose (fun point ->
                        if point.Uninherited then
                            Some(point.Time, 60000.0 / point.BeatLength, point.Meter)
                        else
                            None)

                let! timing =
                    Shared.buildTiming
                        bpmPoints
                        (beatmap.TimingPoints
                         |> List.choose (fun point ->
                             if not point.Uninherited then
                                 Some(point.Time, -100.0 / point.BeatLength)
                             else
                                 None))
                        firstNoteTime
                        ".osu"
                        (Shared.firstDownbeatAfter bpmPoints firstNoteTime)

                let! notes =
                    beatmap.HitObjects
                    |> List.traverseResultM (fun obj ->
                        let lane = min (max (obj.X * keys / 512) 0) (keys - 1)

                        match obj.IsHold, obj.EndTime with
                        | true, Some endTime when endTime < obj.Time ->
                            Result.Error(
                                UrcError.Syntax(
                                    1,
                                    $"hold ends before it starts: {endTime} < {obj.Time}"
                                )
                            )
                        | true, Some endTime ->
                            Result.Ok
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
                        | true, None -> invalidOp "hold hit object must have an end time"
                        | false, _ ->
                            Result.Ok
                                [
                                    {
                                        TimestampMs = obj.Time - firstNoteTime
                                        Lane = lane
                                        Type = NoteType.N
                                    }
                                ])
                    |> Result.map List.concat

                do! Shared.checkHoldOverlap notes

                let judgment =
                    beatmap.OverallDifficulty
                    |> Option.map (fun od ->
                        let windows =
                            16.5
                            :: [
                                for baseline in [ 64.0; 97.0; 127.0; 151.0; 188.0 ] ->
                                    baseline - 3.0 * od + 0.5
                            ]

                        {
                            Windows = windows
                            Rates = judgmentRates
                        })

                let title = beatmap.TitleUnicode |> Option.orElse beatmap.Title
                let artist = beatmap.ArtistUnicode |> Option.orElse beatmap.Artist

                let metadata =
                    [
                        ("Title", title)
                        ("Artist", artist)
                        ("Creator", beatmap.Creator)
                        ("Version", beatmap.Version)
                    ]

                let missing =
                    metadata
                    |> List.choose (fun (name, value) ->
                        match value with
                        | Some value when value <> "" -> None
                        | _ -> Some name)

                let missingText = missing |> String.concat ", "

                if missing <> [] then
                    return! Result.Error(UrcError.Syntax(1, $"missing metadata: {missingText}"))

                match title, artist, beatmap.Creator, beatmap.Version with
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
                    return!
                        Result.Error(UrcError.Syntax(1, $"missing metadata: {missingText}"))
        }
