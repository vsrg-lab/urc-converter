namespace UrcConverter.Sources.Osu

[<RequireQualifiedAccess>]
module Parse =

    open System
    open System.Globalization
    open System.IO
    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Osu.Model

    [<RequireQualifiedAccess>]
    type private Section =
        | None
        | General
        | Metadata
        | Difficulty
        | TimingPoints
        | HitObjects
        | Other

    let private parseSection (name: string) =
        match name.Trim() with
        | "General" -> Section.General
        | "Metadata" -> Section.Metadata
        | "Difficulty" -> Section.Difficulty
        | "TimingPoints" -> Section.TimingPoints
        | "HitObjects" -> Section.HitObjects
        | _ -> Section.Other

    let private (|Skip|SectionHeader|Content|) (line: string) =
        if line.Length = 0 || line.StartsWith("//", StringComparison.Ordinal) then
            Skip
        elif line.Length >= 2 && line[0] = '[' && line[line.Length - 1] = ']' then
            SectionHeader(line[1 .. line.Length - 2])
        else
            Content line

    let private (|KeyValue|_|) (line: string) =
        match line.Split(':', 2) with
        | [| key; value |] -> Some(key.Trim(), value.Trim())
        | _ -> None

    let private parseFloat (token: string) (lineNo: int) (label: string) : Result<float, UrcError> =
        match Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value when Double.IsFinite value -> Ok value
        | _ -> Error(UrcError.syntax lineNo $"invalid {label}: '{token}'")

    let private parseRoundedInt (token: string) (lineNo: int) (label: string) : Result<int, UrcError> =
        parseFloat token lineNo label |> Result.map Shared.roundMs

    let private parseGeneral (beatmap: Beatmap) (line: string) (lineNo: int) : Result<Beatmap, UrcError> =
        match line with
        | KeyValue("Mode", value) ->
            parseRoundedInt value lineNo "Mode"
            |> Result.map (fun mode -> { beatmap with Mode = mode })
        | _ -> Ok beatmap

    let private parseMetadata (beatmap: Beatmap) (line: string) : Result<Beatmap, UrcError> =
        match line with
        | KeyValue("Title", value) -> Ok { beatmap with Title = Some value }
        | KeyValue("TitleUnicode", value) -> Ok { beatmap with TitleUnicode = Some value }
        | KeyValue("Artist", value) -> Ok { beatmap with Artist = Some value }
        | KeyValue("ArtistUnicode", value) -> Ok { beatmap with ArtistUnicode = Some value }
        | KeyValue("Creator", value) -> Ok { beatmap with Creator = Some value }
        | KeyValue("Version", value) -> Ok { beatmap with Version = Some value }
        | _ -> Ok beatmap

    let private parseDifficulty (beatmap: Beatmap) (line: string) (lineNo: int) : Result<Beatmap, UrcError> =
        match line with
        | KeyValue("CircleSize", value) ->
            parseFloat value lineNo "CircleSize"
            |> Result.map (fun cs -> { beatmap with CircleSize = Some cs })
        | KeyValue("OverallDifficulty", value) ->
            parseFloat value lineNo "OverallDifficulty"
            |> Result.map (fun od -> { beatmap with OverallDifficulty = Some od })
        | _ -> Ok beatmap

    let private parseTimingPoint (line: string) (lineNo: int) : Result<TimingPoint, UrcError> =
        let fields = line.Split(',') |> Array.map _.Trim()

        if fields.Length < 2 then
            Error(UrcError.syntax lineNo $"timing point needs at least 2 fields: '{line}'")
        else
            result {
                let! time = parseRoundedInt fields[0] lineNo "timing time"
                let! beatLength = parseFloat fields[1] lineNo "beat length"

                let! meter =
                    if fields.Length > 2 && fields[2] <> "" then
                        parseRoundedInt fields[2] lineNo "meter"
                    else
                        Ok 4

                let! uninherited =
                    if fields.Length > 6 && fields[6] <> "" then
                        parseRoundedInt fields[6] lineNo "uninherited" |> Result.map (fun v -> v <> 0)
                    else
                        Ok true

                return
                    {
                        Time = time
                        BeatLength = beatLength
                        Meter = meter
                        Uninherited = uninherited
                    }
            }

    let private parseHitObject (line: string) (lineNo: int) : Result<HitObject, UrcError> =
        let fields = line.Split(',') |> Array.map _.Trim()

        if fields.Length < 5 then
            Error(UrcError.syntax lineNo $"hit object needs at least 5 fields: '{line}'")
        else
            result {
                let! x = parseRoundedInt fields[0] lineNo "hit object x"
                let! time = parseRoundedInt fields[2] lineNo "hit object time"
                let! typeBits = parseRoundedInt fields[3] lineNo "hit object type"

                let isHold = typeBits &&& 128 <> 0

                if not isHold && typeBits &&& 1 = 0 then
                    return! Error(UrcError.syntax lineNo $"unsupported hit object type: {typeBits}")

                let! kind =
                    if isHold then
                        if fields.Length < 6 then
                            Error(UrcError.syntax lineNo $"hold note needs an end time: '{line}'")
                        else
                            let endParts = fields[5].Split(':', 2)
                            let endToken = endParts[0].Trim()
                            parseRoundedInt endToken lineNo "hold end time" |> Result.map HitObjectKind.Hold
                    else
                        Ok HitObjectKind.Tap

                return
                    {
                        X = x
                        Time = time
                        Kind = kind
                    }
            }

    /// Parses .osu text into its source model.
    let parse (text: string) : Result<Beatmap, UrcError> =
        let stripped = text.TrimStart('\uFEFF')
        use reader = new StringReader(stripped)

        let rec loop (lineNo: int) (section: Section) (beatmap: Beatmap) : Result<Beatmap, UrcError> =
            let raw = reader.ReadLine()

            if isNull raw then
                Ok
                    { beatmap with
                        TimingPoints = List.rev beatmap.TimingPoints
                        HitObjects = List.rev beatmap.HitObjects
                    }
            else
                match raw.Trim() with
                | Skip -> loop (lineNo + 1) section beatmap
                | SectionHeader name -> loop (lineNo + 1) (parseSection name) beatmap
                | Content line ->
                    let next =
                        match section with
                        | Section.General -> parseGeneral beatmap line lineNo
                        | Section.Metadata -> parseMetadata beatmap line
                        | Section.Difficulty -> parseDifficulty beatmap line lineNo
                        | Section.TimingPoints ->
                            parseTimingPoint line lineNo
                            |> Result.map (fun point ->
                                { beatmap with
                                    TimingPoints = point :: beatmap.TimingPoints
                                })
                        | Section.HitObjects ->
                            parseHitObject line lineNo
                            |> Result.map (fun hit ->
                                { beatmap with
                                    HitObjects = hit :: beatmap.HitObjects
                                })
                        | Section.None
                        | Section.Other -> Ok beatmap

                    match next with
                    | Ok updated -> loop (lineNo + 1) section updated
                    | Error err -> Error err

        loop 1 Section.None Beatmap.Empty
