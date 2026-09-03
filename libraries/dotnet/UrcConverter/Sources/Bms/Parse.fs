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
