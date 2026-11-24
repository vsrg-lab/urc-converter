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

            let bpmPoints =
                qua.TimingPoints
                |> List.map (fun point -> point.StartTime, point.Bpm, point.Signature, 4)

            let svPoints =
                qua.SvPoints |> List.map (fun point -> point.StartTime, point.Multiplier)

            let! timing =
                Shared.buildTiming
                    bpmPoints
                    svPoints
                    firstNoteTime
                    ".qua"
                    (Shared.firstDownbeatAfter bpmPoints firstNoteTime)

            let total = keys + specialKeys

            let! notes =
                qua.HitObjects
                |> List.traverseResultM (fun obj ->
                    let lane = obj.Lane - 1

                    if lane < 0 || lane >= total then
                        Error(UrcError.syntax 1 $"lane out of range: {obj.Lane}")
                    elif obj.EndTime <> 0 && obj.EndTime < obj.StartTime then
                        Error(UrcError.syntax 1 $"hold ends before it starts: {obj.EndTime} < {obj.StartTime}")
                    elif obj.EndTime <> 0 && obj.EndTime = obj.StartTime then
                        Error(UrcError.zeroLengthHold 1 $"zero-length hold on lane {lane} at {obj.StartTime}ms")
                    elif obj.EndTime <> 0 then
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
                    elif obj.Mine then
                        Ok
                            [
                                {
                                    TimestampMs = obj.StartTime - firstNoteTime
                                    Lane = lane
                                    Type = NoteType.M
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
