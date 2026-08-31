namespace UrcConverter.Sources

open System
open System.Globalization
open System.Text
open FsToolkit.ErrorHandling
open UrcConverter

module Bms =

    [<Literal>]
    let private MeasureUs = 240000000.0

    let private systemChannels = Set.ofList [ "02"; "03"; "08"; "09"; "SC" ]

    type ParseOptions =
        {
            Pms: bool
            Seed: int64 option
            Branches: uint64 list option
        }

    type BmsChart =
        {
            Pms: bool
            Base: int
            Title: string option
            Artist: string option
            PlayLevel: string option
            Bpm: float option
            LnType: int
            LnObj: string option
            BpmDefs: Map<string, float>
            StopDefs: Map<string, float>
            ScrollDefs: Map<string, float>
            Rates: Map<int, float>
            Measures: Map<int, Map<string, string list>>
        }

    type private JavaRandom =
        {
            mutable State: uint64
        }

    module private JavaRandom =
        let mask = (1UL <<< 48) - 1UL
        let mult = 0x5DEECE66DUL
        let add = 0xBUL

        let create (seed: int64) : JavaRandom =
            { State = ((uint64 seed) ^^^ mult) &&& mask }

        let next (rng: JavaRandom) (bits: int) : uint64 =
            rng.State <- ((rng.State * mult) + add) &&& mask
            rng.State >>> (48 - bits)

        let nextInt (rng: JavaRandom) (bound: uint64) : uint64 =
            if (bound &&& (~~~bound + 1UL)) = bound then
                (bound * (next rng 31)) >>> 31
            else
                let rec loop () =
                    let bits = next rng 31
                    let value = bits % bound
                    if bits - value + (bound - 1UL) <= 0x7FFFFFFFUL then
                        value
                    else
                        loop ()
                loop ()

    let private decode (bytes: byte[]) : Result<string, UrcError> =
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)
        let cleanBytes =
            if bytes.Length >= 3 && bytes[0] = 0xefuy && bytes[1] = 0xbbuy && bytes[2] = 0xbfuy then
                bytes[3..]
            else
                bytes

        let utf8 = UTF8Encoding(false, true)
        try
            Result.Ok(utf8.GetString(cleanBytes))
        with _ ->
            try
                let sjis = Encoding.GetEncoding("shift-jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                Result.Ok(sjis.GetString(cleanBytes))
            with _ ->
                Result.Error(UrcError.Syntax(1, "undecodable bytes: expected UTF-8 or Shift_JIS"))

    let private scanBase (text: string) : Result<int, UrcError> =
        let lines = text.Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.None)
        let rec loop (idx: int) =
            if idx >= lines.Length then
                Result.Ok 36
            else
                let parts = lines[idx].Trim().Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                if parts.Length = 2 && String.Equals(parts[0], "#BASE", StringComparison.OrdinalIgnoreCase) then
                    match Int32.TryParse(parts[1]) with
                    | true, b when b = 36 || b = 62 -> Result.Ok b
                    | true, b -> Result.Error(UrcError.Syntax(1, $"unsupported #BASE: {b}"))
                    | false, _ -> Result.Error(UrcError.Syntax(1, $"invalid #BASE: {parts[1]}"))
                else
                    loop (idx + 1)
        loop 0

    let private isIdChar (c: char) =
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')

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
            let mutable seven = false
            let mutable double = false
            for side, second in used do
                if second = '8' || second = '9' then seven <- true
                if side = 1 then double <- true
            if seven && double then "14K"
            elif double then "10K"
            elif seven then "7K"
            else "5K"

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

    let private toInt (value: string) (lineNo: int) (name: string) : Result<int, UrcError> =
        match Int32.TryParse(value.Trim()) with
        | true, v -> Result.Ok v
        | false, _ -> Result.Error(UrcError.Syntax(lineNo, $"invalid {name}: {value}"))

    let private toFloat (value: string) (lineNo: int) (name: string) : Result<float, UrcError> =
        match Double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, v -> Result.Ok v
        | false, _ -> Result.Error(UrcError.Syntax(lineNo, $"invalid {name}: {value}"))

    let private addMessage
        (chart: BmsChart)
        (measure: int)
        (channel: string)
        (payload: string)
        (lineNo: int)
        : Result<BmsChart, UrcError> =
        result {
            if channel = "02" then
                let! rate = toFloat payload lineNo "measure length"
                if rate < 0.0 then
                    return! Result.Error(UrcError.Syntax(lineNo, $"negative measure length: {payload}"))
                return { chart with Rates = Map.add measure rate chart.Rates }
            else
                if payload.Length % 2 <> 0 || not (payload |> Seq.forall isIdChar) then
                    return! Result.Error(UrcError.Syntax(lineNo, $"malformed object list: \"{payload}\""))
                let ids = [ for i in 0 .. 2 .. payload.Length - 2 -> payload.Substring(i, 2) ]
                let measureMap = Map.tryFind measure chart.Measures |> Option.defaultValue Map.empty
                let channelList = Map.tryFind channel measureMap |> Option.defaultValue []
                let updatedChannelList = channelList @ ids
                let updatedMeasureMap = Map.add channel updatedChannelList measureMap
                return { chart with Measures = Map.add measure updatedMeasureMap chart.Measures }
        }

    let parseBms (data: byte[]) (options: ParseOptions) : Result<BmsChart, UrcError> =
        result {
            let! text = decode data
            let! b = scanBase text
            let lines = text.TrimStart('\uFEFF').Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.None)

            let mutable chart =
                {
                    Pms = options.Pms
                    Base = b
                    Title = None
                    Artist = None
                    PlayLevel = None
                    Bpm = None
                    LnType = 1
                    LnObj = None
                    BpmDefs = Map.empty
                    StopDefs = Map.empty
                    ScrollDefs = Map.empty
                    Rates = Map.empty
                    Measures = Map.empty
                }

            let rng = options.Seed |> Option.map JavaRandom.create
            let mutable frames: (bool * bool) list = []
            let mutable randomValue: uint64 option = None
            let mutable branchIndex = 0

            for i in 0 .. lines.Length - 1 do
                let lineNo = i + 1
                let line = lines[i].Trim()
                if line.StartsWith("#") then
                    let head = line.Substring(1)
                    if head.Length >= 6 && Char.IsDigit(head[0]) && Char.IsDigit(head[1]) && Char.IsDigit(head[2]) && head[5] = ':' then
                        if not (frames |> List.exists fst) then
                            let measure = Int32.Parse(head.Substring(0, 3))
                            let channel = head.Substring(3, 2)
                            let payload = head.Substring(6)
                            let! updated = addMessage chart measure channel payload lineNo
                            chart <- updated
                    else
                        let spaceIdx = head.IndexOf(' ')
                        let command = if spaceIdx = -1 then head else head.Substring(0, spaceIdx)
                        let argument = if spaceIdx = -1 then "" else head.Substring(spaceIdx + 1).Trim()
                        let word = command.ToUpperInvariant()

                        match word with
                        | "RANDOM" ->
                            let! count = toInt argument lineNo "#RANDOM"
                            if count < 1 then
                                return! Result.Error(UrcError.Syntax(lineNo, $"#RANDOM count must be >= 1: {count}"))
                            let! pick =
                                match options.Branches with
                                | Some branches when branchIndex < branches.Length ->
                                    let p = branches[branchIndex]
                                    if p < 1UL || p > uint64 count then
                                        Result.Error(UrcError.Syntax(lineNo, $"branch pick out of range: {p}"))
                                    else
                                        Result.Ok p
                                | _ ->
                                    match rng with
                                    | Some r -> Result.Ok (JavaRandom.nextInt r (uint64 count) + 1UL)
                                    | None -> Result.Ok 1UL
                            randomValue <- Some pick
                            branchIndex <- branchIndex + 1
                        | "IF" ->
                            let! v =
                                randomValue
                                |> Option.map Result.Ok
                                |> Option.defaultValue (Result.Error(UrcError.Syntax(lineNo, "unmatched #IF")))
                            let! cond = toInt argument lineNo "#IF"
                            frames <- (v <> uint64 cond, v = uint64 cond) :: frames
                        | "ELSEIF" ->
                            match frames with
                            | [] -> return! Result.Error(UrcError.Syntax(lineNo, "unmatched #ELSEIF"))
                            | (active, matched) :: rest ->
                                let! cond = toInt argument lineNo "#ELSEIF"
                                let v = randomValue.Value
                                let nowMatched = matched || v = uint64 cond
                                frames <- (not nowMatched, nowMatched) :: rest
                        | "ELSE" ->
                            match frames with
                            | [] -> return! Result.Error(UrcError.Syntax(lineNo, "unmatched #ELSE"))
                            | (active, matched) :: rest ->
                                frames <- (matched, true) :: rest
                        | "ENDIF" ->
                            match frames with
                            | [] -> return! Result.Error(UrcError.Syntax(lineNo, "unmatched #ENDIF"))
                            | _ :: rest -> frames <- rest
                        | "SETRANDOM" | "ENDRANDOM" | "SWITCH" | "CASE" | "SKIP" | "DEF" | "ENDSW" | "SETSWITCH" ->
                            return! Result.Error(UrcError.UnsupportedVersion(lineNo, $"unsupported BMS command: #{command}"))
                        | _ when frames |> List.exists fst -> ()
                        | "BPM" ->
                            let! bpm = toFloat argument lineNo "#BPM"
                            chart <- { chart with Bpm = Some bpm }
                        | _ when (command.Length = 5 && command.StartsWith("BPM", StringComparison.OrdinalIgnoreCase))
                              || (command.Length = 8 && command.StartsWith("EXBPM", StringComparison.OrdinalIgnoreCase)) ->
                            let! bpm = toFloat argument lineNo command
                            chart <- { chart with BpmDefs = Map.add (command.Substring(command.Length - 2)) bpm chart.BpmDefs }
                        | _ when command.Length = 6 && command.StartsWith("STOP", StringComparison.OrdinalIgnoreCase) ->
                            let! stop = toFloat argument lineNo command
                            chart <- { chart with StopDefs = Map.add (command.Substring(command.Length - 2)) (abs stop / 192.0) chart.StopDefs }
                        | _ when command.Length = 8 && command.StartsWith("SCROLL", StringComparison.OrdinalIgnoreCase) ->
                            let! scroll = toFloat argument lineNo command
                            chart <- { chart with ScrollDefs = Map.add (command.Substring(command.Length - 2)) scroll chart.ScrollDefs }
                        | "LNTYPE" ->
                            let! lntype = toInt argument lineNo "#LNTYPE"
                            if lntype <> 1 && lntype <> 2 then
                                return! Result.Error(UrcError.Syntax(lineNo, $"unsupported #LNTYPE: {lntype}"))
                            chart <- { chart with LnType = lntype }
                        | "LNOBJ" ->
                            chart <- { chart with LnObj = if String.IsNullOrEmpty argument then None else Some argument }
                        | "TITLE" ->
                            chart <- { chart with Title = if String.IsNullOrEmpty argument then None else Some argument }
                        | "ARTIST" ->
                            chart <- { chart with Artist = if String.IsNullOrEmpty argument then None else Some argument }
                        | "PLAYLEVEL" ->
                            chart <- { chart with PlayLevel = if String.IsNullOrEmpty argument then None else Some argument }
                        | _ -> ()

            if not (List.isEmpty frames) then
                return! Result.Error(UrcError.Syntax(1, "unterminated #IF block"))
            return chart
        }

    let private pairLongNotes (chart: BmsChart) (stream: (float * string) list) (lane: int) : Result<(int * int * NoteType) list, UrcError> =
        let mutable start: float option = None
        let mutable notes: (int * int * NoteType) list = []

        if chart.LnType = 1 then
            for time, obj in stream do
                if obj <> "00" then
                    match start with
                    | None -> start <- Some time
                    | Some s ->
                        notes <- (Shared.roundMs (s / 1000.0), lane, NoteType.LS) :: notes
                        notes <- (Shared.roundMs (time / 1000.0), lane, NoteType.LE) :: notes
                        start <- None
        else
            for time, obj in stream do
                if obj = "00" then
                    match start with
                    | Some s ->
                        notes <- (Shared.roundMs (s / 1000.0), lane, NoteType.LS) :: notes
                        notes <- (Shared.roundMs (time / 1000.0), lane, NoteType.LE) :: notes
                        start <- None
                    | None -> ()
                elif Option.isNone start then
                    start <- Some time

        match start with
        | Some _ -> Result.Error(UrcError.Syntax(1, $"long note on lane {lane} has no end"))
        | None -> Result.Ok(List.rev notes)

    let private buildNotes
        (chart: BmsChart)
        (mode: string)
        (objects: (float * string * string) list)
        (timed: float[])
        : Result<(int * int * NoteType) list, UrcError> =
        result {
            let streams =
                objects
                |> List.indexed
                |> List.groupBy (fun (_, (_, channel, _)) -> channel)
                |> List.map (fun (channel, group) ->
                    channel, group |> List.map (fun (idx, (_, _, obj)) -> timed[idx], obj))

            let mutable allNotes = []
            for channel, stream in streams do
                match getLane mode channel, channelKind channel with
                | Some lane, Some "mine" ->
                    for time, _ in stream do
                        allNotes <- (Shared.roundMs (time / 1000.0), lane, NoteType.M) :: allNotes
                | Some lane, Some "ln" ->
                    let! paired = pairLongNotes chart stream lane
                    allNotes <- (List.rev paired) @ allNotes
                | Some lane, Some _ ->
                    let mutable pending = None
                    for time, obj in stream do
                        match chart.LnObj, pending with
                        | Some lnobj, Some p when obj = lnobj ->
                            allNotes <- (Shared.roundMs (p / 1000.0), lane, NoteType.LS) :: allNotes
                            allNotes <- (Shared.roundMs (time / 1000.0), lane, NoteType.LE) :: allNotes
                            pending <- None
                        | _ ->
                            match pending with
                            | Some p -> allNotes <- (Shared.roundMs (p / 1000.0), lane, NoteType.N) :: allNotes
                            | None -> ()
                            pending <- Some time
                    match pending with
                    | Some p -> allNotes <- (Shared.roundMs (p / 1000.0), lane, NoteType.N) :: allNotes
                    | None -> ()
                | _ -> ()

            return List.rev allNotes
        }

    [<RequireQualifiedAccess>]
    type private EntryKind =
        | Bpm of float
        | Meter of int
        | Stop of float
        | Scroll of float
        | Object of int

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

            let boundaries = Array.zeroCreate (maxMeasure + 2)
            boundaries[0] <- 0.0
            for m in 0 .. maxMeasure do
                let rate = Map.tryFind m chart.Rates |> Option.defaultValue 1.0
                boundaries[m + 1] <- boundaries[m] + rate

            let mutable entries: (float * int * EntryKind) list = [ (0.0, 0, EntryKind.Bpm bpmInitial) ]
            let mutable objects: (float * string * string) list = []
            let mutable used: Set<int * char> = Set.empty

            for m in 0 .. maxMeasure do
                let rate = Map.tryFind m chart.Rates |> Option.defaultValue 1.0
                let prevRate = Map.tryFind (m - 1) chart.Rates |> Option.defaultValue 1.0
                if rate <> prevRate then
                    let beats = rate * 4.0
                    if abs (beats - round beats) < 1e-9 && round beats >= 1.0 then
                        entries <- (boundaries[m], 3, EntryKind.Meter (int (round beats))) :: entries

                match Map.tryFind m chart.Measures with
                | None -> ()
                | Some measureMap ->
                    for KeyValue(channel, ids) in measureMap do
                        for idx in 0 .. ids.Length - 1 do
                            let obj = ids[idx]
                            let y = boundaries[m] + (float idx / float ids.Length) * rate

                            if Set.contains channel systemChannels then
                                if obj <> "00" then
                                    match channel with
                                    | "03" ->
                                        let digits = idValue obj chart.Base
                                        let bpmVal = float ((digits / 36) * 16 + (digits % 36))
                                        entries <- (y, 0, EntryKind.Bpm bpmVal) :: entries
                                    | "08" ->
                                        match Map.tryFind obj chart.BpmDefs with
                                        | Some bpmVal -> entries <- (y, 0, EntryKind.Bpm bpmVal) :: entries
                                        | None -> return! Result.Error(UrcError.Syntax(1, $"undefined #BPM{obj}"))
                                    | "09" ->
                                        match Map.tryFind obj chart.StopDefs with
                                        | Some stopVal -> entries <- (y, 1, EntryKind.Stop stopVal) :: entries
                                        | None -> return! Result.Error(UrcError.Syntax(1, $"undefined #STOP{obj}"))
                                    | _ ->
                                        match Map.tryFind obj chart.ScrollDefs with
                                        | Some scrollVal -> entries <- (y, 2, EntryKind.Scroll scrollVal) :: entries
                                        | None -> return! Result.Error(UrcError.Syntax(1, $"undefined #SCROLL{obj}"))
                            else
                                match channelKind channel with
                                | None -> ()
                                | Some kind ->
                                    if obj <> "00" then
                                        used <- Set.add (sideOf channel[0], channel[1]) used
                                    if not (obj = "00" && kind <> "ln") then
                                        let objIdx = objects.Length
                                        objects <- objects @ [ (y, channel, obj) ]
                                        entries <- (y, 4, EntryKind.Object objIdx) :: entries

            let mode = detectMode chart.Pms used

            let sortedEntries =
                entries
                |> List.sortBy (fun (y, order, _) -> y, order)

            let groupedEntries =
                sortedEntries
                |> List.groupBy (fun (y, _, _) -> y)

            let timed = Array.zeroCreate objects.Length
            let mutable currentBpm = None
            let mutable currentBeats = 4
            let mutable timeUs = 0.0
            let mutable prevY = 0.0
            let mutable pendingStop = 0.0
            let mutable bpmPoints: (int * float * int) list = []
            let mutable svPoints: (int * float) list = []

            for y, group in groupedEntries do
                match currentBpm with
                | Some b -> timeUs <- timeUs + (MeasureUs * (y - prevY) / b)
                | None -> ()
                timeUs <- timeUs + pendingStop
                pendingStop <- 0.0

                let mutable nextBpm = currentBpm
                let mutable nextBeats = currentBeats
                let mutable scroll = None

                for _, _, kind in group do
                    match kind with
                    | EntryKind.Bpm v -> nextBpm <- Some v
                    | EntryKind.Meter v -> nextBeats <- v
                    | EntryKind.Stop v -> pendingStop <- MeasureUs * v / nextBpm.Value
                    | EntryKind.Scroll v -> scroll <- Some v
                    | EntryKind.Object idx -> timed[idx] <- timeUs

                if nextBpm <> currentBpm || nextBeats <> currentBeats then
                    bpmPoints <- (Shared.roundMs (timeUs / 1000.0), nextBpm.Value, nextBeats) :: bpmPoints
                match scroll with
                | Some s -> svPoints <- (Shared.roundMs (timeUs / 1000.0), s) :: svPoints
                | None -> ()

                currentBpm <- nextBpm
                currentBeats <- nextBeats
                prevY <- y

            let! rawNotes = buildNotes chart mode objects timed
            let firstNoteTime =
                rawNotes
                |> List.filter (fun (_, _, t) -> t <> NoteType.LE)
                |> List.map (fun (t, _, _) -> t)
                |> function
                    | [] -> 0
                    | xs -> List.min xs

            let! timing = Shared.buildTiming (List.rev bpmPoints) (List.rev svPoints) firstNoteTime ".bms"

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
