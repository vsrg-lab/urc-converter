namespace UrcConverter

/// Playback rate scaling for URC charts.
[<RequireQualifiedAccess>]
module Rate =

    open System

    [<Literal>]
    let private MinScaledRate = 50

    [<Literal>]
    let private MaxScaledRate = 300

    [<Literal>]
    let private StepTolerance = 1e-9

    let private roundMs (value: float) : int =
        if value >= 0.0 then
            int (value + 0.5)
        else
            int (value - 0.5)

    let private validateRate (rate: float) : Result<int, UrcError> =
        if not (Double.IsFinite rate) then
            Error(UrcError.invalidRate 1 "playback rate must be between 0.50 and 3.00 in 0.01 steps")
        else
            let scaled = int (Math.Round(rate * 100.0))

            if abs (rate * 100.0 - float scaled) > StepTolerance || scaled < MinScaledRate || scaled > MaxScaledRate then
                Error(UrcError.invalidRate 1 "playback rate must be between 0.50 and 3.00 in 0.01 steps")
            else
                Ok scaled

    type private LaneAcc =
        {
            PrevOrigMs: int option
            PrevAdjustedMs: int option
            PrevType: NoteType option
            Notes: Note list
            Warnings: UrcWarning list
        }

    /// Scales a chart's playback rate within 0.50x to 3.00x in 0.01 steps.
    let scaleRate (chart: Chart) (rate: float) : Result<RateResult, UrcError> =
        validateRate rate
        |> Result.map (fun scaled ->
            let scaledFloat = float scaled

            let timing =
                chart.TimingPoints
                |> List.fold
                    (fun (acc: Map<int, TimingPoint>) point ->
                        let timestampMs = roundMs (float point.TimestampMs * 100.0 / scaledFloat)
                        let bpm = (point.Bpm * scaledFloat) / 100.0

                        Map.add
                            timestampMs
                            {
                                TimestampMs = timestampMs
                                Bpm = bpm
                                Meter = point.Meter
                                Multiplier = point.Multiplier
                            }
                            acc)
                    Map.empty
                |> Map.toList
                |> List.map snd

            let byLane =
                chart.Notes
                |> List.indexed
                |> List.sortBy (fun (index, note) -> note.TimestampMs, index)
                |> List.map snd
                |> List.groupBy _.Lane
                |> List.sortBy fst

            let processLane (lane: int, laneNotes: Note list) =
                let initial =
                    {
                        PrevOrigMs = None
                        PrevAdjustedMs = None
                        PrevType = None
                        Notes = []
                        Warnings = []
                    }

                let step (acc: LaneAcc) (note: Note) =
                    let scaledMs = roundMs (float note.TimestampMs * 100.0 / scaledFloat)

                    let adjustedMs, warnings =
                        match acc.PrevAdjustedMs with
                        | Some prevAdj when scaledMs <= prevAdj ->
                            let nextMs = prevAdj + 1

                            let nextWarnings =
                                match acc.PrevOrigMs with
                                | Some prevOrig when note.TimestampMs <= prevOrig ->
                                    let isHold = acc.PrevType = Some NoteType.LS && note.Type = NoteType.LE

                                    let codeKind, message =
                                        if isHold then
                                            WarningCode.HoldLengthClamped,
                                            $"lane {lane}: hold end at {scaledMs}ms adjusted to {nextMs}ms (minimum 1ms duration)"
                                        else
                                            WarningCode.NoteGapClamped,
                                            $"lane {lane}: note at {scaledMs}ms adjusted to {nextMs}ms (minimum 1ms gap)"

                                    {
                                        CodeKind = codeKind
                                        Lane = lane
                                        OriginalMs = scaledMs
                                        AdjustedMs = nextMs
                                        Message = message
                                    }
                                    :: acc.Warnings
                                | _ -> acc.Warnings

                            nextMs, nextWarnings
                        | _ -> scaledMs, acc.Warnings

                    {
                        PrevOrigMs = Some note.TimestampMs
                        PrevAdjustedMs = Some adjustedMs
                        PrevType = Some note.Type
                        Notes = { note with TimestampMs = adjustedMs } :: acc.Notes
                        Warnings = warnings
                    }

                let doneAcc = laneNotes |> List.fold step initial
                List.rev doneAcc.Notes, List.rev doneAcc.Warnings

            let laneResults = byLane |> List.map processLane

            let adjustedNotes =
                laneResults
                |> List.collect fst
                |> List.sortBy (fun note -> note.TimestampMs, note.Lane)

            let warnings =
                laneResults
                |> List.collect snd
                |> List.sortBy (fun warning -> warning.AdjustedMs, warning.Lane)

            {
                Chart =
                    { chart with
                        TimingPoints = timing
                        Notes = adjustedNotes
                    }
                Warnings = warnings
            })
