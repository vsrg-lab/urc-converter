module internal UrcConverter.Parser.Ojn.Internal.Models

[<Literal>]
let FormatName = "O2Jam"

let difficultyNames = [| "EX"; "NX"; "HX" |]

// #region Event Types

type OjnNoteEvent = {
    Value: int      // Sample reference (0 = padding)
    NoteType: int   // 0=Normal, 1=LN start, 2=LN end
}

type OjnEvent =
    | Note of OjnNoteEvent
    | BpmChange of float
    | MeasureFraction of float
    | Padding

// #endregion

// #region Package & Header

type OjnPackage = {
    Measure: int
    Channel: int
    Events: OjnEvent list
}

type OjnHeader = {
    SongId: int
    Bpm: float
    Level: int array            // [EX, NX, HX]
    NoteCount: int array        // [EX, NX, HX]
    PackageCount: int array     // [EX, NX, HX]
    Title: string
    Artist: string
    Noter: string
    NoteOffset: int array       // [EX, NX, HX]
    CoverOffset: int
}

type OjnFile = {
    Header: OjnHeader
    Packages: OjnPackage list array // [EX packages, NX packages, HX packages]
}

// #endregion