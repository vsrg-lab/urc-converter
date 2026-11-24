namespace UrcConverter

type NoteType =
    | N
    | LS
    | LE
    | M
    | F

    member this.Token =
        match this with
        | N -> "N"
        | LS -> "LS"
        | LE -> "LE"
        | M -> "M"
        | F -> "F"

type Version = { Major: int; Minor: int }

type Metadata =
    {
        Original: string
        Title: string
        Artist: string
        Creator: string
        Version: string
    }

type Judgment =
    {
        Windows: float list
        Rates: float list
    }

type Layout =
    {
        Keys: int
        SpecialKeys: int
        SpecialLanes: int list option
    }

    member this.TotalLanes = this.Keys + this.SpecialKeys

type Meter = { Beats: int; NoteValue: int }

type TimingPoint =
    {
        TimestampMs: int
        Bpm: float
        Meter: Meter
        Multiplier: float option
    }

type Note =
    {
        TimestampMs: int
        Lane: int
        Type: NoteType
    }

type Chart =
    {
        FormatVersion: Version
        Metadata: Metadata
        Judgment: Judgment option
        Layout: Layout
        TimingPoints: TimingPoint list
        Notes: Note list
    }

/// Machine-readable warning code emitted during chart transformation.
[<RequireQualifiedAccess>]
type WarningCode =
    | NoteGapClamped
    | HoldLengthClamped

    member this.Code =
        match this with
        | WarningCode.NoteGapClamped -> Strings.warningNoteGapClamped
        | WarningCode.HoldLengthClamped -> Strings.warningHoldLengthClamped

/// Non-fatal adjustment warning emitted during chart transformation.
type UrcWarning =
    {
        CodeKind: WarningCode
        Lane: int
        OriginalMs: int
        AdjustedMs: int
        Message: string
    }

    member this.Code = this.CodeKind.Code

    override this.ToString() =
        $"[{this.Code}] {this.Message}"

/// Result of playback rate scaling, pairing the adjusted chart with any warnings.
type RateResult =
    {
        Chart: Chart
        Warnings: UrcWarning list
    }
