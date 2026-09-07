namespace UrcConverter.Sources.Ojn

module Model =

    /// One event of a note-section package, in file order.
    type OjnEvent =
        {
            Measure: int
            /// Package grid position: raw event index over the package total.
            Position: float
            Channel: int
            /// f32 payload of channels 0 (measure fraction) and 1 (BPM change).
            Value: float
            /// Note kind from `type % 4`: 0 normal, 2 hold, 3 release.
            Kind: int
            /// Byte offset of the event, for error locations.
            Offset: int
        }

    /// Events of one difficulty section.
    type OjnDifficulty =
        {
            Index: int
            Events: OjnEvent list
        }

    /// Parsed OJN file; the header BPM drives the timing walk.
    type OjnFile =
        {
            Title: string
            Artist: string
            Noter: string
            Bpm: float
            Difficulties: OjnDifficulty list
        }
