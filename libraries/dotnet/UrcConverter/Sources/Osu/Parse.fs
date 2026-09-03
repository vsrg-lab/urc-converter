namespace UrcConverter.Sources.Osu

module Parse =

    open System
    open System.Globalization
    open FParsec
    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Osu.Model

    type private State = { Beatmap: Beatmap; Section: string }

    let private pNumber: Parser<float, State> =
        let invariant (token: string) =
            Double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture)

        pipe2
            (opt (pchar '-'))
            (many1 digit .>>. opt (pchar '.' >>. many1 digit))
            (fun sign (whole, fraction) ->
                let magnitude =
                    match fraction with
                    | None -> invariant (String(Array.ofList whole))
                    | Some digits ->
                        invariant $"{String(Array.ofList whole)}.{String(Array.ofList digits)}"

                if sign.IsSome then -magnitude else magnitude)

    let private pNumberW: Parser<float, State> =
        many (pchar ' ') >>. pNumber .>> many (pchar ' ')

    let private lineText: Parser<string, State> =
        manySatisfy (fun c -> c <> '\r' && c <> '\n')

    let private numberField
        (label: string)
        (value: string)
        (update: Beatmap -> float -> Beatmap)
        : Parser<unit, State> =
        match runParserOnString (pNumber .>> eof) Unchecked.defaultof<State> "" value with
        | Success(number, _, _) ->
            updateUserState (fun state ->
                { state with
                    Beatmap = update state.Beatmap number
                })
        | Failure _ -> fail $"invalid {label}: '{value}'"

    let private pKeyValue: Parser<string * string, State> =
        pipe2
            (many1Satisfy (fun c -> c <> ':' && c <> '\r' && c <> '\n') .>> pchar ':')
            lineText
            (fun key value -> key.Trim(), value.Trim())

    let private applyKeyValue ((key, value): string * string) : Parser<unit, State> =
        match key with
        | "Mode" ->
            numberField "Mode" value (fun beatmap number ->
                { beatmap with Mode = Shared.roundMs number })
        | "CircleSize" ->
            numberField "CircleSize" value (fun beatmap number ->
                { beatmap with
                    CircleSize = Some number
                })
        | "OverallDifficulty" ->
            numberField "OverallDifficulty" value (fun beatmap number ->
                { beatmap with
                    OverallDifficulty = Some number
                })
        | "Title"
        | "TitleUnicode"
        | "Artist"
        | "ArtistUnicode"
        | "Creator"
        | "Version" ->
            updateUserState (fun state ->
                let beatmap = state.Beatmap

                let beatmap =
                    match key with
                    | "Title" -> { beatmap with Title = Some value }
                    | "TitleUnicode" ->
                        { beatmap with
                            TitleUnicode = Some value
                        }
                    | "Artist" -> { beatmap with Artist = Some value }
                    | "ArtistUnicode" ->
                        { beatmap with
                            ArtistUnicode = Some value
                        }
                    | "Creator" -> { beatmap with Creator = Some value }
                    | _ -> { beatmap with Version = Some value }

                { state with Beatmap = beatmap })
        | _ -> preturn ()

    let private pTimingBody: Parser<unit, State> =
        sepBy pNumberW (pchar ',')
        >>= fun fields ->
            match fields with
            | [] -> preturn ()
            | [ _ ] -> fail "timing point needs at least 2 fields"
            | time :: beatLength :: _ ->
                let meter = List.tryItem 2 fields |> Option.map Shared.roundMs |> Option.defaultValue 4

                let uninherited =
                    List.tryItem 6 fields
                    |> Option.map (fun value -> Shared.roundMs value <> 0)
                    |> Option.defaultValue true

                updateUserState (fun state ->
                    let point =
                        {
                            Time = Shared.roundMs time
                            BeatLength = beatLength
                            Meter = meter
                            Uninherited = uninherited
                        }

                    { state with
                        Beatmap =
                            { state.Beatmap with
                                TimingPoints = point :: state.Beatmap.TimingPoints
                            }
                    })

    let private pHitObjectBody: Parser<unit, State> =
        pipe4
            (pNumberW .>> pchar ',')
            (pNumberW .>> pchar ',')
            (pNumberW .>> pchar ',')
            pNumberW
            (fun x _ time typeValue ->
                Shared.roundMs x, Shared.roundMs time, Shared.roundMs typeValue)
        >>= fun (x, time, typeBits) ->
            let isHold = typeBits &&& 128 <> 0

            if not isHold && typeBits &&& 1 = 0 then
                fail $"unsupported hit object type: {typeBits}"
            else
                let endTimeParser =
                    if isHold then
                        (pchar ',' >>. pNumberW >>. pchar ',' >>. pNumberW)
                        |>> fun endValue -> Some(Shared.roundMs endValue)
                    else
                        preturn None

                endTimeParser
                >>= fun endValue ->
                    updateUserState (fun state ->
                        let hitObject =
                            {
                                X = x
                                Time = time
                                IsHold = isHold
                                EndTime = endValue
                            }

                        { state with
                            Beatmap =
                                { state.Beatmap with
                                    HitObjects = hitObject :: state.Beatmap.HitObjects
                                }
                        })

    let private pSectionHeader: Parser<unit, State> =
        pchar '[' >>. manySatisfy (fun c -> c <> ']') .>> pchar ']'
        >>= fun name -> updateUserState (fun state -> { state with Section = name.Trim() })

    let private pLine: Parser<unit, State> =
        getUserState
        >>= fun state ->
            let body =
                match state.Section with
                | "General"
                | "Metadata"
                | "Difficulty" ->
                    // A line without a colon is skipped, matching the original
                    // parser; a bad value in a known field still fails because
                    // `attempt` only wraps the key-value shape.
                    (attempt pKeyValue >>= applyKeyValue) <|> preturn ()
                | "TimingPoints" -> pTimingBody
                | "HitObjects" -> pHitObjectBody
                | _ -> preturn ()

            many (pchar ' ')
            >>. choice [ attempt pSectionHeader; attempt (pstring "//") |>> ignore; body ]
            .>> (lineText |>> ignore)
            .>> newline

    let private pOsuFile: Parser<Beatmap, State> =
        many pLine .>> eof >>. getUserState
        |>> fun state ->
            let beatmap = state.Beatmap

            { beatmap with
                TimingPoints = List.rev beatmap.TimingPoints
                HitObjects = List.rev beatmap.HitObjects
            }

    let parse (text: string) : Result<Beatmap, UrcError> =
        let stripped =
            if text.StartsWith("\uFEFF", StringComparison.Ordinal) then
                text[1..]
            else
                text

        let normalized =
            if stripped.EndsWith("\n") then
                stripped
            else
                stripped + "\n"

        let initial =
            {
                Beatmap =
                    {
                        Mode = 3
                        Title = None
                        TitleUnicode = None
                        Artist = None
                        ArtistUnicode = None
                        Creator = None
                        Version = None
                        CircleSize = None
                        OverallDifficulty = None
                        TimingPoints = []
                        HitObjects = []
                    }
                Section = ""
            }

        match runParserOnString pOsuFile initial "" normalized with
        | Success(beatmap, _, _) -> Result.Ok beatmap
        | Failure(_, parseError, _) ->
            Result.Error(
                UrcError.Syntax(int parseError.Position.Line + 1, $"invalid .osu: {parseError}")
            )
