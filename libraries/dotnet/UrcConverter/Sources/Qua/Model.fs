namespace UrcConverter.Sources.Qua

module Model =

    type TimingPoint =
        {
            StartTime: int
            Bpm: float
            Signature: int
        }

    type SvPoint = { StartTime: int; Multiplier: float }

    type HitObject =
        {
            StartTime: int
            Lane: int
            EndTime: int
            Mine: bool
        }

    type QuaMap =
        {
            Mode: int
            HasScratchKey: bool
            Title: string option
            Artist: string option
            Creator: string option
            DifficultyName: string option
            TimingPoints: TimingPoint list
            SvPoints: SvPoint list
            HitObjects: HitObject list
        }
