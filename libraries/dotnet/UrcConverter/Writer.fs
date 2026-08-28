namespace UrcConverter

module Writer =

    open System
    open System.Globalization

    // InvariantCulture ToString gives the shortest round-trip form ("160" for
    // 160.0), matching the Python reference's repr-based formatting.
    let private floatText (value: float) =
        value.ToString(CultureInfo.InvariantCulture)

    let private joined (values: float list) =
        String.Join(", ", List.map floatText values)

    let write (chart: Chart) : string =
        let layout = chart.Layout

        let typeText =
            if layout.SpecialKeys > 0 then
                $"{layout.Keys}+{layout.SpecialKeys}"
            else
                string layout.Keys

        let specialText =
            match layout.SpecialLanes with
            | None -> Strings.specialNone
            | Some lanes -> String.Join(", ", List.map string lanes)

        let lines = ResizeArray<string>()
        lines.Add $"@URC {chart.FormatVersion.Major}.{chart.FormatVersion.Minor}"
        lines.Add ""
        lines.Add Strings.sectionMetadata

        let metadataValues =
            [
                chart.Metadata.Original
                chart.Metadata.Title
                chart.Metadata.Artist
                chart.Metadata.Creator
                chart.Metadata.Version
            ]

        for name, value in List.zip Strings.metadataFields metadataValues do
            lines.Add $"{name}: {value}"

        match chart.Judgment with
        | Some judgment ->
            lines.Add ""
            lines.Add Strings.sectionJudgment
            lines.Add $"{Strings.judgmentFieldWindow}: {joined judgment.Windows}"
            lines.Add $"{Strings.judgmentFieldRate}: {joined judgment.Rates}"
        | None -> ()

        lines.Add ""
        lines.Add Strings.sectionLayout
        lines.Add $"{Strings.layoutFieldType}: {typeText}"
        lines.Add $"{Strings.layoutFieldSpecial}: {specialText}"
        lines.Add ""
        lines.Add Strings.sectionTiming

        for point in chart.TimingPoints do
            let fields =
                [
                    string point.TimestampMs
                    floatText point.Bpm
                    $"{point.Meter.Beats}/{point.Meter.NoteValue}"
                ]
                @ (match point.Multiplier with
                   | Some multiplier -> [ floatText multiplier ]
                   | None -> [])

            lines.Add(String.Join(", ", fields))

        lines.Add ""
        lines.Add Strings.sectionNotes

        for note in List.sortBy (fun note -> note.TimestampMs, note.Lane) chart.Notes do
            lines.Add $"{note.TimestampMs}, {note.Lane}, {note.Type.Token}"

        String.Join("\n", lines) + "\n"
