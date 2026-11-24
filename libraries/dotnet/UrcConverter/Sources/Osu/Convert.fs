namespace UrcConverter.Sources.Osu

[<RequireQualifiedAccess>]
module Convert =

    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Osu.Model

    let private keyMin, keyMax = 1, 18
    let private judgmentRates = [ 100.0; 100.0; 66.67; 33.33; 16.67; 0.0 ]

    let private nonEmpty (value: string option) =
        value |> Option.filter (fun text -> text <> "")

    /// Maps an osu!mania beatmap onto a URC chart.
    let convert (beatmap: Beatmap) : Result<Chart, UrcError> =
        result {
            if beatmap.Mode <> 3 then
                return! Error(UrcError.unsupportedVersion 1 $"unsupported game mode: {beatmap.Mode}")

            let! circleSize =
                beatmap.CircleSize
                |> Result.requireSome (UrcError.syntax 1 "missing CircleSize")

            if circleSize <> truncate circleSize then
                return! Error(UrcError.syntax 1 $"CircleSize must be an integer: {circleSize}")

            let keys = int circleSize

            if keys < keyMin || keys > keyMax then
                return! Error(UrcError.syntax 1 $"CircleSize out of range: {keys}")

            if beatmap.TimingPoints |> List.exists (fun point -> point.BeatLength = 0.0) then
                return! Error(UrcError.syntax 1 "timing point with zero beat length")

            let firstNoteTime =
                match beatmap.HitObjects with
                | [] -> 0
                | objects -> objects |> List.map _.Time |> List.min

            let bpmPoints =
                beatmap.TimingPoints
                |> List.choose (fun point ->
                    if point.Uninherited then
                        Some(point.Time, 60000.0 / point.BeatLength, point.Meter, 4)
                    else
                        None)

            let svPoints =
                beatmap.TimingPoints
                |> List.choose (fun point ->
                    if not point.Uninherited then
                        Some(point.Time, -100.0 / point.BeatLength)
                    else
                        None)

            let! timing =
                Shared.buildTiming
                    bpmPoints
                    svPoints
                    firstNoteTime
                    ".osu"
                    (Shared.firstDownbeatAfter bpmPoints firstNoteTime)

            let! notes =
                beatmap.HitObjects
                |> List.traverseResultM (fun obj ->
                    let lane = min (max (obj.X * keys / 512) 0) (keys - 1)

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
