namespace UrcConverter.Sources.Osu

module Model =

    type TimingPoint =
        {
            Time: int
            BeatLength: float
            Meter: int
            Uninherited: bool
        }

    [<RequireQualifiedAccess>]
    type HitObjectKind =
        | Tap
        | Hold of endTime: int

    type HitObject =
        {
            X: int
            Time: int
            Kind: HitObjectKind
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

        static member Empty =
            {
                Mode = 3
                Title = None
                TitleUnicode = None
                Artist = None
                ArtistUnicode = None
                Creator = None
                Version = None
                CircleSize = None
                OverallDifficulty = None
                TimingPoints = []
                HitObjects = []
            }
