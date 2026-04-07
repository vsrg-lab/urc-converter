module internal UrcConverter.Parser.Sm.Parsing

open System
open System.Globalization
open System.Text
open FParsec
open FsToolkit.ErrorHandling
open UrcConverter.Parser.Sm.Internal.Models

// #region Tag Structure

let private pLineComment: Parser<unit, unit> = pstring "//" >>. skipRestOfLine true

let private pSkip: Parser<unit, unit> =
    skipMany (pLineComment <|> (satisfy Char.IsWhiteSpace >>% ()))

let private pTagName:  Parser<string, unit> =
    pchar '#' >>. many1Satisfy (fun c -> c <> ':' && c <> ';')
    |>> fun s -> s.Trim().ToUpperInvariant()

let private pTagValue: Parser<string, unit> =
    manyCharsTill anyChar (pchar ';')

let private pTag: Parser<string * string, unit> =
    pTagName .>> pchar ':' .>>. pTagValue

let private pFile: Parser<(string * string) list, unit> =
    pSkip >>. many (pTag .>> pSkip) .>> eof

// #endregion

// #region Beat (= Value list) (BPMS, STOPS, SCROLLS)

let private pBeatValue: Parser<SmBeatValue, unit> =
    pfloat .>> pchar '=' .>>. pfloat
    |>> fun (beat, value) -> { Beat = beat; Value = value }

let private pBeatValueList: Parser<SmBeatValue list, unit> =
    spaces >>. sepBy (pBeatValue .>> spaces) (pchar ',' .>> spaces) .>> spaces .>> eof

let private parseBeatValues (s: string): SmBeatValue list =
    match run pBeatValueList (s.Trim()) with
    | Success(vs, _, _) -> vs
    | Failure _ -> []

// #endregion

// #region Helpers

let private tryParseInt (s: string) =
    match Int32.TryParse(s.Trim()) with
    | true, v -> v
    | _ -> 0

let private tagLookup key (tags: Map<string, string>) =
    tags |> Map.tryFind key |> Option.defaultValue ""

let private tagBeatValues key (tags: Map<string, string>) =
    tags |> Map.tryFind key |> Option.map parseBeatValues

// #endregion

// #region #NOTES:type:desc:diff:meter:radar:notedata;

/// Split SM NOTES value into 5 header fields + note data.
/// The value contains exactly 5 colons before the note data.
let private parseSmNotesTag (value: string): SmChart option =
    // Find indices of first 5 colons
    let mutable indices = []
    let mutable count = 0

    for i in 0 .. value.Length - 1 do
        if value[i] = ':' && count < 5 then
            indices <- i :: indices
            count <- count + 1

    if count < 5 then None
    else
        let idx = indices |> List.rev |> Array.ofList
        let field i j = value[idx[i] + 1 .. idx[j] - 1].Trim()

        Some {
            StepsType       = value[.. idx[0] - 1].Trim()
            Description     = field 0 1
            Difficulty      = field 1 2
            Meter           = field 2 3 |> tryParseInt
            NoteData        = value[idx[4] + 1 ..].Trim()
            ChartBpms       = None
            ChartStops      = None
            ChartScrolls    = None
        }

// #endregion

// #region #NOTEDATA:; sections

let private parseSscCharts (tags: (string * string) list): SmChart list =
    let buildChart (map: Map<string, string>) =
        let noteData = tagLookup "NOTES" map
        if noteData.Trim() = "" then None
        else 
            Some {
                StepsType       = tagLookup "STEPSTYPE" map
                Description     = tagLookup "DESCRIPTION" map
                Difficulty      = tagLookup "DIFFICULTY" map
                Meter           = tagLookup "METER" map |> tryParseInt
                NoteData        = noteData.Trim()
                ChartBpms       = tagBeatValues "BPMS" map
                ChartStops      = tagBeatValues "STOPS" map
                ChartScrolls    = tagBeatValues "SCROLLS" map
            }

    tags
    |> List.fold
        (fun (current, charts) (tag, value) ->
            if tag = "NOTEDATA" then
                let charts' = current |> Option.bind buildChart |> Option.toList |> List.append charts
                (Some Map.empty, charts')
            else
                let current' = current |> Option.map (Map.add tag (value.Trim()))
                (current', charts))
        (None, [])
    |> fun (current, charts) ->
        current |> Option.bind buildChart |> Option.toList |> List.append charts
    |> List.rev

// #endregion

// #region File assembly

let private assembleFile (tags: (string * string) list): Result<SmFile, string> =
    let globalTags =
        tags
        |> List.takeWhile (fun (t, _) -> t <> "NOTEDATA" && t <> "NOTES")
        |> List.map (fun (k, v) -> (k, v.Trim()))
        |> Map.ofList

    let isSec = globalTags |> Map.containsKey "VERSION"

    let charts =
        if isSec then parseSscCharts tags
        else tags |> List.filter (fun (t, _) -> t = "NOTES") |> List.choose (fun (_, v) -> parseSmNotesTag v)

    let bpms = tagLookup "BPMS" globalTags |> parseBeatValues
    let stops = tagLookup "STOPS" globalTags |> parseBeatValues
    let scrolls = tagLookup "SCROLLS" globalTags |> parseBeatValues

    result {
        do! Result.requireTrue "No BPM data found" (bpms.Length > 0)
        do! Result.requireTrue "No chart data found" (charts.Length > 0)

        return {
            Title           = tagLookup "TITLE" globalTags
            SubTitle        = tagLookup "SUBTITLE" globalTags
            TitleTranslit   = tagLookup "TITLETRANSLIT" globalTags
            Artist          = tagLookup "ARTIST" globalTags
            ArtistTranslit  = tagLookup "ARTISTTRANSLIT" globalTags
            Credit          = tagLookup "CREDIT" globalTags
            Bpms            = bpms
            Stops           = stops
            Scrolls         = scrolls
            Charts          = charts
        }
    }

// #endregion

// Public API
let parseSmFile (filePath: string): Result<SmFile, string> =
    try
        let content = IO.File.ReadAllText(filePath, Encoding.UTF8)

        match run pFile content with
        | Success(tags, _, _) -> assembleFile tags
        | Failure(msg, _, _) -> Result.Error $"Failed to parse file structure: {msg}"
    with ex ->
        Result.Error $"Failed to read file: {ex.Message}"
