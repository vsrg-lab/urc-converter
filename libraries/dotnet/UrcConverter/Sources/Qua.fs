namespace UrcConverter.Sources

open System
open System.Globalization
open UrcConverter

module Qua =

    open System.IO
    open FsToolkit.ErrorHandling
    open YamlDotNet.RepresentationModel

    let private modeKeys =
        [
            (1, 4)
            (2, 7)
            (3, 1)
            (4, 2)
            (5, 3)
            (6, 5)
            (7, 6)
            (8, 8)
            (9, 9)
            (10, 10)
        ]

    type TimingPoint =
        {
            StartTime: int
            Bpm: float
            Signature: int
        }

    type SvPoint = { StartTime: int; Multiplier: float }

    type HitObject =
        {
            StartTime: int
            Lane: int
            EndTime: int
            Mine: bool
        }

    type QuaMap =
        {
            Mode: int
            HasScratchKey: bool
            Title: string option
            Artist: string option
            Creator: string option
            DifficultyName: string option
            TimingPoints: TimingPoint list
            SvPoints: SvPoint list
            HitObjects: HitObject list
        }

    let private tryFind (mapping: YamlMappingNode) (key: string) : YamlNode option =
        let mutable value = Unchecked.defaultof<YamlNode>

        if mapping.Children.TryGetValue(YamlScalarNode(key), &value) then
            Some value
        else
            None

    let private number (mapping: YamlMappingNode) (key: string) : Result<float, UrcError> =
        match tryFind mapping key with
        | Some(:? YamlScalarNode as scalar) ->
            match
                Double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture)
            with
            | true, value -> Result.Ok value
            | false, _ -> Result.Error(UrcError.Syntax(1, $"invalid {key}: '{scalar.Value}'"))
        | Some _ -> Result.Error(UrcError.Syntax(1, $"invalid {key}"))
        | None -> Result.Error(UrcError.Syntax(1, $"missing {key}"))

    let private numberOr
        (mapping: YamlMappingNode)
        (key: string)
        (fallback: float)
        : Result<float, UrcError> =
        match tryFind mapping key with
        | None -> Result.Ok fallback
        | Some _ -> number mapping key

    let private entries
        (mapping: YamlMappingNode)
        (key: string)
        : Result<YamlMappingNode list, UrcError> =
        match tryFind mapping key with
        | None -> Result.Ok []
        | Some(:? YamlSequenceNode as sequence) ->
            sequence.Children
            |> Seq.toList
            |> List.map (fun node ->
                match node with
                | :? YamlMappingNode as entry -> Result.Ok entry
                | _ -> Result.Error(UrcError.Syntax(1, $"{key} must be a list of mappings")))
            |> Shared.sequence
        | Some _ -> Result.Error(UrcError.Syntax(1, $"{key} must be a list of mappings"))

    let private scalarText (mapping: YamlMappingNode) (key: string) : string option =
        match tryFind mapping key with
        | Some(:? YamlScalarNode as scalar) -> Some scalar.Value
        | _ -> None

    let parse (text: string) : Result<QuaMap, UrcError> =
        let stream = YamlStream()

        let loaded =
            try
                use reader = new StringReader(text)
                stream.Load(reader)
                Result.Ok()
            with error ->
                let line =
                    match error with
                    | :? YamlDotNet.Core.YamlException as yaml -> int yaml.Start.Line
                    | _ -> 1

                Result.Error(UrcError.Syntax(line, $"invalid YAML: {error.Message}"))

        match loaded with
        | Result.Error error -> Result.Error error
        | Result.Ok() ->
            match stream.Documents |> Seq.toList with
            | doc :: _ ->
                match doc.RootNode with
                | :? YamlMappingNode as root ->
                    result {
                        let! mode = numberOr root "Mode" 1.0 |> Result.map int

                        let hasScratchKey =
                            scalarText root "HasScratchKey"
                            |> Option.map (fun value -> value = "true")
                            |> Option.defaultValue false

                        let! timingPoints =
                            entries root "TimingPoints"
                            |> Result.bind (fun list ->
                                list
                                |> List.map (fun entry ->
                                    result {
                                        let! bpm = number entry "Bpm"

                                        let! startTime =
                                            number entry "StartTime" |> Result.map Shared.roundMs

                                        let! signature =
                                            numberOr entry "Signature" 4.0 |> Result.map int

                                        let signature = if signature = 0 then 4 else signature

                                        if signature <> 3 && signature <> 4 then
                                            return!
                                                Result.Error(
                                                    UrcError.Syntax(
                                                        1,
                                                        $"unsupported time signature: {signature}"
                                                    )
                                                )

                                        return
                                            {
                                                StartTime = startTime
                                                Bpm = bpm
                                                Signature = signature
                                            }
                                    })
                                |> Shared.sequence)

                        let! svPoints =
                            entries root "ScrollSpeedFactors"
                            |> Result.bind (fun list ->
                                list
                                |> List.map (fun entry ->
                                    result {
                                        let! startTime =
                                            number entry "StartTime" |> Result.map Shared.roundMs

                                        let! multiplier = number entry "Multiplier"

                                        return
                                            {
                                                StartTime = startTime
                                                Multiplier = multiplier
                                            }
                                    })
                                |> Shared.sequence)

                        let! hitObjects =
                            entries root "HitObjects"
                            |> Result.bind (fun list ->
                                list
                                |> List.map (fun entry ->
                                    result {
                                        let! startTime =
                                            number entry "StartTime" |> Result.map Shared.roundMs

                                        let! lane = number entry "Lane" |> Result.map int

                                        let! endTime =
                                            numberOr entry "EndTime" 0.0
                                            |> Result.map Shared.roundMs

                                        let! objectType =
                                            numberOr entry "Type" 0.0 |> Result.map int

                                        return
                                            {
                                                StartTime = startTime
                                                Lane = lane
                                                EndTime = endTime
                                                Mine = objectType = 1
                                            }
                                    })
                                |> Shared.sequence)

                        return
                            {
                                Mode = mode
                                HasScratchKey = hasScratchKey
                                Title = scalarText root "Title"
                                Artist = scalarText root "Artist"
                                Creator = scalarText root "Creator"
                                DifficultyName = scalarText root "DifficultyName"
                                TimingPoints = timingPoints
                                SvPoints = svPoints
                                HitObjects = hitObjects
                            }
                    }
                | _ -> Result.Error(UrcError.Syntax(1, ".qua must be a YAML mapping"))
            | [] -> Result.Error(UrcError.Syntax(1, ".qua has no YAML document"))

    let convert (qua: QuaMap) : Result<Chart, UrcError> =
        result {
            match modeKeys |> List.tryFind (fun (mode, _) -> mode = qua.Mode) with
            | None ->
                return!
                    Result.Error(
                        UrcError.UnsupportedVersion(1, $"unsupported Quaver mode: {qua.Mode}")
                    )
            | Some(_, keys) ->
                let specialKeys = if qua.HasScratchKey then 1 else 0

                let firstNoteTime =
                    match qua.HitObjects with
                    | [] -> 0
                    | objects -> objects |> List.map _.StartTime |> List.min

                let! timing =
                    Shared.buildTiming
                        (qua.TimingPoints
                         |> List.map (fun point -> point.StartTime, point.Bpm, point.Signature))
                        (qua.SvPoints |> List.map (fun point -> point.StartTime, point.Multiplier))
                        firstNoteTime
                        ".qua"

                let total = keys + specialKeys

                let! notes =
                    qua.HitObjects
                    |> List.map (fun obj ->
                        let lane = obj.Lane - 1

                        if lane < 0 || lane >= total then
                            Result.Error(UrcError.Syntax(1, $"lane out of range: {obj.Lane}"))
                        elif obj.EndTime <> 0 && obj.EndTime < obj.StartTime then
                            Result.Error(
                                UrcError.Syntax(
                                    1,
                                    $"hold ends before it starts: {obj.EndTime} < {obj.StartTime}"
                                )
                            )
                        elif obj.EndTime <> 0 then
                            Result.Ok
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
                            Result.Ok
                                [
                                    {
                                        TimestampMs = obj.StartTime - firstNoteTime
                                        Lane = lane
                                        Type = NoteType.M
                                    }
                                ]
                        else
                            Result.Ok
                                [
                                    {
                                        TimestampMs = obj.StartTime - firstNoteTime
                                        Lane = lane
                                        Type = NoteType.N
                                    }
                                ])
                    |> Shared.sequence
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

                let missingText = String.Join(", ", missing)

                if missing <> [] then
                    return! Result.Error(UrcError.Syntax(1, $"missing metadata: {missingText}"))

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
                | _ -> return! Result.Error(UrcError.Syntax(1, $"missing metadata: {missingText}"))
        }
