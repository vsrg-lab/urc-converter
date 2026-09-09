namespace UrcConverter.Sources.Bms

module internal Channels =

    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Bms.Model

    let systemChannels = Set.ofList [ "02"; "03"; "08"; "09"; "SC" ]

    let idValue (text: string) (b: int) =
        let digit (c: char) =
            if c >= '0' && c <= '9' then int c - int '0'
            elif c >= 'A' && c <= 'Z' then int c - int 'A' + 10
            else int c - int 'a' + 36
        (digit text[0]) * b + (digit text[1])

    let channelKind (channel: string) =
        if channel.Length <> 2 then None
        else
            let c0, c1 = channel[0], channel[1]
            if (c0 = '1' || c0 = '2') && (c1 >= '1' && c1 <= '9') then Some "visible"
            elif (c0 = '5' || c0 = '6') && (c1 >= '1' && c1 <= '9') then Some "ln"
            elif (c0 = 'D' || c0 = 'E') && (c1 >= '1' && c1 <= '9') then Some "mine"
            else None

    let sideOf (c: char) =
        match c with
        | '1' | '5' | 'D' -> 0
        | _ -> 1

    let detectMode (pms: bool) (used: Set<int * char>) =
        if pms then
            let is18 =
                used
                |> Set.exists (fun (side, second) ->
                    (second >= '6' && second <= '9') || (side = 1 && second = '1'))
            if is18 then "PMS18" else "PMS9"
        else
            let seven = used |> Set.exists (fun (_, second) -> second = '8' || second = '9')
            let isDouble = used |> Set.exists (fun (side, _) -> side = 1)
            match seven, isDouble with
            | true, true -> "14K"
            | false, true -> "10K"
            | true, false -> "7K"
            | false, false -> "5K"

    let getLane (mode: string) (channel: string) : int option =
        if channel.Length <> 2 then None
        else
            let side = sideOf channel[0]
            let key = channel[1]
            match mode, side with
            | ("5K" | "10K"), side ->
                if key = '6' then Some (side * 6)
                elif key >= '1' && key <= '5' then Some ((int key - int '0') + side * 6)
                else None
            | ("7K" | "14K"), side ->
                if key = '6' then Some (side * 8)
                elif key >= '1' && key <= '5' then Some ((int key - int '0') + side * 8)
                elif key = '8' || key = '9' then Some ((int key - int '8' + 6) + side * 8)
                else None
            | "PMS9", 0 when key >= '1' && key <= '5' -> Some (int key - int '1')
            | "PMS9", 1 when key >= '2' && key <= '5' -> Some (int key - int '2' + 5)
            | "PMS18", side ->
                let baseLane = side * 9
                match key with
                | '1' | '2' | '3' | '4' | '5' -> Some (baseLane + (int key - int '1'))
                | '8' -> Some (baseLane + 5)
                | '9' -> Some (baseLane + 6)
                | '6' -> Some (baseLane + 7)
                | '7' -> Some (baseLane + 8)
                | _ -> None
            | _ -> None

    let pairLongNotes (chart: BmsChart) (stream: (float * string) list) (lane: int) : Result<(int * int * NoteType) list, UrcError> =
        let folder (start, notes) (time, obj) =
            if chart.LnType = 1 then
                if obj <> "00" then
                    match start with
                    | None -> Some time, notes
                    | Some s ->
                        let ls = (Shared.roundMs (s / 1000.0), lane, NoteType.LS)
                        let le = (Shared.roundMs (time / 1000.0), lane, NoteType.LE)
                        None, le :: ls :: notes
                else
                    start, notes
            else
                if obj = "00" then
                    match start with
                    | Some s ->
                        let ls = (Shared.roundMs (s / 1000.0), lane, NoteType.LS)
                        let le = (Shared.roundMs (time / 1000.0), lane, NoteType.LE)
                        None, le :: ls :: notes
                    | None -> None, notes
                elif Option.isNone start then
                    Some time, notes
                else
                    start, notes

        let finalStart, finalNotes = stream |> List.fold folder (None, [])
        match finalStart with
        | Some _ -> Error(UrcError.Syntax(1, $"long note on lane {lane} has no end"))
        | None -> Ok(List.rev finalNotes)

    let buildNotes
        (chart: BmsChart)
        (mode: string)
        (objects: (float * string * string) list)
        (timed: Map<int, float>)
        : Result<(int * int * NoteType) list, UrcError> =
        let streams =
            objects
            |> List.indexed
            |> List.groupBy (fun (_, (_, channel, _)) -> channel)
            |> List.map (fun (channel, group) ->
                channel, group |> List.map (fun (idx, (_, _, obj)) -> Map.find idx timed, obj))

        let channelNotes (channel, stream) : Result<(int * int * NoteType) list, UrcError> =
            match getLane mode channel, channelKind channel with
            | Some lane, Some "mine" ->
                stream
                |> List.map (fun (time, _) -> (Shared.roundMs (time / 1000.0), lane, NoteType.M))
                |> Ok
            | Some lane, Some "ln" ->
                pairLongNotes chart stream lane
            | Some lane, Some _ ->
                let folder (pending, notes) (time, obj) =
                    match chart.LnObj, pending with
                    | Some lnobj, Some p when obj = lnobj ->
                        let ls = (Shared.roundMs (p / 1000.0), lane, NoteType.LS)
                        let le = (Shared.roundMs (time / 1000.0), lane, NoteType.LE)
                        None, le :: ls :: notes
                    | _ ->
                        let acc =
                            match pending with
                            | Some p -> (Shared.roundMs (p / 1000.0), lane, NoteType.N) :: notes
                            | None -> notes
                        Some time, acc

                let lastPending, foldedNotes = stream |> List.fold folder (None, [])
                let finalNotes =
                    match lastPending with
                    | Some p -> (Shared.roundMs (p / 1000.0), lane, NoteType.N) :: foldedNotes
                    | None -> foldedNotes
                Ok (List.rev finalNotes)
            | _ -> Ok []

        streams
        |> List.traverseResultM channelNotes
        |> Result.map List.concat

    let resolveLayout (mode: string) =
        match mode with
        | "5K" -> 5, 1, Some [ 0 ]
        | "7K" -> 7, 1, Some [ 0 ]
        | "10K" -> 10, 2, Some [ 0; 6 ]
        | "14K" -> 14, 2, Some [ 0; 8 ]
        | "PMS9" -> 9, 0, None
        | "PMS18" -> 18, 0, None
        | _ -> failwith "unreachable"
