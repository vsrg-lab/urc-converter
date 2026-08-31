namespace UrcConverter.Sources

open System
open System.Globalization
open UrcConverter

module Osu =

    open FParsec
    open FsToolkit.ErrorHandling

    let private keyMin, keyMax = 1, 18
    let private judgmentRates = [ 100.0; 100.0; 66.67; 33.33; 16.67; 0.0 ]

    type TimingPoint =
        {
            Time: int
            BeatLength: float
            Meter: int
            Uninherited: bool
        }

    type HitObject =
        {
            X: int
            Time: int
            IsHold: bool
            EndTime: int option
        }

    type Beatmap =
        {
            Mode: int
            Title: string option
            TitleUnicode: string option
            Artist: string option
            ArtistUnicode: string option
            Creator: string option
            Version: string option
            CircleSize: float option
            OverallDifficulty: float option
            TimingPoints: TimingPoint list
            HitObjects: HitObject list
        }

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

    let convert (beatmap: Beatmap) : Result<Chart, UrcError> =
        result {
            if beatmap.Mode <> 3 then
                return!
                    Result.Error(
                        UrcError.UnsupportedVersion(1, $"unsupported game mode: {beatmap.Mode}")
                    )

            match beatmap.CircleSize with
            | None -> return! Result.Error(UrcError.Syntax(1, "missing CircleSize"))
            | Some circleSize when circleSize <> truncate circleSize ->
                return!
                    Result.Error(UrcError.Syntax(1, $"CircleSize must be an integer: {circleSize}"))
            | Some circleSize ->
                let keys = int circleSize

                if keys < keyMin || keys > keyMax then
                    return! Result.Error(UrcError.Syntax(1, $"CircleSize out of range: {keys}"))

                if beatmap.TimingPoints |> List.exists (fun point -> point.BeatLength = 0.0) then
                    return! Result.Error(UrcError.Syntax(1, "timing point with zero beat length"))

                let firstNoteTime =
                    match beatmap.HitObjects with
                    | [] -> 0
                    | objects -> objects |> List.map _.Time |> List.min

                let! timing =
                    Shared.buildTiming
                        (beatmap.TimingPoints
                         |> List.choose (fun point ->
                             if point.Uninherited then
                                 Some(point.Time, 60000.0 / point.BeatLength, point.Meter)
                             else
                                 None))
                        (beatmap.TimingPoints
                         |> List.choose (fun point ->
                             if not point.Uninherited then
                                 Some(point.Time, -100.0 / point.BeatLength)
                             else
                                 None))
                        firstNoteTime
                        ".osu"

                let! notes =
                    beatmap.HitObjects
                    |> List.traverseResultM (fun obj ->
                        let lane = min (max (obj.X * keys / 512) 0) (keys - 1)

                        match obj.IsHold, obj.EndTime with
                        | true, Some endTime when endTime < obj.Time ->
                            Result.Error(
                                UrcError.Syntax(
                                    1,
                                    $"hold ends before it starts: {endTime} < {obj.Time}"
                                )
                            )
                        | true, Some endTime ->
                            Result.Ok
                                [
                                    {
                                        TimestampMs = obj.Time - firstNoteTime
                                        Lane = lane
                                        Type = NoteType.LS
                                    }
                                    {
                                        TimestampMs = endTime - firstNoteTime
                                        Lane = lane
                                        Type = NoteType.LE
                                    }
                                ]
                        | true, None -> invalidOp "hold hit object must have an end time"
                        | false, _ ->
                            Result.Ok
                                [
                                    {
                                        TimestampMs = obj.Time - firstNoteTime
                                        Lane = lane
                                        Type = NoteType.N
                                    }
                                ])
                    |> Result.map List.concat

                do! Shared.checkHoldOverlap notes

                let judgment =
                    beatmap.OverallDifficulty
                    |> Option.map (fun od ->
                        let windows =
                            16.5
                            :: [
                                for baseline in [ 64.0; 97.0; 127.0; 151.0; 188.0 ] ->
                                    baseline - 3.0 * od + 0.5
                            ]

                        {
                            Windows = windows
                            Rates = judgmentRates
                        })

                let title = beatmap.TitleUnicode |> Option.orElse beatmap.Title
                let artist = beatmap.ArtistUnicode |> Option.orElse beatmap.Artist

                let metadata =
                    [
                        ("Title", title)
                        ("Artist", artist)
                        ("Creator", beatmap.Creator)
                        ("Version", beatmap.Version)
                    ]

                let missing =
                    metadata
                    |> List.choose (fun (name, value) ->
                        match value with
                        | Some value when value <> "" -> None
                        | _ -> Some name)

                let missingText = missing |> String.concat ", "

                if missing <> [] then
                    return! Result.Error(UrcError.Syntax(1, $"missing metadata: {missingText}"))

                match title, artist, beatmap.Creator, beatmap.Version with
                | Some title, Some artist, Some creator, Some version ->
                    return
                        {
                            FormatVersion = { Major = 1; Minor = 1 }
                            Metadata =
                                {
                                    Original = "osu!mania"
                                    Title = title
                                    Artist = artist
                                    Creator = creator
                                    Version = version
                                }
                            Judgment = judgment
                            Layout =
                                {
                                    Keys = keys
                                    SpecialKeys = 0
                                    SpecialLanes = None
                                }
                            TimingPoints = timing
                            Notes = notes
                        }
                | _ ->
                    return!
                        Result.Error(UrcError.Syntax(1, $"missing metadata: {missingText}"))
        }
