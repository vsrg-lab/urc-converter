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

    type private ParseAcc =
        {
            NextSeq: int
            Completed: (int * SmNote) list
            OpenHolds: Map<int, int * SmNote>
        }

    let parseNoteData (data: string) (lanes: int) : Result<SmNote list, UrcError> =
        let skipKeysound (chars: char[]) (position: int) =
            if position < chars.Length && chars[position] = '[' then
                let rec findBracket (i: int) =
                    if i >= chars.Length || chars[i] = ']' then i else findBracket (i + 1)

                let endIdx = findBracket position
                if endIdx >= chars.Length then chars.Length else endIdx + 1
            else
                position

        let rec processChars
            (row: int)
            (chars: char[])
            (track: int)
            (position: int)
            (acc: ParseAcc)
            : Result<ParseAcc, UrcError> =
            if track >= lanes || position >= chars.Length then
                Ok acc
            else
                let ch = chars[position]
                let nextPos = skipKeysound chars (position + 1)

                let pushSimple kind =
                    let note = { Row = row; Track = track; Kind = kind; TailRow = None }

                    { acc with
                        NextSeq = acc.NextSeq + 1
                        Completed = (acc.NextSeq, note) :: acc.Completed
                    }

                match ch with
                | '1' -> processChars row chars (track + 1) nextPos (pushSimple Tap)
                | '2'
                | '4' ->
                    if Map.containsKey track acc.OpenHolds then
                        Error(UrcError.syntax 1 $"overlapping hold head at row {row}")
                    else
                        let kind = if ch = '2' then Hold else Roll
                        let head = { Row = row; Track = track; Kind = kind; TailRow = None }

                        let nextAcc =
                            { acc with
                                NextSeq = acc.NextSeq + 1
                                OpenHolds = Map.add track (acc.NextSeq, head) acc.OpenHolds
                            }

                        processChars row chars (track + 1) nextPos nextAcc
                | '3' ->
                    match Map.tryFind track acc.OpenHolds with
                    | Some(seqId, head) ->
                        let closed = { head with TailRow = Some row }

                        let nextAcc =
                            { acc with
                                Completed = (seqId, closed) :: acc.Completed
                                OpenHolds = Map.remove track acc.OpenHolds
                            }

                        processChars row chars (track + 1) nextPos nextAcc
                    | None -> Error(UrcError.syntax 1 $"hold tail without a head at row {row}")
                | 'M' -> processChars row chars (track + 1) nextPos (pushSimple Mine)
                | 'L' -> processChars row chars (track + 1) nextPos (pushSimple Lift)
                | 'F' -> processChars row chars (track + 1) nextPos (pushSimple FakeNote)
                | _ ->
                    // Unknown characters are ignored, like StepMania.
                    processChars row chars (track + 1) nextPos acc

        let processMeasure (measure: int) (part: string) (acc: ParseAcc) : Result<ParseAcc, UrcError> =
            let content =
                part.Split('\n')
                |> Array.map (fun raw -> raw.Trim(' ', '\t', '\r'))
                |> Array.filter (fun line -> line.Length > 0)

            let total = float content.Length

            let rec processLines (index: int) (lineAcc: ParseAcc) : Result<ParseAcc, UrcError> =
                if index >= content.Length then
                    Ok lineAcc
                else
                    let row = rowsOf ((float measure + float index / total) * 4.0)
                    let chars = content[index].ToCharArray()

                    match processChars row chars 0 0 lineAcc with
                    | Ok nextAcc -> processLines (index + 1) nextAcc
                    | Error err -> Error err

            processLines 0 acc

        // Zero-length measure parts are skipped without advancing the index.
        let rec loopMeasures (measure: int) (parts: string list) (acc: ParseAcc) : Result<ParseAcc, UrcError> =
            match parts with
            | [] -> Ok acc
            | part :: tail when part.Length = 0 -> loopMeasures measure tail acc
            | part :: tail ->
                match processMeasure measure part acc with
                | Ok nextAcc -> loopMeasures (measure + 1) tail nextAcc
                | Error err -> Error err

        let initial = { NextSeq = 0; Completed = []; OpenHolds = Map.empty }

        loopMeasures 0 (List.ofArray (data.Split(','))) initial
        |> Result.bind (fun finalAcc ->
            if not (Map.isEmpty finalAcc.OpenHolds) then
                Error(UrcError.syntax 1 "hold note without a tail")
            else
                finalAcc.Completed |> List.sortBy fst |> List.map snd |> Ok)

    let buildUrcNotes
        (timing: Timing)
        (notes: SmNote list)
        (headTimes: Map<int, float>)
        (tailTimes: Map<int, float>)
        : Result<UrcNote list, UrcError> =
        let fakeRanges =
            timing.Fakes
            |> List.map (fun (beat, length) ->
                let start = rowsOf beat
                start, start + rowsOf length)

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

                        if tailMs < headMs then
                            Error(UrcError.syntax 1 $"hold ends before it starts: {tailMs} < {headMs}")
                        elif tailMs = headMs then
                            Error(UrcError.zeroLengthHold 1 $"zero-length hold on lane {note.Track} at {headMs}ms")
                        else
                            loop
                                tail
                                (index + 1)
                                ((tailMs, note.Track, NoteType.LE) :: (headMs, note.Track, NoteType.LS) :: acc)
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
