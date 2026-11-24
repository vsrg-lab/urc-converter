namespace UrcConverter

module Writer =

    open System
    open System.Globalization

    let private floatText (value: float) =
        if value = 0.0 then
            "0"
        else
            let text = value.ToString(CultureInfo.InvariantCulture)
            let eIdx = text.IndexOfAny([| 'e'; 'E' |])

            if eIdx = -1 then
                text
            else
                let neg = text.StartsWith('-')
                let mantissaStart = if neg then 1 else 0
                let mantissa = text.Substring(mantissaStart, eIdx - mantissaStart)
                let exp = Int32.Parse(text.Substring(eIdx + 1), CultureInfo.InvariantCulture)
                let dotIdx = mantissa.IndexOf('.')

                let digits =
                    if dotIdx = -1 then
                        mantissa
                    else
                        mantissa.Substring(0, dotIdx) + mantissa.Substring(dotIdx + 1)

                let fracLen = if dotIdx = -1 then 0 else mantissa.Length - dotIdx - 1
                let point = digits.Length - fracLen + exp

                let body =
                    if point <= 0 then
                        "0." + String('0', -point) + digits
                    elif point >= digits.Length then
                        digits + String('0', point - digits.Length)
                    else
                        digits.Substring(0, point) + "." + digits.Substring(point)

                if neg then "-" + body else body

    let private joined (values: float list) =
        values |> List.map floatText |> String.concat ", "

    let write (chart: Chart) : string =
        let layoutLines =
            let typeText =
                if chart.Layout.SpecialKeys > 0 then
                    $"{chart.Layout.Keys}+{chart.Layout.SpecialKeys}"
                else
                    string chart.Layout.Keys

            let specialText =
                match chart.Layout.SpecialLanes with
                | None -> Strings.specialNone
                | Some lanes -> lanes |> List.map string |> String.concat ", "

            [
                Strings.sectionLayout
                $"{Strings.layoutFieldType}: {typeText}"
                $"{Strings.layoutFieldSpecial}: {specialText}"
            ]

        let metadataValues =
            [
                chart.Metadata.Original
                chart.Metadata.Title
                chart.Metadata.Artist
                chart.Metadata.Creator
                chart.Metadata.Version
            ]

        let metadataLines =
            Strings.sectionMetadata
            :: (List.zip Strings.metadataFields metadataValues
                |> List.map (fun (name, value) -> $"{name}: {value}"))

        let judgmentLines =
            match chart.Judgment with
            | Some judgment ->
                [
                    Strings.sectionJudgment
                    $"{Strings.judgmentFieldWindow}: {joined judgment.Windows}"
                    $"{Strings.judgmentFieldRate}: {joined judgment.Rates}"
                ]
            | None -> []

        let timingLines =
            chart.TimingPoints
            |> List.map (fun point ->
                let fields =
                    [
                        string point.TimestampMs
                        floatText point.Bpm
                        $"{point.Meter.Beats}/{point.Meter.NoteValue}"
                    ]
                    @ (point.Multiplier |> Option.toList |> List.map floatText)

                fields |> String.concat ", ")

        let noteLines =
            chart.Notes
            |> List.indexed
            |> List.sortBy (fun (index, note) -> note.TimestampMs, note.Lane, index)
            |> List.map (fun (_, note) -> $"{note.TimestampMs}, {note.Lane}, {note.Type.Token}")

        [
            [ $"@URC {chart.FormatVersion.Major}.{chart.FormatVersion.Minor}" ]
            metadataLines
            judgmentLines
            layoutLines
            Strings.sectionTiming :: timingLines
            Strings.sectionNotes :: noteLines
        ]
        |> List.filter (not << List.isEmpty)
        |> List.map (String.concat "\n")
        |> String.concat "\n\n"
        |> sprintf "%s\n"
