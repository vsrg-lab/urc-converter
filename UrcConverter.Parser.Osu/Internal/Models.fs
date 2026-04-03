module internal UrcConverter.Parser.Osu.Internal.Models

type OsuNoteType =
    | HitCircle     // type & 1
    | HoldNote      // type & 128 (LN)

type OsuHitObject = {
    X: int
    Time: int
    NoteType: OsuNoteType
    EndTime: int option     // Only exists if LN
}

type OsuTimingPoint = {
    Time: int
    BeatLength: float
    Meter: int
    Uninherited: bool       // true if BPM changes
}

type OsuChart = {
    // [General]
    Mode: int

    // [Metadata]
    Title: string
    TitleUnicode: string
    Artist: string
    ArtistUnicode: string
    Creator: string
    Version: string

    // [Difficulty]
    KeyCount: int
    OverallDifficulty: float

    // [TimingPoints]
    TimingPoints: OsuTimingPoint list

    // [HitObjects]
    HitObjects: OsuHitObject list
}
