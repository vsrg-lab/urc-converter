namespace UrcConverter

module Writer =

    open System
    open System.Globalization

    let private floatText (value: float) =
        value.ToString(CultureInfo.InvariantCulture)

    let private joined (values: float list) =
        String.Join(", ", List.map floatText values)

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
                | Some lanes -> String.Join(", ", List.map string lanes)

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
                    @ (match point.Multiplier with
                       | Some multiplier -> [ floatText multiplier ]
                       | None -> [])

                String.Join(", ", fields))

        let noteLines =
            chart.Notes
            |> List.sortBy (fun note -> note.TimestampMs, note.Lane)
            |> List.map (fun note -> $"{note.TimestampMs}, {note.Lane}, {note.Type.Token}")

        [
            [ $"@URC {chart.FormatVersion.Major}.{chart.FormatVersion.Minor}" ]
            metadataLines
            judgmentLines
            layoutLines
            Strings.sectionTiming :: timingLines
            Strings.sectionNotes :: noteLines
        ]
        |> List.filter (not << List.isEmpty)
        |> List.collect (fun section -> "" :: section)
        |> List.tail
        |> fun lines -> String.Join("\n", lines) + "\n"
