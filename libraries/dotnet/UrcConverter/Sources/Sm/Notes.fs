namespace UrcConverter.Sources.Sm

module internal Notes =

    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Sm.Model
    open Preprocess

    [<Literal>]
    let RollTapSpacingMs = 500

    type UrcNote = int * int * NoteType

    let private difficultyNames =
        [
            "beginner", "Beginner"
            "easy", "Easy"
            "basic", "Easy"
            "light", "Easy"
            "medium", "Medium"
            "another", "Medium"
            "trick", "Medium"
            "standard", "Medium"
            "difficult", "Medium"
            "hard", "Hard"
            "ssr", "Hard"
            "maniac", "Hard"
            "heavy", "Hard"
            "smaniac", "Challenge"
            "challenge", "Challenge"
            "expert", "Challenge"
            "oni", "Challenge"
            "edit", "Edit"
        ]
        |> Map.ofList

    let difficultyName (difficulty: string) (description: string) =
        let key = difficulty.Trim().ToLowerInvariant()
        let name = difficultyNames |> Map.tryFind key |> Option.defaultValue "Edit"

        if
            name = "Hard"
            && (let desc = description.Trim().ToLowerInvariant() in desc = "smaniac" || desc = "challenge")
        then
            "Challenge"
        else
            name

    let firstNonEmpty (first: string) (second: string) (fallback: string) =
        if first.Length > 0 then first
        elif second.Length > 0 then second
        else fallback

    let parseNoteData (data: string) (lanes: int) : Result<SmNote list, UrcError> =
        let notes = ResizeArray<SmNote>()

        let processMeasure (measure: int) (part: string) (openHolds: Map<int, int>) : Result<Map<int, int>, UrcError> =
            let content =
                part.Split('\n')
                |> Array.map (fun raw -> raw.Trim(' ', '\t', '\r'))
                |> Array.filter (fun line -> line.Length > 0)

            let total = float content.Length

            let rec processLines (index: int) (openHolds: Map<int, int>) : Result<Map<int, int>, UrcError> =
                if index >= content.Length then
                    Ok openHolds
                else
                    let line = content[index]
                    let row = rowsOf ((float measure + float index / total) * 4.0)
                    let chars = line.ToCharArray()

                    // Keysound index suffixes ("[3]") are consumed but dropped.
                    let skipKeysound (position: int) =
                        if position < chars.Length && chars[position] = '[' then
                            let rec findBracket (i: int) =
                                if i >= chars.Length || chars[i] = ']' then i else findBracket (i + 1)

                            let end' = findBracket position
                            if end' >= chars.Length then chars.Length else end' + 1
                        else
                            position

                    let rec processChars (track: int) (position: int) (holds: Map<int, int>) : Result<Map<int, int>, UrcError> =
                        if track >= lanes || position >= chars.Length then
                            Ok holds
                        else
                            let char = chars[position]
                            let position = position + 1

                            let addNote kind =
                                notes.Add({ Row = row; Track = track; Kind = kind; TailRow = None })

                            match char with
                            | '1' ->
                                addNote Tap
                                processChars (track + 1) (skipKeysound position) holds
                            | '2'
                            | '4' ->
                                if holds.ContainsKey track then
                                    Error(UrcError.syntax 1 $"overlapping hold head at row {row}")
                                else
                                    addNote (if char = '2' then Hold else Roll)
                                    processChars (track + 1) (skipKeysound position) (Map.add track (notes.Count - 1) holds)
                            | '3' ->
                                match holds |> Map.tryFind track with
                                | Some noteIndex ->
                                    let head = notes[noteIndex]
                                    notes[noteIndex] <- { head with TailRow = Some row }
                                    processChars (track + 1) (skipKeysound position) (Map.remove track holds)
                                | None -> Error(UrcError.syntax 1 $"hold tail without a head at row {row}")
                            | 'M' ->
                                addNote Mine
                                processChars (track + 1) (skipKeysound position) holds
                            | 'L' ->
                                addNote Lift
                                processChars (track + 1) (skipKeysound position) holds
                            | 'F' ->
                                addNote FakeNote
                                processChars (track + 1) (skipKeysound position) holds
                            | _ ->
                                // Unknown characters are ignored, like StepMania.
                                processChars (track + 1) (skipKeysound position) holds

                    processChars 0 0 openHolds
                    |> Result.bind (fun holds -> processLines (index + 1) holds)

            processLines 0 openHolds

        // Zero-length measure parts are skipped without advancing the index.
        let rec loopMeasures (measure: int) (parts: string list) (openHolds: Map<int, int>) : Result<Map<int, int>, UrcError> =
            match parts with
            | [] -> Ok openHolds
            | part :: tail when part.Length = 0 -> loopMeasures measure tail openHolds
            | part :: tail ->
                processMeasure measure part openHolds
                |> Result.bind (fun holds -> loopMeasures (measure + 1) tail holds)

        loopMeasures 0 (List.ofArray (data.Split(','))) Map.empty
        |> Result.bind (fun openHolds ->
            if not (Map.isEmpty openHolds) then
                Error(UrcError.syntax 1 "hold note without a tail")
            else
                Ok(List.ofSeq notes))

    let buildUrcNotes
        (timing: Timing)
        (notes: SmNote list)
        (headTimes: Map<int, float>)
        (tailTimes: Map<int, float>)
        : Result<UrcNote list, UrcError> =
        let fakeRanges =
            timing.Fakes
            |> List.map (fun (beat, length) -> let start = rowsOf beat in (start, start + rowsOf length))

        let rec loop (rest: SmNote list) (index: int) (acc: UrcNote list) : Result<UrcNote list, UrcError> =
            match rest with
            | [] -> Ok(List.rev acc)
            | note :: tail ->
                let headMs =
                    headTimes
                    |> Map.tryFind index
                    |> Option.defaultValue 0.0
                    |> fun sec -> Shared.roundMs (sec * 1000.0)

                let inFake =
                    fakeRanges |> List.exists (fun (start, end') -> start <= note.Row && note.Row < end')

                if inFake then
                    loop tail (index + 1) ((headMs, note.Track, NoteType.F) :: acc)
                else
                    match note.Kind with
                    | Hold ->
                        let tailMs =
                            tailTimes
                            |> Map.tryFind index
                            |> Option.defaultValue 0.0
                            |> fun sec -> Shared.roundMs (sec * 1000.0)

                        if tailMs <= headMs then
                            Error(UrcError.syntax 1 $"hold on lane {note.Track} collapses to zero length")
                        else
                            loop
                                tail
                                (index + 1)
                                ((tailMs, note.Track, NoteType.LE)
                                 :: (headMs, note.Track, NoteType.LS)
                                 :: acc)
                    | Roll ->
                        let endMs =
                            tailTimes
                            |> Map.tryFind index
                            |> Option.defaultValue 0.0
                            |> fun sec -> Shared.roundMs (sec * 1000.0)

                        let taps =
                            [
                                for tapMs in headMs + RollTapSpacingMs .. RollTapSpacingMs .. endMs - 1 ->
                                    (tapMs, note.Track, NoteType.N)
                            ]

                        loop tail (index + 1) ((headMs, note.Track, NoteType.N) :: (List.append taps acc))
                    | Mine -> loop tail (index + 1) ((headMs, note.Track, NoteType.M) :: acc)
                    | FakeNote -> loop tail (index + 1) ((headMs, note.Track, NoteType.F) :: acc)
                    | Tap
                    | Lift -> loop tail (index + 1) ((headMs, note.Track, NoteType.N) :: acc)

        loop notes 0 []
