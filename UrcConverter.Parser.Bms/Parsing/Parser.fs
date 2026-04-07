module internal UrcConverter.Parser.Bms.Parsing

open System
open System.Globalization
open System.Text
open FParsec
open FsToolkit.ErrorHandling
open UrcConverter.Parser.Bms.Internal.Models

// #region Types

type private BmsLine =
    | Header of key: string * value: string
    | ExtDef of prefix: string * id: string * value: string
    | DataLine of measure: int * channel: int * data: string
    | Ignored

// #endregion

// #region FParsec Primitives

let private pBase36Char: Parser<char, unit> =
    satisfy (fun c -> (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))

/// Two consecutive base-36 characters as a string. e.g. "0A", "ZZ"
let private pBase36Pair: Parser<string, unit> =
    parray 2 pBase36Char |>> fun cs -> String(cs)

// #endregion

// #region Line Parsers

/// Data line: #XXXYY:DATA
/// XXX = 3 decimal digits (measure), YY = 2 decimal digits or 2 base-36 chars if Alphabet includes (channel)
let private pDataLine: Parser<BmsLine, unit> =
    pchar '#'
    >>. pipe3
        (parray 3 digit |>> fun cs -> Int32.Parse(String(cs)))
        (parray 2 pBase36Char |>> fun cs -> 
            let s = String(cs)
            if s |> Seq.forall Char.IsDigit then Int32.Parse s
            else Base36.decode s)
        (pchar ':' >>. restOfLine true |>> fun s -> s.Trim())
        (fun measure channel data -> DataLine(measure, channel, data))

/// Known extended definition prefixes and their key length.
let private extDefPrefixes = [ "SCROLL", 6; "STOP", 4; "BPM", 3 ]

/// Classify a parsed #KEY VALUE pair as either Header or ExtDef.
let private classifyCommand (key: string) (value: string): BmsLine =
    let k = key.ToUpperInvariant()
    let v = value.Trim()

    extDefPrefixes
    |> List.tryFind (fun (prefix, len) -> k.Length > len && k.StartsWith prefix)
    |> function
        | Some(prefix, len) -> ExtDef(prefix, k[len..], v)
        | None              -> Header(k, v)

//// Command line: #KEY VALUE (header or extended definition)
let private pCommandLine: Parser<BmsLine, unit> =
    pchar '#'
    >>. many1Satisfy (fun c -> c <> ' ' && c <> '\t' && c <> '\n' && c <> '\r')
    .>> spaces
    .>>. restOfLine true
    |>> fun (rawKey, rawValue) -> classifyCommand rawKey rawValue

/// Parse a single BMS line. Tries data line first, then command, then ignores.
let private pBmsLine: Parser<BmsLine, unit> =
    spaces 
    >>. choice 
        [ 
            attempt pDataLine
            attempt pCommandLine
            (restOfLine true >>% Ignored)
        ]

/// Classify a line string using FParsec.
let private classifyLine (line: string): BmsLine =
    match run pBmsLine line with
    | Success(result, _, _) -> result
    | Failure _             -> Ignored

// #endregion

// #region Data Expansion

/// Parse the data portion of a data line into base-36 pairs.
let private pObjectPairs: Parser<string list, unit> = many pBase36Pair .>> eof

/// Expand a data string into BmsObjects.
let private expandObjects (measure: int) (channel: int) (data: string): BmsObject list =
    match run pObjectPairs data with
    | Success(pairs, _, _) ->
        let count = pairs.Length
        if count = 0 then []
        else
            pairs
            |> List.mapi (fun i value -> (i, value))
            |> List.filter (fun (_, v) -> v.ToUpperInvariant() <> "00")
            |> List.map (fun (i, v) ->
                {
                    Measure = measure
                    Channel = channel
                    Position = float i / float count
                    Value = v
                })
    | Failure _ -> []

/// Parse channel 02 (measure length) - data is a single float.
let private parseMeasureLength (data: string): float option =
    match Double.TryParse(data, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, v when v > 0.0 -> Some v
    | _ -> None

// #endregion

// #region Chart Assembly

let private tryParseFloat (s: string) =
    match Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | _ -> None

let private tryParseInt (s: string) =
    match Int32.TryParse s with
    | true, v -> Some v
    | _ -> None

let private applyHeader (chart: BmsChart) (key: string) (value: string): BmsChart =
    match key with
    | "PLAYER"      -> { chart with Player = tryParseInt value |> Option.defaultValue chart.Player }
    | "TITLE"       -> { chart with Title = value }
    | "SUBTITLE"    -> { chart with SubTitle = value }
    | "ARTIST"      -> { chart with Artist = value }
    | "SUBARTIST"   -> { chart with SubArtist = value }
    | "GENRE"       -> { chart with Genre = value }
    | "BPM"         -> { chart with Bpm = tryParseFloat value |> Option.defaultValue chart.Bpm }
    | "PLAYLEVEL"
    | "DIFFICULTY"  -> { chart with PlayLevel  = tryParseInt value |> Option.defaultValue chart.PlayLevel }
    | "RANK"        -> { chart with Rank = tryParseInt value |> Option.defaultValue chart.Rank }
    | "LNTYPE"      -> { chart with LnType = tryParseInt value |> Option.defaultValue chart.LnType }
    | "LNOBJ"       -> { chart with LnObj = Some(Base36.decode value) }
    | _             -> chart

let private applyExtDef (chart: BmsChart) (prefix: string) (id: string) (value: string): BmsChart =
    let key = Base36.decode id

    let addFloatDef fallback (map: Map<int, float>)  =
        map |> Map.add key (tryParseFloat value |> Option.defaultValue fallback)

    match prefix with
    | "BPM"     -> { chart with ExtBpms = chart.ExtBpms |> addFloatDef 0.0 }
    | "STOP"    -> { chart with Stops = chart.Stops |> addFloatDef 0.0 }
    | "SCROLL"  -> { chart with Scrolls = chart.Scrolls |> addFloatDef 1.0 }
    | _         -> chart

let private applyDataLine (chart: BmsChart) (measure: int) (channel: int) (data: string): BmsChart =
    if channel = Channel.MeasureLength then
        match parseMeasureLength data with
        | Some len -> { chart with MeasureLengths = chart.MeasureLengths |> Map.add measure len }
        | None -> chart
    else
        let objects = expandObjects measure channel data
        { chart with Objects = chart.Objects @ objects}

let private assembleChart (lines: BmsLine list): Result<BmsChart, string> =
    let chart =
        lines
        |> List.fold
            (fun chart line ->
                match line with
                | Header(key, value) -> applyHeader chart key value
                | ExtDef(prefix, id, value) -> applyExtDef chart prefix id value
                | DataLine(measure, ch, data) -> applyDataLine chart measure ch data
                | Ignored -> chart)
            emptyChart

    result {
        do! Result.requireTrue 
                "No valid BPM found" 
                (chart.Bpm > 0.0)

        do! Result.requireTrue 
                "No playable note data found" 
                (chart.Objects |> List.exists (fun o -> Channel.isPlayable o.Channel))

        return { chart with Objects = chart.Objects |> List.sortBy (fun o -> o.Measure, o.Position) }
    }

// #endregion

// Public API
let parseBmsFile (filePath: string): Result<BmsChart, string> =
    try
        IO.File.ReadAllLines(filePath, Encoding.UTF8)
        |> Array.toList
        |> List.map classifyLine
        |> assembleChart
    with ex ->
        Result.Error $"Failed to read file: {ex.Message}"