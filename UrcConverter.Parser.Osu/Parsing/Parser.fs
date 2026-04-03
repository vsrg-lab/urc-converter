module UrcConverter.Parser.Osu.Parsing

open System
open FParsec
open FsToolkit.ErrorHandling
open UrcConverter.Parser.Osu.Internal.Models

type private RawSection = { Name: string; Lines: string list }

// #region Split .osu file into raw sections

let private pBom: Parser<unit, unit> = 
    optional (pchar '\uFEFF') >>% ()

let private pFormatHeader: Parser<int, unit> =
    pBom >>. spaces >>. pstring "osu file format v" >>. pint32 .>> skipRestOfLine true

let private pBlankOrComment: Parser<unit, unit> =
    skipMany (anyOf " \t")
    >>. (skipNewline <|> (pstring "//" >>. skipRestOfLine true))

let private pSkipFluff: Parser<unit, unit> =
    skipMany (attempt pBlankOrComment)

let private pSectionName: Parser<string, unit> =
    pchar '[' >>. manySatisfy ((<>) ']') .>> pchar ']' .>> skipRestOfLine true

let private pContentLine: Parser<string, unit> =
    notFollowedBy (pchar '[') >>. restOfLine true

let private isContentLine (s: string) =
    let t = s.Trim()
    t.Length > 0 && not (t.StartsWith "//")

let private pSection: Parser<RawSection, unit> =
    pSectionName .>> pSkipFluff
    .>>. many (attempt (pContentLine .>> pSkipFluff))
    |>> fun (name, lines) ->
        { Name = name; Lines = lines |> List.filter isContentLine }

let private pOsuFile: Parser<int * RawSection list, unit> =
    pSkipFluff >>. pFormatHeader .>> pSkipFluff
    .>>. many (pSection .>> pSkipFluff)
    .>> eof

// #endregion

// #region Parse section contents

// Helpers
let private pComma: Parser<unit, unit> = pchar ',' >>% ()
let private pCsvFloat: Parser<float, unit> = pfloat .>> optional pComma
let private pCsvInt: Parser<int, unit> = pint32 .>> optional pComma

let private pSkipField: Parser<unit, unit> =
    skipManySatisfy (fun c -> c <> ',' && c <> '\r' && c <> '\n') >>. optional pComma

// Key-Value sections
let private parseKeyValue (line: string): (string * string) option =
    match line.IndexOf ':' with
    | -1 -> None
    | i -> Some(line[.. i - 1].Trim(), line[i + 1 ..].Trim())

let private toKvMap (lines: string list): Map<string, string> =
    lines |> List.choose parseKeyValue |> Map.ofList

// Line sections
    
// TimingPoint
// Format: time, beatLength, meter, sampleSet, sampleIndex, volume, uninherited, effects
let private pTimingPointLine: Parser<OsuTimingPoint, unit> =
    pipe4
        pCsvFloat                                               // [0] time
        pCsvFloat                                               // [1] beatLength
        pCsvInt                                                 // [2] meter
        (pSkipField >>. pSkipField >>. pSkipField >>. pCsvInt)  // skip [3..5], read [6] uninherited
        (fun time beatLen meter uninherited ->
            {
                Time        = time |> Math.Round |> int
                BeatLength  = beatLen
                Meter       = meter
                Uninherited = uninherited = 1
            })

let private parseTimingPoint (line: string): OsuTimingPoint option =
    match run (pTimingPointLine .>> restOfLine true) line with
    | Success(tp, _, _) -> Some tp
    | Failure _         -> None

// HitObject
// Format: x, y, time, type, hitSound[, extras...]
// Mania hold (type & 128): exras = "endTime:hitSample"
let private pHitObjectLine: Parser<OsuHitObject, unit> =
    pipe5
        pCsvInt                 // [0] x
        pCsvInt                 // [1] y (ignored)
        pCsvInt                 // [2] time
        pCsvInt                 // [3] type bit flags
        (restOfLine true)       // [4+] hitSound, extras
        (fun x _y time typeVal rest ->
            let isHold = typeVal &&& 128 <> 0

            let endTime =
                if not isHold then None
                else
                    // rest = "hitSound, endTime:hitSample..."
                    let parts = rest.Split ','
                    if parts.Length < 2 then None
                    else
                        let extra = parts[1].Trim()
                        let colonIdx = extra.IndexOf ':'
                        let raw = if colonIdx > 0 then extra[.. colonIdx - 1] else extra

                        match Int32.TryParse raw with
                        | true, v -> Some v
                        | _ -> None

            {
                X = x
                Time = time
                NoteType = if isHold then HoldNote else HitCircle
                EndTime = endTime })

let private parseHitObject (line: string): OsuHitObject option =
    match run pHitObjectLine line with
    | Success(ho, _, _) -> Some ho
    | Failure _         -> None

// #endregion

// #region Assemble OsuChart

let private findSection name (sections: RawSection list) =
    sections |> List.tryFind (fun s -> s.Name = name)

let private kvLookup key (kv: Map<string, string>) =
    kv |> Map.tryFind key |> Option.defaultValue ""

let private kvFloat key fallback (kv: Map<string, string>) =
    kv
    |> Map.tryFind key
    |> Option.bind (fun v ->
        match Double.TryParse v with
        | true, f   -> Some f
        | _         -> None)
    |> Option.defaultValue fallback

let private kvInt key fallback (kv: Map<string, string>) =
    kv
    |> Map.tryFind key
    |> Option.bind (fun v ->
        match Int32.TryParse v with
        | true, i   -> Some i
        | _         -> None)
    |> Option.defaultValue fallback

let private assembleChart (sections: RawSection list): Result<OsuChart, string> =
    let general = findSection "General" sections |> Option.map (fun s -> toKvMap s.Lines) |> Option.defaultValue Map.empty
    let metadata = findSection "Metadata" sections |> Option.map (fun s -> toKvMap s.Lines) |> Option.defaultValue Map.empty
    let difficulty = findSection "Difficulty" sections |> Option.map (fun s -> toKvMap s.Lines) |> Option.defaultValue Map.empty

    let mode = kvInt "Mode" -1 general
    if mode <> 3 then
        Error $"Not an osu!mania chart (Mode = {mode})"
    else
        let timingPoints = 
            findSection "TimingPoints" sections
            |> Option.map (fun s -> s.Lines |> List.choose parseTimingPoint)
            |> Option.defaultValue []

        let hitObjects =
            findSection "HitObjects" sections
            |> Option.map (fun s -> s.Lines |> List.choose parseHitObject)
            |> Option.defaultValue []

        Ok {
            Mode                = mode
            Title               = kvLookup "Title" metadata
            TitleUnicode        = kvLookup "TitleUnicode" metadata
            Artist              = kvLookup "Artist" metadata
            ArtistUnicode       = kvLookup "ArtistUnicode" metadata
            Creator             = kvLookup "Creator" metadata
            Version             = kvLookup "Version" metadata
            KeyCount            = kvFloat "CircleSize" 4.0 difficulty |> int
            OverallDifficulty   = kvFloat "OverallDifficulty" 5.0 difficulty
            TimingPoints        = timingPoints
            HitObjects          = hitObjects }

// #endregion

// Public API

let parseOsuFile (filePath: string): Result<OsuChart, string> =
    try
        let content = IO.File.ReadAllText(filePath, Text.Encoding.UTF8)

        match run pOsuFile content with
        | Success((_version, sections), _, _) -> assembleChart sections
        | Failure(msg, _, _) -> Error $"Failed to parse .osu file: {msg}"
    with ex:
        Error $"Failed to read file: {ex.Message}"
