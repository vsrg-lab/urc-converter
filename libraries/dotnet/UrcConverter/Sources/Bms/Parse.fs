namespace UrcConverter.Sources.Bms

module Parse =

    open System
    open System.Globalization
    open System.Text
    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources.Bms.Model

    type ParseOptions =
        {
            Pms: bool
            Seed: int64 option
            Branches: uint64 list option
        }

    [<RequireQualifiedAccess>]
    module private JavaRandom =
        let private mask = (1UL <<< 48) - 1UL
        let private mult = 0x5DEECE66DUL
        let private add = 0xBUL

        let create (seed: int64) : uint64 =
            ((uint64 seed) ^^^ mult) &&& mask

        let next (state: uint64) (bits: int) : uint64 * uint64 =
            let nextState = ((state * mult) + add) &&& mask
            (nextState >>> (48 - bits)), nextState

        let nextInt (state: uint64) (bound: uint64) : uint64 * uint64 =
            if (bound &&& (~~~bound + 1UL)) = bound then
                let raw, s1 = next state 31
                ((bound * raw) >>> 31), s1
            else
                let rec loop currState =
                    let bits, s1 = next currState 31
                    let value = bits % bound
                    if bits - value + (bound - 1UL) <= 0x7FFFFFFFUL then
                        value, s1
                    else
                        loop s1
                loop state

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

    type private LoopState =
        {
            Chart: BmsChart
            Frames: (bool * bool) list
            RandomValue: uint64 option
            BranchIndex: int
            Rng: uint64 option
        }

    let parseBms (data: byte[]) (options: ParseOptions) : Result<BmsChart, UrcError> =
        result {
            let! text = decode data
            let! b = scanBase text
            let lines = text.TrimStart('\uFEFF').Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.None)

            let initialChart =
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

            let initialLoop =
                {
                    Chart = initialChart
                    Frames = []
                    RandomValue = None
                    BranchIndex = 0
                    Rng = options.Seed |> Option.map JavaRandom.create
                }

            let rec step (state: LoopState) (idx: int) : Result<BmsChart, UrcError> =
                if idx >= lines.Length then
                    if not (List.isEmpty state.Frames) then
                        Result.Error(UrcError.Syntax(1, "unterminated #IF block"))
                    else
                        Result.Ok state.Chart
                else
                    let lineNo = idx + 1
                    let line = lines[idx].Trim()
                    if not (line.StartsWith("#")) then
                        step state (idx + 1)
                    else
                        let head = line.Substring(1)
                        if head.Length >= 6 && Char.IsDigit(head[0]) && Char.IsDigit(head[1]) && Char.IsDigit(head[2]) && head[5] = ':' then
                            if not (state.Frames |> List.exists fst) then
                                match Int32.TryParse(head.Substring(0, 3)) with
                                | true, measure ->
                                    let channel = head.Substring(3, 2)
                                    let payload = head.Substring(6)
                                    addMessage state.Chart measure channel payload lineNo
                                    |> Result.bind (fun updated -> step { state with Chart = updated } (idx + 1))
                                | false, _ ->
                                    Result.Error(UrcError.Syntax(lineNo, $"invalid measure in header: {head}"))
                            else
                                step state (idx + 1)
                        else
                            let spaceIdx = head.IndexOf(' ')
                            let command = if spaceIdx = -1 then head else head.Substring(0, spaceIdx)
                            let argument = if spaceIdx = -1 then "" else head.Substring(spaceIdx + 1).Trim()
                            let word = command.ToUpperInvariant()

                            result {
                                match word with
                                | "RANDOM" ->
                                    let! count = toInt argument lineNo "#RANDOM"
                                    if count < 1 then
                                        return! Result.Error(UrcError.Syntax(lineNo, $"#RANDOM count must be >= 1: {count}"))
                                    let! pick, nextRng =
                                        match options.Branches with
                                        | Some branches when state.BranchIndex < branches.Length ->
                                            let p = branches[state.BranchIndex]
                                            if p < 1UL || p > uint64 count then
                                                Result.Error(UrcError.Syntax(lineNo, $"branch pick out of range: {p}"))
                                            else
                                                Result.Ok(p, state.Rng)
                                        | _ ->
                                            match state.Rng with
                                            | Some r ->
                                                let v, nextR = JavaRandom.nextInt r (uint64 count)
                                                Result.Ok(v + 1UL, Some nextR)
                                            | None -> Result.Ok(1UL, None)
                                    return! step { state with RandomValue = Some pick; BranchIndex = state.BranchIndex + 1; Rng = nextRng } (idx + 1)
                                | "IF" ->
                                    let! v =
                                        state.RandomValue
                                        |> Option.map Result.Ok
                                        |> Option.defaultValue (Result.Error(UrcError.Syntax(lineNo, "unmatched #IF")))
                                    let! cond = toInt argument lineNo "#IF"
                                    let frames = (v <> uint64 cond, v = uint64 cond) :: state.Frames
                                    return! step { state with Frames = frames } (idx + 1)
                                | "ELSEIF" ->
                                    match state.Frames with
                                    | [] -> return! Result.Error(UrcError.Syntax(lineNo, "unmatched #ELSEIF"))
                                    | (active, matched) :: rest ->
                                        let! cond = toInt argument lineNo "#ELSEIF"
                                        let v = state.RandomValue.Value
                                        let nowMatched = matched || v = uint64 cond
                                        return! step { state with Frames = (not nowMatched, nowMatched) :: rest } (idx + 1)
                                | "ELSE" ->
                                    match state.Frames with
                                    | [] -> return! Result.Error(UrcError.Syntax(lineNo, "unmatched #ELSE"))
                                    | (active, matched) :: rest ->
                                        return! step { state with Frames = (matched, true) :: rest } (idx + 1)
                                | "ENDIF" ->
                                    match state.Frames with
                                    | [] -> return! Result.Error(UrcError.Syntax(lineNo, "unmatched #ENDIF"))
                                    | _ :: rest -> return! step { state with Frames = rest } (idx + 1)
                                | "SETRANDOM" | "ENDRANDOM" | "SWITCH" | "CASE" | "SKIP" | "DEF" | "ENDSW" | "SETSWITCH" ->
                                    return! Result.Error(UrcError.UnsupportedVersion(lineNo, $"unsupported BMS command: #{command}"))
                                | _ when state.Frames |> List.exists fst ->
                                    return! step state (idx + 1)
                                | "BPM" ->
                                    let! bpm = toFloat argument lineNo "#BPM"
                                    return! step { state with Chart = { state.Chart with Bpm = Some bpm } } (idx + 1)
                                | _ when (command.Length = 5 && command.StartsWith("BPM", StringComparison.OrdinalIgnoreCase))
                                      || (command.Length = 8 && command.StartsWith("EXBPM", StringComparison.OrdinalIgnoreCase)) ->
                                    let! bpm = toFloat argument lineNo command
                                    let chart = { state.Chart with BpmDefs = Map.add (command.Substring(command.Length - 2)) bpm state.Chart.BpmDefs }
                                    return! step { state with Chart = chart } (idx + 1)
                                | _ when command.Length = 6 && command.StartsWith("STOP", StringComparison.OrdinalIgnoreCase) ->
                                    let! stop = toFloat argument lineNo command
                                    let chart = { state.Chart with StopDefs = Map.add (command.Substring(command.Length - 2)) (abs stop / 192.0) state.Chart.StopDefs }
                                    return! step { state with Chart = chart } (idx + 1)
                                | _ when command.Length = 8 && command.StartsWith("SCROLL", StringComparison.OrdinalIgnoreCase) ->
                                    let! scroll = toFloat argument lineNo command
                                    let chart = { state.Chart with ScrollDefs = Map.add (command.Substring(command.Length - 2)) scroll state.Chart.ScrollDefs }
                                    return! step { state with Chart = chart } (idx + 1)
                                | "LNTYPE" ->
                                    let! lntype = toInt argument lineNo "#LNTYPE"
                                    if lntype <> 1 && lntype <> 2 then
                                        return! Result.Error(UrcError.Syntax(lineNo, $"unsupported #LNTYPE: {lntype}"))
                                    return! step { state with Chart = { state.Chart with LnType = lntype } } (idx + 1)
                                | "LNOBJ" ->
                                    let chart = { state.Chart with LnObj = if String.IsNullOrEmpty argument then None else Some argument }
                                    return! step { state with Chart = chart } (idx + 1)
                                | "TITLE" ->
                                    let chart = { state.Chart with Title = if String.IsNullOrEmpty argument then None else Some argument }
                                    return! step { state with Chart = chart } (idx + 1)
                                | "ARTIST" ->
                                    let chart = { state.Chart with Artist = if String.IsNullOrEmpty argument then None else Some argument }
                                    return! step { state with Chart = chart } (idx + 1)
                                | "PLAYLEVEL" ->
                                    let chart = { state.Chart with PlayLevel = if String.IsNullOrEmpty argument then None else Some argument }
                                    return! step { state with Chart = chart } (idx + 1)
                                | _ ->
                                    return! step state (idx + 1)
                            }

            return! step initialLoop 0
        }
