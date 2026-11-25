namespace UrcConverter.Sources.Bms

[<RequireQualifiedAccess>]
module Parse =

    open System
    open System.Globalization
    open System.Text
    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Bms.Model

    type ParseOptions =
        {
            Pms: bool
            Seed: int64 option
            Branches: uint64 list option
        }

        static member Default =
            {
                Pms = false
                Seed = None
                Branches = None
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
        Shared.ensureCodePages ()

        let cleanBytes =
            if bytes.Length >= 3 && bytes[0] = 0xefuy && bytes[1] = 0xbbuy && bytes[2] = 0xbfuy then
                bytes[3..]
            else
                bytes

        let utf8 = UTF8Encoding(false, true)

        try
            Ok(utf8.GetString(cleanBytes))
        with _ ->
            try
                let sjis =
                    Encoding.GetEncoding(
                        "shift-jis",
                        EncoderFallback.ExceptionFallback,
                        DecoderFallback.ExceptionFallback
                    )

                Ok(sjis.GetString(cleanBytes))
            with _ ->
                Error(UrcError.syntax 1 "undecodable bytes: expected UTF-8 or Shift_JIS")

    let private scanBase (lines: string[]) : Result<int, UrcError> =
        let rec loop (idx: int) =
            if idx >= lines.Length then
                Ok 36
            else
                let parts =
                    lines[idx]
                        .Trim()
                        .Split((null: char[]), StringSplitOptions.RemoveEmptyEntries)

                if
                    parts.Length = 2
                    && String.Equals(parts[0], "#BASE", StringComparison.OrdinalIgnoreCase)
                then
                    match Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture) with
                    | true, b when b = 36 || b = 62 -> Ok b
                    | true, b -> Error(UrcError.syntax 1 $"unsupported #BASE: {b}")
                    | false, _ -> Error(UrcError.syntax 1 $"invalid #BASE: {parts[1]}")
                else
                    loop (idx + 1)

        loop 0

    let private isAsciiDigit (c: char) = c >= '0' && c <= '9'

    let private isIdChar (c: char) =
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')

    let private normId (token: string) (b: int) =
        if b = 36 then token.ToUpperInvariant() else token

    let private toInt (value: string) (lineNo: int) (name: string) : Result<int, UrcError> =
        match Int32.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, v -> Ok v
        | false, _ -> Error(UrcError.syntax lineNo $"invalid {name}: {value}")

    let private toInt64 (value: string) (lineNo: int) (name: string) : Result<int64, UrcError> =
        match Int64.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, v -> Ok v
        | false, _ -> Error(UrcError.syntax lineNo $"invalid {name}: {value}")

    let private toFloat (value: string) (lineNo: int) (name: string) : Result<float, UrcError> =
        match Double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, v when Double.IsFinite v -> Ok v
        | _ -> Error(UrcError.syntax lineNo $"invalid {name}: {value}")

    let private isRelevantChannel (channel: string) =
        if channel = "03" || channel = "08" || channel = "09" || channel = "SC" then
            true
        elif channel.Length <> 2 then
            false
        else
            let c0, c1 = channel[0], channel[1]
            (c0 = '1' || c0 = '2' || c0 = '5' || c0 = '6' || c0 = 'D' || c0 = 'E') && c1 >= '1' && c1 <= '9'

    type private LoopState =
        {
            Chart: BmsChart
            MeasureChunks: Map<int, Map<string, string list list>>
            Frames: (bool * bool) list
            RandomValue: int64 option
            BranchIndex: int
            Rng: uint64 option
        }

    let private addMessage
        (state: LoopState)
        (measure: int)
        (channel: string)
        (payload: string)
        (lineNo: int)
        : Result<LoopState, UrcError> =
        result {
            if channel = "02" then
                let! rate = toFloat payload lineNo "measure length"

                if rate <= 0.0 then
                    return! Error(UrcError.syntax lineNo $"non-positive measure length: {payload}")

                return
                    { state with
                        Chart =
                            { state.Chart with
                                Rates = Map.add measure rate state.Chart.Rates
                            }
                    }
            else
                if payload.Length % 2 <> 0 || not (payload |> Seq.forall isIdChar) then
                    return! Error(UrcError.syntax lineNo $"malformed object list: \"{payload}\"")

                if payload.Length = 0 || not (isRelevantChannel channel) then
                    return state
                else
                    let normalized =
                        if channel = "03" || state.Chart.Base = 36 then
                            payload.ToUpperInvariant()
                        else
                            payload

                    let ids = [ for i in 0 .. 2 .. normalized.Length - 2 -> normalized.Substring(i, 2) ]

                    let measureMap =
                        Map.tryFind measure state.MeasureChunks |> Option.defaultValue Map.empty

                    let revChunks = Map.tryFind channel measureMap |> Option.defaultValue []
                    let updatedMeasureMap = Map.add channel (ids :: revChunks) measureMap

                    return
                        { state with
                            MeasureChunks = Map.add measure updatedMeasureMap state.MeasureChunks
                        }
        }

    let private processLine
        (branches: uint64[] option)
        (state: LoopState)
        (rawLine: string)
        (lineNo: int)
        : Result<LoopState, UrcError> =
        let line = rawLine.Trim()

        if not (line.StartsWith("#", StringComparison.Ordinal)) then
            Ok state
        else
            let head = line.Substring(1)

            if
                head.Length >= 6
                && isAsciiDigit head[0]
                && isAsciiDigit head[1]
                && isAsciiDigit head[2]
                && isIdChar head[3]
                && isIdChar head[4]
                && head[5] = ':'
            then
                if not (state.Frames |> List.exists fst) then
                    match Int32.TryParse(head.Substring(0, 3), NumberStyles.None, CultureInfo.InvariantCulture) with
                    | true, measure ->
                        let channel = head.Substring(3, 2).ToUpperInvariant()
                        let payload = head.Substring(6)
                        addMessage state measure channel payload lineNo
                    | false, _ -> Error(UrcError.syntax lineNo $"invalid measure in header: {head}")
                else
                    Ok state
            else
                let sepIdx = head.IndexOfAny([| ' '; '\t' |])
                let command = if sepIdx = -1 then head else head.Substring(0, sepIdx)
                let argument = if sepIdx = -1 then "" else head.Substring(sepIdx + 1).Trim()
                let word = command.ToUpperInvariant()

                result {
                    match word with
                    | "RANDOM" ->
                        let! count = toInt64 argument lineNo "#RANDOM"

                        if count < 1L then
                            return! Error(UrcError.syntax lineNo $"#RANDOM count must be >= 1: {count}")

                        let! pick, nextRng =
                            match branches with
                            | Some picks when state.BranchIndex < picks.Length ->
                                let p = picks[state.BranchIndex]

                                if p < 1UL || p > uint64 count then
                                    Error(UrcError.syntax lineNo $"branch pick out of range: {p}")
                                else
                                    Ok(int64 p, state.Rng)
                            | _ ->
                                match state.Rng with
                                | Some r ->
                                    let v, nextR = JavaRandom.nextInt r (uint64 count)
                                    Ok(int64 (v + 1UL), Some nextR)
                                | None -> Ok(1L, None)

                        return
                            { state with
                                RandomValue = Some pick
                                BranchIndex = state.BranchIndex + 1
                                Rng = nextRng
                            }
                    | "IF" ->
                        let! v =
                            state.RandomValue
                            |> Result.requireSome (UrcError.syntax lineNo "unmatched #IF")

                        let! cond = toInt64 argument lineNo "#IF"
                        let frames = (v <> cond, v = cond) :: state.Frames
                        return { state with Frames = frames }
                    | "ELSEIF" ->
                        match state.Frames with
                        | [] -> return! Error(UrcError.syntax lineNo "unmatched #ELSEIF")
                        | (_, matched) :: rest ->
                            let! cond = toInt64 argument lineNo "#ELSEIF"

                            let! v =
                                state.RandomValue
                                |> Result.requireSome (UrcError.syntax lineNo "unmatched #ELSEIF")

                            if matched then
                                return { state with Frames = (true, true) :: rest }
                            else
                                let now = v = cond
                                return { state with Frames = (not now, now) :: rest }
                    | "ELSE" ->
                        match state.Frames with
                        | [] -> return! Error(UrcError.syntax lineNo "unmatched #ELSE")
                        | (_, matched) :: rest -> return { state with Frames = (matched, true) :: rest }
                    | "ENDIF" ->
                        match state.Frames with
                        | [] -> return! Error(UrcError.syntax lineNo "unmatched #ENDIF")
                        | _ :: rest -> return { state with Frames = rest }
                    | "SETRANDOM"
                    | "ENDRANDOM"
                    | "SWITCH"
                    | "CASE"
                    | "SKIP"
                    | "DEF"
                    | "ENDSW"
                    | "SETSWITCH" ->
                        return! Error(UrcError.unsupportedVersion lineNo $"unsupported BMS command: #{command}")
                    | _ when state.Frames |> List.exists fst -> return state
                    | "BPM" ->
                        let! bpm = toFloat argument lineNo "#BPM"
                        return { state with Chart = { state.Chart with Bpm = Some bpm } }
                    | _ when
                        (command.Length = 5 && command.StartsWith("BPM", StringComparison.OrdinalIgnoreCase))
                        || (command.Length = 7 && command.StartsWith("EXBPM", StringComparison.OrdinalIgnoreCase))
                        ->
                        let! bpm = toFloat argument lineNo command
                        let id = normId (command.Substring(command.Length - 2)) state.Chart.Base

                        let chart =
                            { state.Chart with
                                BpmDefs = Map.add id bpm state.Chart.BpmDefs
                            }

                        return { state with Chart = chart }
                    | _ when command.Length = 6 && command.StartsWith("STOP", StringComparison.OrdinalIgnoreCase) ->
                        let! stop = toFloat argument lineNo command
                        let id = normId (command.Substring(command.Length - 2)) state.Chart.Base

                        let chart =
                            { state.Chart with
                                StopDefs = Map.add id (abs stop / 192.0) state.Chart.StopDefs
                            }

                        return { state with Chart = chart }
                    | _ when command.Length = 8 && command.StartsWith("SCROLL", StringComparison.OrdinalIgnoreCase) ->
                        let! scroll = toFloat argument lineNo command
                        let id = normId (command.Substring(command.Length - 2)) state.Chart.Base

                        let chart =
                            { state.Chart with
                                ScrollDefs = Map.add id scroll state.Chart.ScrollDefs
                            }

                        return { state with Chart = chart }
                    | "LNTYPE" ->
                        let! lntype = toInt argument lineNo "#LNTYPE"

                        if lntype <> 1 && lntype <> 2 then
                            return! Error(UrcError.syntax lineNo $"unsupported #LNTYPE: {lntype}")

                        return { state with Chart = { state.Chart with LnType = lntype } }
                    | "LNOBJ" ->
                        let lnObj =
                            if String.IsNullOrEmpty argument then
                                None
                            else
                                Some(normId argument state.Chart.Base)

                        return { state with Chart = { state.Chart with LnObj = lnObj } }
                    | "TITLE" ->
                        let title = if String.IsNullOrEmpty argument then None else Some argument
                        return { state with Chart = { state.Chart with Title = title } }
                    | "ARTIST" ->
                        let artist = if String.IsNullOrEmpty argument then None else Some argument
                        return { state with Chart = { state.Chart with Artist = artist } }
                    | "PLAYLEVEL" ->
                        let playLevel = if String.IsNullOrEmpty argument then None else Some argument
                        return { state with Chart = { state.Chart with PlayLevel = playLevel } }
                    | _ -> return state
                }

    /// Parses BMS/BME/BML/PMS bytes into its source model.
    let parse (data: byte[]) (options: ParseOptions) : Result<BmsChart, UrcError> =
        result {
            let! text = decode data
            let lines = text.TrimStart('\uFEFF').Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.None)
            let! b = scanBase lines

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
                    MeasureChunks = Map.empty
                    Frames = []
                    RandomValue = None
                    BranchIndex = 0
                    Rng = options.Seed |> Option.map JavaRandom.create
                }

            let branchArray = options.Branches |> Option.map Array.ofList

            let rec step (state: LoopState) (idx: int) : Result<LoopState, UrcError> =
                if idx >= lines.Length then
                    if not (List.isEmpty state.Frames) then
                        Error(UrcError.syntax 1 "unterminated #IF block")
                    else
                        Ok state
                else
                    match processLine branchArray state lines[idx] (idx + 1) with
                    | Ok nextState -> step nextState (idx + 1)
                    | Error err -> Error err

            let! doneState = step initialLoop 0

            let measures =
                doneState.MeasureChunks
                |> Map.map (fun _ channelMap -> channelMap |> Map.map (fun _ revChunks -> List.rev revChunks))

            return { doneState.Chart with Measures = measures }
        }
