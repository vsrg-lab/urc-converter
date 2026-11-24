namespace UrcConverter.Sources.Qua

[<RequireQualifiedAccess>]
module Parse =

    open System
    open System.Globalization
    open System.IO
    open FsToolkit.ErrorHandling
    open YamlDotNet.Core
    open YamlDotNet.RepresentationModel
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Qua.Model

    let private isNullScalar (scalar: YamlScalarNode) =
        scalar.Style = ScalarStyle.Plain
        && (isNull scalar.Value
            || scalar.Value = ""
            || scalar.Value = "~"
            || scalar.Value = "null"
            || scalar.Value = "Null"
            || scalar.Value = "NULL")

    let private (|NullNode|Scalar|Mapping|Sequence|) (node: YamlNode) =
        match node with
        | :? YamlScalarNode as scalar when isNullScalar scalar -> NullNode
        | :? YamlScalarNode as scalar -> Scalar(if isNull scalar.Value then "" else scalar.Value)
        | :? YamlMappingNode as mapping -> Mapping mapping
        | :? YamlSequenceNode as sequence -> Sequence(sequence.Children |> Seq.toList)
        | _ -> NullNode

    let private tryFind (mapping: YamlMappingNode) (key: string) : YamlNode option =
        match mapping.Children.TryGetValue(YamlScalarNode(key)) with
        | true, value -> Some value
        | false, _ -> None

    let private numberOr
        (mapping: YamlMappingNode)
        (key: string)
        (fallback: float)
        : Result<float, UrcError> =
        match tryFind mapping key with
        | None
        | Some NullNode -> Ok fallback
        | Some(Scalar text) ->
            match Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value when Double.IsFinite(value) -> Ok value
            | _ -> Error(UrcError.Syntax(1, $"invalid {key}: '{text}'"))
        | Some _ -> Error(UrcError.Syntax(1, $"invalid {key}"))

    let private scalarText (mapping: YamlMappingNode) (key: string) : Result<string option, UrcError> =
        match tryFind mapping key with
        | None
        | Some NullNode -> Ok None
        | Some(Scalar text) -> Ok(Some text)
        | Some _ -> Error(UrcError.Syntax(1, $"invalid {key}"))

    let private boolOr (mapping: YamlMappingNode) (key: string) (fallback: bool) : bool =
        match tryFind mapping key with
        | Some(Scalar text) ->
            match text.Trim().ToLowerInvariant() with
            | "true" -> true
            | "false" -> false
            | _ -> fallback
        | _ -> fallback

    let private modeField (mapping: YamlMappingNode) : Result<int, UrcError> =
        match tryFind mapping "Mode" with
        | None
        | Some NullNode -> Ok 1
        | Some(Scalar text) ->
            let trimmed = text.Trim()

            match trimmed.ToLowerInvariant() with
            | "keys4" -> Ok 1
            | "keys7" -> Ok 2
            | "keys1" -> Ok 3
            | "keys2" -> Ok 4
            | "keys3" -> Ok 5
            | "keys5" -> Ok 6
            | "keys6" -> Ok 7
            | "keys8" -> Ok 8
            | "keys9" -> Ok 9
            | "keys10" -> Ok 10
            | _ ->
                match Int32.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, value -> Ok value
                | false, _ -> Error(UrcError.UnsupportedVersion(1, $"unsupported Quaver mode: {trimmed}"))
        | Some _ -> Error(UrcError.Syntax(1, "invalid Mode"))

    let private signatureField (mapping: YamlMappingNode) : Result<int, UrcError> =
        match tryFind mapping "Signature" with
        | None
        | Some NullNode -> Ok 4
        | Some(Scalar text) ->
            let trimmed = text.Trim()

            match trimmed.ToLowerInvariant() with
            | "quadruple" -> Ok 4
            | "triple" -> Ok 3
            | _ ->
                match Int32.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, 0 -> Ok 4
                | true, value when value >= 1 -> Ok value
                | true, value -> Error(UrcError.Syntax(1, $"unsupported time signature: {value}"))
                | false, _ -> Error(UrcError.Syntax(1, $"unsupported time signature: {trimmed}"))
        | Some _ -> Error(UrcError.Syntax(1, "invalid Signature"))

    let private hitObjectTypeField (mapping: YamlMappingNode) : Result<int, UrcError> =
        match tryFind mapping "Type" with
        | None
        | Some NullNode -> Ok 0
        | Some(Scalar text) ->
            let trimmed = text.Trim()

            match trimmed.ToLowerInvariant() with
            | "normal" -> Ok 0
            | "mine" -> Ok 1
            | _ ->
                match Int32.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, 0 -> Ok 0
                | true, 1 -> Ok 1
                | true, value -> Error(UrcError.Syntax(1, $"unsupported hit object type: {value}"))
                | false, _ -> Error(UrcError.Syntax(1, $"unsupported hit object type: {trimmed}"))
        | Some _ -> Error(UrcError.Syntax(1, "invalid Type"))

    let private entries
        (mapping: YamlMappingNode)
        (key: string)
        : Result<YamlMappingNode list, UrcError> =
        match tryFind mapping key with
        | None
        | Some NullNode -> Ok []
        | Some(Sequence children) ->
            children
            |> List.traverseResultM (function
                | Mapping entry -> Ok entry
                | _ -> Error(UrcError.Syntax(1, $"{key} must be a list of mappings")))
        | Some _ -> Error(UrcError.Syntax(1, $"{key} must be a list of mappings"))

    let parse (text: string) : Result<QuaMap, UrcError> =
        let cleaned = text.TrimStart('\uFEFF')
        let stream = YamlStream()

        let loaded =
            try
                use reader = new StringReader(cleaned)
                stream.Load(reader)
                Ok()
            with error ->
                let line =
                    match error with
                    | :? YamlException as yaml -> int yaml.Start.Line
                    | _ -> 1

                Error(UrcError.Syntax(line, $"invalid YAML: {error.Message}"))

        loaded
        |> Result.bind (fun () ->
            match stream.Documents |> Seq.toList with
            | doc :: _ ->
                match doc.RootNode with
                | Mapping root ->
                    result {
                        let! mode = modeField root
                        let hasScratchKey = boolOr root "HasScratchKey" false
                        let bpmDoesNotAffectSv = boolOr root "BPMDoesNotAffectScrollVelocity" false
                        let! initialScrollVelocity = numberOr root "InitialScrollVelocity" 0.0
                        let! title = scalarText root "Title"
                        let! artist = scalarText root "Artist"
                        let! creator = scalarText root "Creator"
                        let! difficultyName = scalarText root "DifficultyName"

                        let! timingPoints =
                            entries root "TimingPoints"
                            |> Result.bind (
                                List.traverseResultM (fun entry ->
                                    result {
                                        let! bpm = numberOr entry "Bpm" 0.0

                                        let! startTime =
                                            numberOr entry "StartTime" 0.0 |> Result.map Shared.roundMs

                                        let! signature = signatureField entry

                                        return
                                            {
                                                StartTime = startTime
                                                Bpm = bpm
                                                Signature = signature
                                            }
                                    }))

                        let! svPoints =
                            entries root "SliderVelocities"
                            |> Result.bind (
                                List.traverseResultM (fun entry ->
                                    result {
                                        let! startTime =
                                            numberOr entry "StartTime" 0.0 |> Result.map Shared.roundMs

                                        let! multiplier = numberOr entry "Multiplier" 0.0

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
                                            numberOr entry "StartTime" 0.0 |> Result.map Shared.roundMs

                                        let! lane =
                                            numberOr entry "Lane" 0.0 |> Result.map Shared.roundMs

                                        let! endTime =
                                            numberOr entry "EndTime" 0.0 |> Result.map Shared.roundMs

                                        let! objectType = hitObjectTypeField entry

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
                                BpmDoesNotAffectSv = bpmDoesNotAffectSv
                                InitialScrollVelocity = initialScrollVelocity
                                Title = title
                                Artist = artist
                                Creator = creator
                                DifficultyName = difficultyName
                                TimingPoints = timingPoints
                                SvPoints = svPoints
                                HitObjects = hitObjects
                            }
                    }
                | _ -> Error(UrcError.Syntax(1, ".qua must be a YAML mapping"))
            | [] -> Error(UrcError.Syntax(1, ".qua has no YAML document")))
