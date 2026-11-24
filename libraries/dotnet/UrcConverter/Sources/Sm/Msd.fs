namespace UrcConverter.Sources.Sm

module internal Msd =

    open System
    open System.Globalization
    open FsToolkit.ErrorHandling
    open UrcConverter

    let parseFloat (token: string) : Result<float, UrcError> =
        match Double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value when Double.IsFinite value -> Ok value
        | _ -> Error(UrcError.syntax 1 $"invalid number: {token}")

    let parseBeat (token: string) : Result<float, UrcError> =
        let trimmed = token.TrimEnd()

        if
            trimmed.EndsWith("r", StringComparison.Ordinal)
            || trimmed.EndsWith("R", StringComparison.Ordinal)
        then
            Error(UrcError.syntax 1 $"row-format beats are not supported: {token}")
        else
            parseFloat token

    let parseInt (token: string) : Result<int, UrcError> =
        match Int32.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, value -> Ok value
        | false, _ -> Error(UrcError.syntax 1 $"invalid integer: {token}")

    let expressions (value: string) (minimum: int) : Result<string list list, UrcError> =
        value.Split(',')
        |> Array.toList
        |> List.filter (fun expr -> expr.Trim().Length > 0)
        |> List.traverseResultM (fun expr ->
            let fields = List.ofArray (expr.Split('='))

            if fields.Length < minimum then
                Error(UrcError.syntax 1 $"malformed timing expression: {expr}")
            else
                Ok fields)

    let pairs (value: string) (skipZero: bool) : Result<(float * float) list, UrcError> =
        expressions value 2
        |> Result.bind (
            List.traverseResultM (fun parts ->
                match parts with
                | [ beatStr; valueStr ] ->
                    result {
                        let! beat = parseBeat beatStr
                        let! number = parseFloat valueStr
                        return if not skipZero || number <> 0.0 then Some(beat, number) else None
                    }
                | _ ->
                    let joined = String.concat "=" parts
                    Error(UrcError.syntax 1 $"malformed timing expression: {joined}"))
        )
        |> Result.map (List.choose id)

    let private charsToString (revChars: char list) : string =
        let arr = revChars |> List.rev |> Array.ofList
        String(arr)

    let private hasVisibleChars (revLine: char list) : bool =
        revLine |> List.exists (fun c -> c <> ' ' && c <> '\t')

    /// Splits a simfile into MSD values (#TAG:param:...;) following MsdFile.
    let tokenize (text: string) : string list list =
        let n = text.Length

        let rec skipComment (i: int) =
            if i < n && text[i] <> '\n' && text[i] <> '\r' then skipComment (i + 1) else i

        let rec loop
            (i: int)
            (reading: bool)
            (revCurrent: char list)
            (revLine: char list)
            (revParams: string list)
            (revValues: string list list)
            : string list list =
            if i >= n then
                if reading then
                    let completed = List.rev (charsToString revCurrent :: revParams)
                    List.rev (completed :: revValues)
                else
                    List.rev revValues
            elif i + 1 < n && text[i] = '/' && text[i + 1] = '/' then
                loop (skipComment i) reading revCurrent revLine revParams revValues
            elif reading && text[i] = '#' then
                if hasVisibleChars revLine then
                    loop (i + 1) true ('#' :: revCurrent) ('#' :: revLine) revParams revValues
                else
                    let trimmedParam = (charsToString revCurrent).TrimEnd(' ', '\t', '\r', '\n')
                    let completed = List.rev (trimmedParam :: revParams)
                    loop i false [] [] [] (completed :: revValues)
            elif not reading then
                if text[i] = '#' then
                    loop (i + 1) true [] [] [] revValues
                elif text[i] <> '\\' then
                    loop (i + 1) false revCurrent revLine revParams revValues
                elif i + 1 < n then
                    loop (i + 2) false revCurrent revLine revParams revValues
                else
                    loop (i + 1) false revCurrent revLine revParams revValues
            else
                let c = text[i]

                if c = ':' then
                    let nextParams = charsToString revCurrent :: revParams
                    loop (i + 1) true [] [] nextParams revValues
                elif c = ';' then
                    let completed = List.rev (charsToString revCurrent :: revParams)
                    loop (i + 1) false [] [] [] (completed :: revValues)
                elif c = '\\' then
                    let nextIdx = i + 1

                    if nextIdx < n then
                        let escaped = text[nextIdx]
                        let nextLine = if escaped = '\r' || escaped = '\n' then [] else escaped :: revLine
                        loop (nextIdx + 1) true (escaped :: revCurrent) nextLine revParams revValues
                    else
                        loop (nextIdx + 1) true revCurrent revLine revParams revValues
                else
                    let nextLine = if c = '\r' || c = '\n' then [] else c :: revLine
                    loop (i + 1) true (c :: revCurrent) nextLine revParams revValues

        loop 0 false [] [] [] []
