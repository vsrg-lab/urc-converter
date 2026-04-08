module internal UrcConverter.Parser.Bms.Internal.Models

open System
open System.Globalization

[<Literal>]
let FormatName = "BMS"

// #region Base-36 Utilities

module Base36 =
    let decodeChar (c: Char) =
        match c with
        | c when c >= '0' && c <= '9' -> int c - int '0'
        | c when c >= 'A' && c <= 'Z' -> int c - int 'A' + 10
        | c when c >= 'a' && c <= 'z' -> int c - int 'a' + 10
        | _ -> 0

    /// Decode a base-36 string to int. e.g. "0A" -> 10, "ZZ" -> 1295
    let decode (s: string) =
        s |> Seq.fold (fun acc c -> acc * 36 + decodeChar c) 0

    /// Decode a hex string to int. Used for channel 03 BPM values.
    let decodeHex (s: string) =
        match Int32.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture) with
        | true, v -> v
        | _ -> 0

// #endregion

// #region Channel Constants

[<RequireQualifiedAccess>]
module Channel =
    let [<Literal>] MeasureLength = 2
    let [<Literal>] BpmHex = 3          // BPM as direct hex value (0 ~ 255)
    let [<Literal>] ExtBpm = 8          // Reference to #BPMxx
    let [<Literal>] Stop = 9            // Reference to #STOPxx
    let Scroll = Base36.decode "SC"     // beatoraja extension, reference to #SCROLLxx

    let private noteSet = set [ 11; 12; 13; 14; 15; 16; 17; 18; 19 ]
    let private lnSet = set [ 51; 52; 53; 54; 55; 56; 57; 58; 59 ]

    let isNote ch = Set.contains ch noteSet
    let isLn ch = Set.contains ch lnSet
    let lnToNote ch = ch - 40

    /// Any channel that carries playable note data.
    let isPlayable ch = isNote ch || isLn ch

    /// Timing/SV channels that affect the timeline.
    let isTiming ch = ch = BpmHex || ch = ExtBpm || ch = Stop || ch = Scroll

// #endregion

// #region Difficulty Mapping

let difficultyName (d: int): string =
    match d with
    | 1 -> "BEGINNER"
    | 2 -> "NORMAL"
    | 3 -> "HYPER"
    | 4 -> "ANOTHER"
    | 5 -> "INSANE"
    | _ -> ""

// #endregion

// #region BMS Models

/// A single object expanded from a BMS data line.
type BmsObject = {
    Measure: int
    Channel: int
    Position: float // 0.0 to <1.0 within measure (fractional position)
    Value: string   // raw 2-char base-36/hex string (e.g. "01", "ZZ")
}

/// Parsed BMS chart - intermediate representation.
type BmsChart = {
    // Header fields
    Player: int                     // 1=SP, 2=CP, 3=DP
    Title: string
    SubTitle: string
    Artist: string
    SubArtist: string
    Genre: string
    Bpm: float                      // Initial BPM from #BPM header
    PlayLevel: int                  // #PLAYLEVEL
    Difficulty: int                 // #DIFFICULTY (1=Beginner, 2=Normal, 3=Hyper, 4=Another, 5=Insane)
    Rank: int                       // 0=VeryHard, 1=Hard, 2=Normal, 3=Easy
    LnType: int                     // 1=RDM (ch 51 ~ 59), 2=MGQ
    LnObj: int option               // #LNOBJ - base-36 decoded object ID marking LN ends

    // Extended definitions
    ExtBpms: Map<int, float>        // #BPMxx -> BPM value
    Stops: Map<int, float>          // #STOPxx -> duration in 1/192 of a whole note
    Scrolls: Map<int, float>        // #SCROLLxx -> scroll speed multiplier

    // Measure data
    MeasureLengths: Map<int, float> // measure -> length multiplier (default 1.0)

    // All expanded objects from data lines
    Objects: BmsObject list
}

let emptyChart: BmsChart = {
    Player = 1; Title = ""; SubTitle = ""; Artist = ""; SubArtist = ""
    Genre = ""; Bpm = 130.0; PlayLevel = 0; Difficulty = 0; Rank = 2; LnType = 1; LnObj = None
    ExtBpms = Map.empty; Stops = Map.empty; Scrolls = Map.empty
    MeasureLengths = Map.empty; Objects = []
}

// #endregion
