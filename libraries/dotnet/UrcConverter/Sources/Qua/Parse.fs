namespace UrcConverter.Sources.Qua

module Parse =

    open System
    open System.Globalization
    open System.IO
    open FsToolkit.ErrorHandling
    open YamlDotNet.RepresentationModel
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Qua.Model

    let private (|Scalar|_|) (node: YamlNode) =
        match node with
        | :? YamlScalarNode as scalar -> Some scalar.Value
        | _ -> None

    let private (|Mapping|_|) (node: YamlNode) =
        match node with
        | :? YamlMappingNode as mapping -> Some mapping
        | _ -> None

    let private (|Sequence|_|) (node: YamlNode) =
        match node with
        | :? YamlSequenceNode as sequence -> Some(sequence.Children |> Seq.toList)
        | _ -> None

    let private tryFind (mapping: YamlMappingNode) (key: string) : YamlNode option =
        match mapping.Children.TryGetValue(YamlScalarNode(key)) with
        | true, value -> Some value
        | false, _ -> None

    let private number (mapping: YamlMappingNode) (key: string) : Result<float, UrcError> =
        match tryFind mapping key with
        | Some(Scalar text) ->
            match Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value -> Result.Ok value
            | false, _ -> Result.Error(UrcError.Syntax(1, $"invalid {key}: '{text}'"))
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
        | Some(Sequence children) ->
            children
            |> List.traverseResultM (function
                | Mapping entry -> Result.Ok entry
                | _ -> Result.Error(UrcError.Syntax(1, $"{key} must be a list of mappings")))
        | Some _ -> Result.Error(UrcError.Syntax(1, $"{key} must be a list of mappings"))

    let private scalarText (mapping: YamlMappingNode) (key: string) : string option =
        match tryFind mapping key with
        | Some(Scalar text) -> Some text
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
                | Mapping root ->
                    result {
                        let! mode = numberOr root "Mode" 1.0 |> Result.map int

                        let hasScratchKey =
                            scalarText root "HasScratchKey"
                            |> Option.map (fun value -> value = "true")
                            |> Option.defaultValue false

                        let! timingPoints =
                            entries root "TimingPoints"
                            |> Result.bind (
                                List.traverseResultM (fun entry ->
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
                                    }))

                        let! svPoints =
                            entries root "ScrollSpeedFactors"
                            |> Result.bind (
                                List.traverseResultM (fun entry ->
                                    result {
                                        let! startTime =
                                            number entry "StartTime" |> Result.map Shared.roundMs

                                        let! multiplier = number entry "Multiplier"

                                        return
                                            {
                                                StartTime = startTime
                                                Multiplier = multiplier
                                            }
                                    }))

                        let! hitObjects =
                            entries root "HitObjects"
                            |> Result.bind (
                                List.traverseResultM (fun entry ->
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
                                    }))

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
