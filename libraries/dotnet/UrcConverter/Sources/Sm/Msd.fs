namespace UrcConverter.Sources.Sm

module internal Msd =

    open System.Globalization
    open UrcConverter

    let parseFloat (token: string) : Result<float, UrcError> =
        match System.Double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value -> Ok value
        | false, _ -> Error(UrcError.syntax 1 $"invalid number: {token}")

    let parseBeat (token: string) : Result<float, UrcError> =
        let trimmed = token.TrimEnd()

        if trimmed.EndsWith("r", System.StringComparison.Ordinal)
           || trimmed.EndsWith("R", System.StringComparison.Ordinal) then
            Error(UrcError.syntax 1 $"row-format beats are not supported: {token}")
        else
            parseFloat token

    let parseInt (token: string) : Result<int, UrcError> =
        match System.Int32.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, value -> Ok value
        | false, _ -> Error(UrcError.syntax 1 $"invalid integer: {token}")

    let expressions (value: string) (minimum: int) : Result<string list list, UrcError> =
        let rec loop (parts: string list) (acc: string list list) : Result<string list list, UrcError> =
            match parts with
            | [] -> Ok(List.rev acc)
            | expression :: rest ->
                if expression.Trim().Length = 0 then
                    loop rest acc
                else
                    let fields = List.ofArray (expression.Split('='))

                    if fields.Length < minimum then
                        Error(UrcError.syntax 1 $"malformed timing expression: {expression}")
                    else
                        loop rest (fields :: acc)

        loop (List.ofArray (value.Split(','))) []

    let pairs (value: string) (skipZero: bool) : Result<(float * float) list, UrcError> =
        expressions value 2
        |> Result.bind (fun list ->
            let rec loop (entries: (float * float) list) (rest: string list list) : Result<(float * float) list, UrcError> =
                match rest with
                | [] -> Ok(List.rev entries)
                | parts :: tail when parts.Length <> 2 ->
                    let joined = String.concat "=" parts
                    Error(UrcError.syntax 1 $"malformed timing expression: {joined}")
                | parts :: tail ->
                    match parseBeat parts[0], parseFloat parts[1] with
                    | Error error, _
                    | _, Error error -> Error error
                    | Ok beat, Ok number ->
                        if not skipZero || number <> 0.0 then
                            loop ((beat, number) :: entries) tail
                        else
                            loop entries tail

            loop [] list)

    /// Splits a simfile into MSD values (#TAG:param:...;) following MsdFile.
    let tokenize (text: string) : string list list =
        let values = ResizeArray<string list>()
        let params_ = ResizeArray<string>()
        let current = System.Text.StringBuilder()
        let line = System.Text.StringBuilder()
        let mutable reading = false
        let mutable i = 0
        let n = text.Length

        let endParam () =
            params_.Add(current.ToString())
            current.Clear() |> ignore
            line.Clear() |> ignore

        while i < n do
            if i + 1 < n && text[i] = '/' && text[i + 1] = '/' then
                while i < n && text[i] <> '\n' do
                    i <- i + 1
            elif reading && text[i] = '#' then
                let visible = line.ToString().Trim(' ', '\t')

                if visible.Length > 0 then
                    current.Append('#') |> ignore
                    line.Append('#') |> ignore
                    i <- i + 1
                else
                    params_.Add(current.ToString().TrimEnd(' ', '\t', '\r', '\n'))
                    values.Add(List.ofSeq params_)
                    params_.Clear() |> ignore
                    current.Clear() |> ignore
                    line.Clear() |> ignore
                    reading <- false
            elif not reading then
                if text[i] = '#' then
                    reading <- true
                    line.Clear() |> ignore
                    i <- i + 1
                elif text[i] <> '\\' then
                    i <- i + 1
                elif i + 1 < n then
                    i <- i + 2
                else
                    i <- i + 1
            else
                if text[i] = ':' then
                    endParam ()
                elif text[i] = ';' then
                    endParam ()
                    values.Add(List.ofSeq params_)
                    params_.Clear() |> ignore
                    current.Clear() |> ignore
                    line.Clear() |> ignore
                    reading <- false
                elif text[i] = '\\' then
                    i <- i + 1

                    if i < n then
                        current.Append text[i] |> ignore
                        line.Append text[i] |> ignore
                else
                    current.Append text[i] |> ignore
                    line.Append text[i] |> ignore

                if i < n && (text[i] = '\r' || text[i] = '\n') then
                    line.Clear() |> ignore

                i <- i + 1

        if reading then
            params_.Add(current.ToString())

        List.ofSeq values
