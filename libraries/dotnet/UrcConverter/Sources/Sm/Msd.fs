namespace UrcConverter.Sources.Sm

module internal Msd =

    open System
    open System.Globalization
    open System.Text
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

    /// Splits a simfile into MSD values (#TAG:param:...;) following MsdFile.
    let tokenize (text: string) : string list list =
        let n = text.Length
        let values = ResizeArray<string list>()
        let mutable revParams: string list = []
        let current = StringBuilder()
        let mutable lineHasVisible = false
        let mutable reading = false
        let mutable i = 0

        while i < n do
            let ch = text[i]

            if ch = '/' && i + 1 < n && text[i + 1] = '/' then
                while i < n && text[i] <> '\n' && text[i] <> '\r' do
                    i <- i + 1
            elif reading && ch = '#' then
                if lineHasVisible then
                    current.Append('#') |> ignore
                    i <- i + 1
                else
                    let trimmedParam = current.ToString().TrimEnd(' ', '\t', '\r', '\n')
                    values.Add(List.rev (trimmedParam :: revParams))
                    revParams <- []
                    current.Clear() |> ignore
                    lineHasVisible <- false
                    reading <- false
            elif not reading then
                if ch = '#' then
                    reading <- true
                    lineHasVisible <- false
                    i <- i + 1
                elif ch = '\\' && i + 1 < n then
                    i <- i + 2
                else
                    i <- i + 1
            elif ch = ':' then
                revParams <- current.ToString() :: revParams
                current.Clear() |> ignore
                lineHasVisible <- false
                i <- i + 1
            elif ch = ';' then
                values.Add(List.rev (current.ToString() :: revParams))
                revParams <- []
                current.Clear() |> ignore
                lineHasVisible <- false
                reading <- false
                i <- i + 1
            elif ch = '\\' then
                i <- i + 1

                if i < n then
                    let escaped = text[i]
                    current.Append(escaped) |> ignore

                    if escaped = '\r' || escaped = '\n' then
                        lineHasVisible <- false
                    elif escaped <> ' ' && escaped <> '\t' then
                        lineHasVisible <- true

                    i <- i + 1
            else
                let start = i
                let mutable scanning = true

                while i < n && scanning do
                    let c = text[i]

                    if
                        c = '#'
                        || c = ':'
                        || c = ';'
                        || c = '\\'
                        || (c = '/' && i + 1 < n && text[i + 1] = '/')
                    then
                        scanning <- false
                    else
                        if c = '\r' || c = '\n' then
                            lineHasVisible <- false
                        elif c <> ' ' && c <> '\t' then
                            lineHasVisible <- true

                        i <- i + 1

                current.Append(text, start, i - start) |> ignore

        if reading then
            values.Add(List.rev (current.ToString() :: revParams))

        Seq.toList values
