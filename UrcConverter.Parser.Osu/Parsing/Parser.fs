module internal UrcConverter.Parser.Osu.Parsing

open System
open System.Text
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
    notFollowedBy (pchar '[') >>. notFollowedBy eof >>. restOfLine true

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

let private pSkipCsvField: Parser<unit, unit> =
    skipManySatisfy (fun c -> c <> ',' && c <> '\r' && c <> '\n') >>. optional pComma

let private tryParseLine (parser: Parser<'a, unit>) (line: string): 'a option =
    match run (parser .>> restOfLine true) line with
    | Success(v, _, _)  -> Some v
    | Failure _         -> None

let private findSection name (sections: RawSection list) =
    sections |> List.tryFind (fun s -> s.Name = name)

let private findKvSection name (sections: RawSection list): Map<string, string> =
    findSection name sections
    |> Option.map (fun s ->
        s.Lines
        |> List.choose (fun line ->
            match line.IndexOf ':' with
            | -1 -> None
            | i -> Some(line[.. i - 1].Trim(), line[i + 1 ..].Trim()))
        |> Map.ofList)
    |> Option.defaultValue Map.empty

let private findParsedSection name (parser: Parser<'a, unit>) (sections: RawSection list): 'a list =
    findSection name sections
    |> Option.map (fun s -> s.Lines |> List.choose (tryParseLine parser))
    |> Option.defaultValue []

// Key-Value lookups
let private kvStr key (kv: Map<string, string>) =
    kv |> Map.tryFind key |> Option.defaultValue ""

let private kvParse (parse: string -> bool * 'a) key fallback (kv: Map<string, string>) =
    kv
    |> Map.tryFind key
    |> Option.bind (fun v -> 
        match parse v with 
        | true, x -> Some x 
        | _ -> None)
    |> Option.defaultValue fallback

let private kvFloat key fallback kv = kvParse Double.TryParse key fallback kv
let private kvInt key fallback kv = kvParse Int32.TryParse key fallback kv

// TimingPoint parser
// Format: time, beatLength, meter, sampleSet, sampleIndex, volume, uninherited, effects
let private pTimingPointLine: Parser<OsuTimingPoint, unit> =
    pipe4
        pCsvFloat                                                       // [0] time
        pCsvFloat                                                       // [1] beatLength
        pCsvInt                                                         // [2] meter
        (pSkipCsvField >>. pSkipCsvField >>. pSkipCsvField >>. pCsvInt) // skip [3..5], read [6] uninherited
        (fun time beatLen meter uninherited ->
            {
                Time        = time |> Math.Round |> int
                BeatLength  = beatLen
                Meter       = meter
                Uninherited = uninherited = 1
            })

// HitObject parser
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
                EndTime = endTime 
            })

// #endregion


// #region Assemble OsuChart

let private assembleChart (sections: RawSection list): Result<OsuChart, string> =
    let general = findKvSection "General" sections
    let metadata = findKvSection "Metadata" sections
    let difficulty = findKvSection "Difficulty" sections

    let mode = kvInt "Mode" -1 general

    result {
        do! Result.requireEqual 3 mode $"Not an osu!mania chart (Mode = {mode})"

        return
            {
                Mode                = mode
                Title               = kvStr "Title" metadata
                TitleUnicode        = kvStr "TitleUnicode" metadata
                Artist              = kvStr "Artist" metadata
                ArtistUnicode       = kvStr "ArtistUnicode" metadata
                Creator             = kvStr "Creator" metadata
                Version             = kvStr "Version" metadata
                KeyCount            = kvFloat "CircleSize" 4.0 difficulty |> int
                OverallDifficulty   = kvFloat "OverallDifficulty" 5.0 difficulty
                TimingPoints        = findParsedSection "TimingPoints" pTimingPointLine sections
                HitObjects          = findParsedSection "HitObjects" pHitObjectLine sections
            }
    }

// #endregion

// Public API
let parseOsuFile (filePath: string): Result<OsuChart, string> =
    try
        let content = IO.File.ReadAllText(filePath, Encoding.UTF8)

        match run pOsuFile content with
        | Success((_version, sections), _, _) -> assembleChart sections
        | Failure(msg, _, _) -> Result.Error $"Failed to parse .osu file: {msg}"
    with ex ->
        Result.Error $"Failed to read file: {ex.Message}"
