module internal UrcConverter.Parser.Qua.Parsing

open System
open System.Globalization
open System.IO
open System.Text
open FsToolkit.ErrorHandling
open YamlDotNet.RepresentationModel
open UrcConverter.Parser.Qua.Internal.Models

// #region YAML Helpers

let private scalarValue (node: YamlNode) =
    match node with
    | :? YamlScalarNode as s -> s.Value
    | _ -> ""

let private tryScalar (key: string)  (mapping: YamlMappingNode) =
    match mapping.Children.TryGetValue(YamlScalarNode(key)) with
    | true, node -> scalarValue node |> Some
    | _ -> None

let private scalarOr (key: string) (fallback: string) (mapping: YamlMappingNode) =
    tryScalar key mapping |> Option.defaultValue fallback

let private tryParseFloat (s: string) =
    match Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | _ -> None

let private tryParseInt (s: string) =
    match Int32.TryParse s with
    | true, v -> Some v
    | _ -> None

let private floatOr fallback s = tryParseFloat s |> Option.defaultValue fallback
let private intOr fallback s = tryParseInt s |> Option.defaultValue fallback

let private sequenceOf (key: string) (mapping: YamlMappingNode): YamlMappingNode list =
    match mapping.Children.TryGetValue(YamlScalarNode(key)) with
    | true, (:? YamlSequenceNode as seq) ->
        seq.Children
        |> Seq.choose (fun n -> 
            match n with 
            | :? YamlMappingNode as m -> Some m
            | _ -> None)
        |> Seq.toList
    | _ -> []

// #endregion

// #region Section Parsers

let private parseTimingPoint (m: YamlMappingNode): QuaTimingPoint =
    {
        StartTime = scalarOr "StartTime" "0" m |> floatOr 0.0
        Bpm = scalarOr "Bpm" "120" m |> floatOr 120.0
        Signature = scalarOr "Signature" "4" m |> intOr 4
    }

let private parseSliderVelocity (m: YamlMappingNode): QuaSliderVelocity =
    {
        StartTime = scalarOr "StartTime" "0" m |> floatOr 0.0
        Multiplier = scalarOr "Multiplier" "1" m |> floatOr 1.0
    }

let private parseHitObject (m: YamlMappingNode): QuaHitObject =
    {
        StartTime = scalarOr "StartTime" "0" m |> intOr 0
        Lane = scalarOr "Lane" "1" m |> intOr 1
        EndTime = scalarOr "EndTime" "0" m |> intOr 0
    }

// #endregion

// #region Chart Assembly

let private assembleChart (root: YamlMappingNode): Result<QuaChart, string> =
    let timingPoints = sequenceOf "TimingPoints" root |> List.map parseTimingPoint
    let sliderVelocities = sequenceOf "SliderVelocities" root |> List.map parseSliderVelocity
    let hitObjects = sequenceOf "HitObjects" root |> List.map parseHitObject

    let mode =
        match (scalarOr "Mode" "Keys4" root).ToLowerInvariant() with
        | "keys7" | "2" -> 2
        | _ -> 1

    let hasScratch =
        scalarOr "HasScratchKey" "false" root
        |> fun s -> s.Equals("true", StringComparison.OrdinalIgnoreCase)

    result {
        do! Result.requireTrue "No timing points found" (timingPoints.Length > 0)

        do! Result.requireTrue "No hit objects found" (hitObjects.Length > 0)

        return {
            Title               = scalarOr "Title" "" root
            Artist              = scalarOr "Artist" "" root
            Creator             = scalarOr "Creator" "" root
            DifficultyName      = scalarOr "DifficultyName" "" root
            Mode                = mode
            HasScratchKey       = hasScratch
            TimingPoints        = timingPoints
            SliderVelocities    = sliderVelocities
            HitObjects          = hitObjects
        }
    }

// #endregion

// Public API
let parseQuaFile (filePath: string): Result<QuaChart, string> =
    try
        let yaml = YamlStream()
        use reader = new StreamReader(filePath, Encoding.UTF8)
        yaml.Load(reader)

        if yaml.Documents.Count = 0 then
            Result.Error "Empty YAML document"
        else
            match yaml.Documents[0].RootNode with
            | :? YamlMappingNode as root -> assembleChart root
            | _ -> Result.Error "Invalid YAML structure: root is not a mapping"
    with ex ->
        Result.Error $"Failed to read file: {ex.Message}"