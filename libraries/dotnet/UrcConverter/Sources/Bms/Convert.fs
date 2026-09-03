namespace UrcConverter.Sources.Bms

module Convert =

    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Bms.Model

    [<Literal>]
    let private MeasureUs = 240000000.0

    let private systemChannels = Set.ofList [ "02"; "03"; "08"; "09"; "SC" ]

    let private idValue (text: string) (b: int) =
        let digit (c: char) =
            if c >= '0' && c <= '9' then int c - int '0'
            elif c >= 'A' && c <= 'Z' then int c - int 'A' + 10
            else int c - int 'a' + 36
        (digit text[0]) * b + (digit text[1])

    let private channelKind (channel: string) =
        if channel.Length <> 2 then None
        else
            let c0, c1 = channel[0], channel[1]
            if (c0 = '1' || c0 = '2') && (c1 >= '1' && c1 <= '9') then Some "visible"
            elif (c0 = '5' || c0 = '6') && (c1 >= '1' && c1 <= '9') then Some "ln"
            elif (c0 = 'D' || c0 = 'E') && (c1 >= '1' && c1 <= '9') then Some "mine"
            else None

    let private sideOf (c: char) =
        match c with
        | '1' | '5' | 'D' -> 0
        | _ -> 1

    let private detectMode (pms: bool) (used: Set<int * char>) =
        if pms then
            let is18 =
                used
                |> Set.exists (fun (side, second) ->
                    (second >= '6' && second <= '9') || (side = 1 && second = '1'))
            if is18 then "PMS18" else "PMS9"
        else
            let seven = used |> Set.exists (fun (_, second) -> second = '8' || second = '9')
            let isDouble = used |> Set.exists (fun (side, _) -> side = 1)
            match seven, isDouble with
            | true, true -> "14K"
            | false, true -> "10K"
            | true, false -> "7K"
            | false, false -> "5K"

    let private getLane (mode: string) (channel: string) : int option =
        if channel.Length <> 2 then None
        else
            let side = sideOf channel[0]
            let key = channel[1]
            match mode, side with
            | ("5K" | "10K"), side ->
                if key = '6' then Some (side * 6)
                elif key >= '1' && key <= '5' then Some ((int key - int '0') + side * 6)
                else None
            | ("7K" | "14K"), side ->
                if key = '6' then Some (side * 8)
                elif key >= '1' && key <= '5' then Some ((int key - int '0') + side * 8)
                elif key = '8' || key = '9' then Some ((int key - int '8' + 6) + side * 8)
                else None
            | "PMS9", 0 when key >= '1' && key <= '5' -> Some (int key - int '1')
            | "PMS9", 1 when key >= '2' && key <= '5' -> Some (int key - int '2' + 5)
            | "PMS18", side ->
                let baseLane = side * 9
                match key with
                | '1' | '2' | '3' | '4' | '5' -> Some (baseLane + (int key - int '1'))
                | '8' -> Some (baseLane + 5)
                | '9' -> Some (baseLane + 6)
                | '6' -> Some (baseLane + 7)
                | '7' -> Some (baseLane + 8)
                | _ -> None
            | _ -> None

    let private pairLongNotes (chart: BmsChart) (stream: (float * string) list) (lane: int) : Result<(int * int * NoteType) list, UrcError> =
        let folder (start, notes) (time, obj) =
            if chart.LnType = 1 then
                if obj <> "00" then
                    match start with
                    | None -> Some time, notes
                    | Some s ->
                        let ls = (Shared.roundMs (s / 1000.0), lane, NoteType.LS)
                        let le = (Shared.roundMs (time / 1000.0), lane, NoteType.LE)
                        None, le :: ls :: notes
                else
                    start, notes
            else
                if obj = "00" then
                    match start with
                    | Some s ->
                        let ls = (Shared.roundMs (s / 1000.0), lane, NoteType.LS)
                        let le = (Shared.roundMs (time / 1000.0), lane, NoteType.LE)
                        None, le :: ls :: notes
                    | None -> None, notes
                elif Option.isNone start then
                    Some time, notes
                else
                    start, notes

        let finalStart, finalNotes = stream |> List.fold folder (None, [])
        match finalStart with
        | Some _ -> Result.Error(UrcError.Syntax(1, $"long note on lane {lane} has no end"))
        | None -> Result.Ok(List.rev finalNotes)

    let private buildNotes
        (chart: BmsChart)
        (mode: string)
        (objects: (float * string * string) list)
        (timed: Map<int, float>)
        : Result<(int * int * NoteType) list, UrcError> =
        let streams =
            objects
            |> List.indexed
            |> List.groupBy (fun (_, (_, channel, _)) -> channel)
            |> List.map (fun (channel, group) ->
                channel, group |> List.map (fun (idx, (_, _, obj)) -> Map.find idx timed, obj))

        let channelNotes (channel, stream) : Result<(int * int * NoteType) list, UrcError> =
            match getLane mode channel, channelKind channel with
            | Some lane, Some "mine" ->
                stream
                |> List.map (fun (time, _) -> (Shared.roundMs (time / 1000.0), lane, NoteType.M))
                |> Result.Ok
            | Some lane, Some "ln" ->
                pairLongNotes chart stream lane
            | Some lane, Some _ ->
                let folder (pending, notes) (time, obj) =
                    match chart.LnObj, pending with
                    | Some lnobj, Some p when obj = lnobj ->
                        let ls = (Shared.roundMs (p / 1000.0), lane, NoteType.LS)
                        let le = (Shared.roundMs (time / 1000.0), lane, NoteType.LE)
                        None, le :: ls :: notes
                    | _ ->
                        let acc =
                            match pending with
                            | Some p -> (Shared.roundMs (p / 1000.0), lane, NoteType.N) :: notes
                            | None -> notes
                        Some time, acc

                let lastPending, foldedNotes = stream |> List.fold folder (None, [])
                let finalNotes =
                    match lastPending with
                    | Some p -> (Shared.roundMs (p / 1000.0), lane, NoteType.N) :: foldedNotes
                    | None -> foldedNotes
                Result.Ok (List.rev finalNotes)
            | _ -> Result.Ok []

        streams
        |> List.traverseResultM channelNotes
        |> Result.map List.concat

    [<RequireQualifiedAccess>]
    type private EntryKind =
        | Bpm of float
        | Meter of int
        | Stop of float
        | Scroll of float
        | Object of int
        | Anchor

    type private ScanAcc =
        {
            Entries: (float * int * EntryKind) list
            Objects: (float * string * string) list
            Used: Set<int * char>
        }

    type private TimingScanState =
        {
            TimeUs: float
            PrevY: float
            PendingStop: float
            CurrentBpm: float option
            CurrentBeats: int
            BpmPoints: (int * float * int) list
            SvPoints: (int * float) list
            Anchors: int list
            Timed: Map<int, float>
        }

    let convertBms (chart: BmsChart) : Result<Chart, UrcError> =
        result {
            let! bpmInitial =
                match chart.Bpm with
                | Some bpm when bpm > 0.0 -> Result.Ok bpm
                | _ -> Result.Error(UrcError.Syntax(1, "missing or non-positive #BPM"))

            let maxMeasure =
                chart.Measures
                |> Map.keys
                |> Seq.fold max -1

            let boundaries =
                [| 0 .. maxMeasure |]
                |> Array.scan (fun acc m ->
                    let rate = Map.tryFind m chart.Rates |> Option.defaultValue 1.0
                    acc + rate) 0.0

            let initialAcc =
                {
                    Entries = [ (0.0, 0, EntryKind.Bpm bpmInitial) ]
                    Objects = []
                    Used = Set.empty
                }

            let rec scanMeasures (acc: ScanAcc) (m: int) : Result<ScanAcc, UrcError> =
                if m > maxMeasure then
                    Result.Ok { acc with Objects = List.rev acc.Objects }
                else
                    let rate = Map.tryFind m chart.Rates |> Option.defaultValue 1.0
                    let prevRate = Map.tryFind (m - 1) chart.Rates |> Option.defaultValue 1.0
                    let meterEntries =
                        if rate <> prevRate then
                            let beats = rate * 4.0
                            if abs (beats - round beats) < 1e-9 && round beats >= 1.0 then
                                [ (boundaries[m], 3, EntryKind.Meter (int (round beats))) ]
                            else []
                        else []

                    let accWithMeter = { acc with Entries = meterEntries @ acc.Entries }

                    match Map.tryFind m chart.Measures with
                    | None -> scanMeasures accWithMeter (m + 1)
                    | Some measureMap ->
                        let rec scanChannels (cAcc: ScanAcc) (channels: (string * string list) list) : Result<ScanAcc, UrcError> =
                            match channels with
                            | [] -> Result.Ok cAcc
                            | (channel, ids) :: restChannels ->
                                let rec scanIds (iAcc: ScanAcc) (idx: int) : Result<ScanAcc, UrcError> =
                                    if idx >= ids.Length then
                                        Result.Ok iAcc
                                    else
                                        let obj = ids[idx]
                                        let y = boundaries[m] + (float idx / float ids.Length) * rate
                                        if Set.contains channel systemChannels then
                                            if obj <> "00" then
                                                match channel with
                                                | "03" ->
                                                    let digits = idValue obj chart.Base
                                                    let bpmVal = float ((digits / 36) * 16 + (digits % 36))
                                                    scanIds { iAcc with Entries = (y, 0, EntryKind.Bpm bpmVal) :: iAcc.Entries } (idx + 1)
                                                | "08" ->
                                                    match Map.tryFind obj chart.BpmDefs with
                                                    | Some bpmVal -> scanIds { iAcc with Entries = (y, 0, EntryKind.Bpm bpmVal) :: iAcc.Entries } (idx + 1)
                                                    | None -> Result.Error(UrcError.Syntax(1, $"undefined #BPM{obj}"))
                                                | "09" ->
                                                    match Map.tryFind obj chart.StopDefs with
                                                    | Some stopVal -> scanIds { iAcc with Entries = (y, 1, EntryKind.Stop stopVal) :: iAcc.Entries } (idx + 1)
                                                    | None -> Result.Error(UrcError.Syntax(1, $"undefined #STOP{obj}"))
                                                | _ ->
                                                    match Map.tryFind obj chart.ScrollDefs with
                                                    | Some scrollVal -> scanIds { iAcc with Entries = (y, 2, EntryKind.Scroll scrollVal) :: iAcc.Entries } (idx + 1)
                                                    | None -> Result.Error(UrcError.Syntax(1, $"undefined #SCROLL{obj}"))
                                            else
                                                scanIds iAcc (idx + 1)
                                        else
                                            match channelKind channel with
                                            | None -> scanIds iAcc (idx + 1)
                                            | Some kind ->
                                                let used =
                                                    if obj <> "00" then Set.add (sideOf channel[0], channel[1]) iAcc.Used
                                                    else iAcc.Used
                                                if not (obj = "00" && kind <> "ln") then
                                                    let objIdx = iAcc.Objects.Length
                                                    let objects = (y, channel, obj) :: iAcc.Objects
                                                    let entries = (y, 4, EntryKind.Object objIdx) :: iAcc.Entries
                                                    scanIds { Entries = entries; Objects = objects; Used = used } (idx + 1)
                                                else
                                                    scanIds { iAcc with Used = used } (idx + 1)

                                match scanIds cAcc 0 with
                                | Result.Ok nextAcc -> scanChannels nextAcc restChannels
                                | Result.Error err -> Result.Error err

                        match scanChannels accWithMeter (Map.toList measureMap) with
                        | Result.Ok nextAcc -> scanMeasures nextAcc (m + 1)
                        | Result.Error err -> Result.Error err

            let! scanned = scanMeasures initialAcc 0
            let mode = detectMode chart.Pms scanned.Used

            let sortedEntries =
                (boundaries
                 |> Seq.map (fun y -> y, 5, EntryKind.Anchor)
                 |> List.ofSeq)
                @ scanned.Entries
                |> List.sortBy (fun (y, order, _) -> y, order)

            let groupedEntries =
                sortedEntries
                |> List.groupBy (fun (y, _, _) -> y)

            let initialTiming =
                {
                    TimeUs = 0.0
                    PrevY = 0.0
                    PendingStop = 0.0
                    CurrentBpm = None
                    CurrentBeats = 4
                    BpmPoints = []
                    SvPoints = []
                    Anchors = []
                    Timed = Map.empty
                }

            let foldTimingGroup (state: TimingScanState) (y: float, group: (float * int * EntryKind) list) =
                let timeUs =
                    match state.CurrentBpm with
                    | Some b -> state.TimeUs + (MeasureUs * (y - state.PrevY) / b)
                    | None -> state.TimeUs
                    + state.PendingStop

                let rec foldGroupEvents nextBpm nextBeats pendingStop scroll anchors timed (events: (float * int * EntryKind) list) =
                    match events with
                    | [] -> nextBpm, nextBeats, pendingStop, scroll, anchors, timed
                    | (_, _, kind) :: rest ->
                        match kind with
                        | EntryKind.Bpm v -> foldGroupEvents (Some v) nextBeats pendingStop scroll anchors timed rest
                        | EntryKind.Meter v -> foldGroupEvents nextBpm v pendingStop scroll anchors timed rest
                        | EntryKind.Stop v ->
                            let stopTime = MeasureUs * v / nextBpm.Value
                            foldGroupEvents nextBpm nextBeats stopTime scroll anchors timed rest
                        | EntryKind.Scroll v -> foldGroupEvents nextBpm nextBeats pendingStop (Some v) anchors timed rest
                        | EntryKind.Object idx -> foldGroupEvents nextBpm nextBeats pendingStop scroll anchors (Map.add idx timeUs timed) rest
                        | EntryKind.Anchor -> foldGroupEvents nextBpm nextBeats pendingStop scroll (Shared.roundMs (timeUs / 1000.0) :: anchors) timed rest

                let nextBpm, nextBeats, pendingStop, scroll, nextAnchors, nextTimed =
                    foldGroupEvents state.CurrentBpm state.CurrentBeats 0.0 None state.Anchors state.Timed group

                let bpmPoints =
                    if nextBpm <> state.CurrentBpm || nextBeats <> state.CurrentBeats then
                        (Shared.roundMs (timeUs / 1000.0), nextBpm.Value, nextBeats) :: state.BpmPoints
                    else
                        state.BpmPoints

                let svPoints =
                    match scroll with
                    | Some s -> (Shared.roundMs (timeUs / 1000.0), s) :: state.SvPoints
                    | None -> state.SvPoints

                {
                    TimeUs = timeUs
                    PrevY = y
                    PendingStop = pendingStop
                    CurrentBpm = nextBpm
                    CurrentBeats = nextBeats
                    BpmPoints = bpmPoints
                    SvPoints = svPoints
                    Anchors = nextAnchors
                    Timed = nextTimed
                }

            let timingState = (initialTiming, groupedEntries) ||> List.fold foldTimingGroup

            let! rawNotes = buildNotes chart mode scanned.Objects timingState.Timed
            let firstNoteTime =
                rawNotes
                |> List.filter (fun (_, _, t) -> t <> NoteType.LE)
                |> List.map (fun (t, _, _) -> t)
                |> function
                    | [] -> 0
                    | xs -> List.min xs

            let anchorMs = timingState.Anchors |> List.rev |> List.tryFind (fun time -> time >= firstNoteTime)

            let! timing =
                Shared.buildTiming (List.rev timingState.BpmPoints) (List.rev timingState.SvPoints) firstNoteTime ".bms" anchorMs

            let typeOrder = function
                | NoteType.N -> 0
                | NoteType.LS -> 1
                | NoteType.LE -> 2
                | NoteType.M -> 3
                | NoteType.F -> 4

            let urcNotes =
                rawNotes
                |> List.map (fun (t, lane, nt) ->
                    {
                        TimestampMs = t - firstNoteTime
                        Lane = lane
                        Type = nt
                    })
                |> List.sortBy (fun n -> n.TimestampMs, n.Lane, typeOrder n.Type)

            do! Shared.checkHoldOverlap urcNotes

            let keys, specialKeys, specialLanes =
                match mode with
                | "5K" -> 5, 1, Some [ 0 ]
                | "7K" -> 7, 1, Some [ 0 ]
                | "10K" -> 10, 2, Some [ 0; 6 ]
                | "14K" -> 14, 2, Some [ 0; 8 ]
                | "PMS9" -> 9, 0, None
                | "PMS18" -> 18, 0, None
                | _ -> failwith "unreachable"

            return
                {
                    FormatVersion = { Major = 1; Minor = 1 }
                    Metadata =
                        {
                            Original = if chart.Pms then "PMS" else "BMS"
                            Title = chart.Title |> Option.defaultValue "Unknown"
                            Artist = chart.Artist |> Option.defaultValue "Unknown"
                            Creator = "Unknown"
                            Version = chart.PlayLevel |> Option.defaultValue "Unknown"
                        }
                    Judgment = None
                    Layout =
                        {
                            Keys = keys
                            SpecialKeys = specialKeys
                            SpecialLanes = specialLanes
                        }
                    TimingPoints = timing
                    Notes = urcNotes
                }
        }
