namespace UrcConverter.Sources.Ojn

module Model =

    /// Note event kind decoded from `type % 4`.
    [<RequireQualifiedAccess>]
    type OjnNoteKind =
        | Normal
        | HoldStart
        | HoldEnd

    /// Event payload discriminated by package channel.
    [<RequireQualifiedAccess>]
    type OjnEventKind =
        | MeasureFraction of float
        | BpmChange of float
        | Note of lane: int * kind: OjnNoteKind

    /// One event of a note-section package, in file order.
    type OjnEvent =
        {
            Measure: int
            /// Package grid position: raw event index over the package total.
            Position: float
            Kind: OjnEventKind
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
