module internal UrcConverter.Parser.Sm.Internal.Models

[<Literal>]
let FormatName = "StepMania"

/// A beat-value pair used for BPM changes, STOPs, and SCROLLs
type SmBeatValue = { Beat: float; Value: float }

/// Parsed chart data from a StepMania file.
type SmChart = {
    StepsType: string       // e.g. "dance-single", "kb7-single", "pump-single"
    Description: string
    Difficulty: string      // "Beginner", "Easy", "Medium", "Hard", "Challenge", "Edit"
    Meter: int
    NoteData: string        // raw note data (measures separated by commas)

    // SSC per-chart timing (overrides global if present)
    ChartBpms: SmBeatValue list option
    ChartStops: SmBeatValue list option
    ChartScrolls: SmBeatValue list option
}

/// Parsed StepMania file (.sm & .ssc).
type SmFile = {
    Title: string
    SubTitle: string
    TitleTranslit: string
    Artist: string
    ArtistTranslit: string
    Credit: string
    Bpms: SmBeatValue list      // Global BPM changes
    Stops: SmBeatValue list     // Global Stops
    Scrolls: SmBeatValue list   // Global Scrolls (SSC only)
    Charts: SmChart list
}

let keyCountFromSteps (stepsType: string): int =
    match stepsType.ToLowerInvariant() with
    | "dance-single"                    -> 4
    | "dance-double" | "dance-couple"   -> 8
    | "dance-solo"                      -> 6
    | "pump-single"                     -> 5
    | "pump-double" | "pump-couple"     -> 10
    | "pump-halfdouble"                 -> 6
    | "kb7-single"                      -> 7
    | s ->
        // Fallback: try to infer from name
        if s.Contains "double" then 8 else 4