namespace UrcConverter.Sources.Osu

module Model =

    type TimingPoint =
        {
            Time: int
            BeatLength: float
            Meter: int
            Uninherited: bool
        }

    type HitObject =
        {
            X: int
            Time: int
            IsHold: bool
            EndTime: int option
        }

    type Beatmap =
        {
            Mode: int
            Title: string option
            TitleUnicode: string option
            Artist: string option
            ArtistUnicode: string option
            Creator: string option
            Version: string option
            CircleSize: float option
            OverallDifficulty: float option
            TimingPoints: TimingPoint list
            HitObjects: HitObject list
        }
