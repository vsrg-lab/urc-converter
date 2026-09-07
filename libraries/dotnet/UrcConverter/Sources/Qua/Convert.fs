namespace UrcConverter.Sources.Qua

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

    let convert (qua: QuaMap) : Result<Chart, UrcError> =
        result {
            match modeKeys qua.Mode with
            | None ->
                return!
                    Error(
                        UrcError.UnsupportedVersion(1, $"unsupported Quaver mode: {qua.Mode}")
                    )
            | Some keys ->
                let specialKeys = if qua.HasScratchKey then 1 else 0

                let firstNoteTime =
                    match qua.HitObjects with
                    | [] -> 0
                    | objects -> objects |> List.map _.StartTime |> List.min

                let bpmPoints =
                    qua.TimingPoints
                    |> List.map (fun point -> point.StartTime, point.Bpm, point.Signature, 4)

                let! timing =
                    Shared.buildTiming
                        bpmPoints
                        (qua.SvPoints |> List.map (fun point -> point.StartTime, point.Multiplier))
                        firstNoteTime
                        ".qua"
                        (Shared.firstDownbeatAfter bpmPoints firstNoteTime)

                let total = keys + specialKeys

                let! notes =
                    qua.HitObjects
                    |> List.traverseResultM (fun obj ->
                        let lane = obj.Lane - 1

                        if lane < 0 || lane >= total then
                            Error(UrcError.Syntax(1, $"lane out of range: {obj.Lane}"))
                        elif obj.EndTime <> 0 && obj.EndTime < obj.StartTime then
                            Error(
                                UrcError.Syntax(
                                    1,
                                    $"hold ends before it starts: {obj.EndTime} < {obj.StartTime}"
                                )
                            )
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

                let metadata =
                    [
                        ("Title", qua.Title)
                        ("Artist", qua.Artist)
                        ("Creator", qua.Creator)
                        ("DifficultyName", qua.DifficultyName)
                    ]

                let missing =
                    metadata
                    |> List.choose (fun (name, value) ->
                        match value with
                        | Some value when value <> "" -> None
                        | _ -> Some name)

                let missingText = missing |> String.concat ", "

                if not (List.isEmpty missing) then
                    return! Error(UrcError.Syntax(1, $"missing metadata: {missingText}"))

                match qua.Title, qua.Artist, qua.Creator, qua.DifficultyName with
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
                | _ -> return! Error(UrcError.Syntax(1, $"missing metadata: {missingText}"))
        }
