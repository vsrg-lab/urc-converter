module internal UrcConverter.Parser.Qua.Internal.Models

[<Literal>]
let FormatName = "Quaver"

type QuaMode =
    | Keys4 = 1
    | Keys7 = 2

type QuaTimingPoint = {
    StartTime: float
    Bpm: float
    Signature: int      // 4 = 4/4, 3 = 3/4, etc.
}

type QuaSliderVelocity = {
    StartTime: float
    Multiplier: float
}

type QuaHitObject = {
    StartTime: int
    Lane: int       // 1-indexed
    EndTime: int    // 0 = normal note, >0 = LN end time
}

/// Parsed Quaver file. (.qua)
type QuaChart = {
    Title: string
    Artist: string
    Creator: string
    DifficultyName: string
    Mode: int
    HasScratchKey: bool
    TimingPoints: QuaTimingPoint list
    SliderVelocities: QuaSliderVelocity list
    HitObjects: QuaHitObject list
}