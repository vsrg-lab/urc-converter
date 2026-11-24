namespace UrcConverter.Sources.Bms

module internal Channels =

    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Bms.Model

    [<RequireQualifiedAccess>]
    type ChannelKind =
        | Visible
        | LongNote
        | Mine

    [<RequireQualifiedAccess>]
    type BmsMode =
        | Keys5
        | Keys7
        | Keys10
        | Keys14
        | Pms9
        | Pms18

    let systemChannels = Set.ofList [ "02"; "03"; "08"; "09"; "SC" ]

    let idValue (text: string) (b: int) =
        let digit (c: char) =
            if c >= '0' && c <= '9' then int c - int '0'
            elif c >= 'A' && c <= 'Z' then int c - int 'A' + 10
            else int c - int 'a' + 36

        (digit text[0]) * b + (digit text[1])

    let channelKind (channel: string) : ChannelKind option =
        if channel.Length <> 2 then
            None
        else
            let c0, c1 = channel[0], channel[1]

            if (c0 = '1' || c0 = '2') && (c1 >= '1' && c1 <= '9') then
                Some ChannelKind.Visible
            elif (c0 = '5' || c0 = '6') && (c1 >= '1' && c1 <= '9') then
                Some ChannelKind.LongNote
            elif (c0 = 'D' || c0 = 'E') && (c1 >= '1' && c1 <= '9') then
                Some ChannelKind.Mine
            else
                None

    let sideOf (c: char) =
        match c with
        | '1'
        | '5'
        | 'D' -> 0
        | _ -> 1

    let detectMode (pms: bool) (used: Set<int * char>) : BmsMode =
        if pms then
            let is18 =
                used
                |> Set.exists (fun (side, second) ->
                    (second >= '6' && second <= '9') || (side = 1 && second = '1'))

            if is18 then BmsMode.Pms18 else BmsMode.Pms9
        else
            let seven = used |> Set.exists (fun (_, second) -> second = '8' || second = '9')
            let isDouble = used |> Set.exists (fun (side, _) -> side = 1)

            match seven, isDouble with
            | true, true -> BmsMode.Keys14
            | false, true -> BmsMode.Keys10
            | true, false -> BmsMode.Keys7
            | false, false -> BmsMode.Keys5

    let getLane (mode: BmsMode) (channel: string) : int option =
        if channel.Length <> 2 then
            None
        else
            let side = sideOf channel[0]
            let key = channel[1]

            match mode, side with
            | (BmsMode.Keys5 | BmsMode.Keys10), 0 ->
                if key = '6' then Some 0
                elif key >= '1' && key <= '5' then Some(int key - int '0')
                else None
            | (BmsMode.Keys5 | BmsMode.Keys10), 1 ->
                if key = '6' then Some 11
                elif key >= '1' && key <= '5' then Some((int key - int '0') + 5)
                else None
            | (BmsMode.Keys7 | BmsMode.Keys14), 0 ->
                if key = '6' then Some 0
                elif key >= '1' && key <= '5' then Some(int key - int '0')
                elif key = '8' || key = '9' then Some(int key - int '8' + 6)
                else None
            | (BmsMode.Keys7 | BmsMode.Keys14), 1 ->
                if key = '6' then Some 15
                elif key >= '1' && key <= '5' then Some((int key - int '0') + 7)
                elif key = '8' || key = '9' then Some(int key - int '8' + 13)
                else None
            | BmsMode.Pms9, 0 when key >= '1' && key <= '5' -> Some(int key - int '1')
            | BmsMode.Pms9, 1 when key >= '2' && key <= '5' -> Some(int key - int '2' + 5)
            | BmsMode.Pms18, side ->
                let baseLane = side * 9

                match key with
                | '1'
                | '2'
                | '3'
                | '4'
                | '5' -> Some(baseLane + (int key - int '1'))
                | '8' -> Some(baseLane + 5)
                | '9' -> Some(baseLane + 6)
                | '6' -> Some(baseLane + 7)
                | '7' -> Some(baseLane + 8)
                | _ -> None
            | _ -> None

    let private emitHold (lane: int) (startUs: float) (endUs: float) (notes: (int * int * NoteType) list) =
        let lsMs = Shared.roundMs (startUs / 1000.0)
        let leMs = Shared.roundMs (endUs / 1000.0)

        if leMs <= lsMs then
            Error(UrcError.zeroLengthHold 1 $"zero-length hold on lane {lane} at {lsMs}ms")
        else
            Ok((leMs, lane, NoteType.LE) :: (lsMs, lane, NoteType.LS) :: notes)

    let pairLongNotes
        (chart: BmsChart)
        (stream: (float * string) list)
        (lane: int)
        : Result<(int * int * NoteType) list, UrcError> =
        let rec loop (start: float option) (notes: (int * int * NoteType) list) (rest: (float * string) list) =
            match rest with
            | [] ->
                match start with
                | Some _ -> Error(UrcError.syntax 1 $"long note on lane {lane} has no end")
                | None -> Ok(List.rev notes)
            | (time, obj) :: tail ->
                if chart.LnType = 1 then
                    if obj = "00" then
                        loop start notes tail
                    else
                        match start with
                        | None -> loop (Some time) notes tail
                        | Some s ->
                            match emitHold lane s time notes with
                            | Ok updated -> loop None updated tail
                            | Error err -> Error err
                else if obj = "00" then
                    match start with
                    | Some s ->
                        match emitHold lane s time notes with
                        | Ok updated -> loop None updated tail
                        | Error err -> Error err
                    | None -> loop None notes tail
                elif Option.isNone start then
                    loop (Some time) notes tail
                else
                    loop start notes tail

        loop None [] stream

    let private buildVisibleNotes
        (lnObj: string option)
        (lane: int)
        (stream: (float * string) list)
        : Result<(int * int * NoteType) list, UrcError> =
        let rec loop (pending: float option) (notes: (int * int * NoteType) list) (rest: (float * string) list) =
            match rest with
            | [] ->
                let finalNotes =
                    match pending with
                    | Some p -> (Shared.roundMs (p / 1000.0), lane, NoteType.N) :: notes
                    | None -> notes

                Ok(List.rev finalNotes)
            | (time, obj) :: tail ->
                match lnObj, pending with
                | Some marker, Some p when obj = marker ->
                    match emitHold lane p time notes with
                    | Ok updated -> loop None updated tail
                    | Error err -> Error err
                | _ ->
                    let acc =
                        match pending with
                        | Some p -> (Shared.roundMs (p / 1000.0), lane, NoteType.N) :: notes
                        | None -> notes

                    loop (Some time) acc tail

        loop None [] stream

    let buildNotes
        (chart: BmsChart)
        (mode: BmsMode)
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
            | Some lane, Some ChannelKind.Mine ->
                stream
                |> List.map (fun (time, _) -> (Shared.roundMs (time / 1000.0), lane, NoteType.M))
                |> Ok
            | Some lane, Some ChannelKind.LongNote -> pairLongNotes chart stream lane
            | Some lane, Some ChannelKind.Visible -> buildVisibleNotes chart.LnObj lane stream
            | _ -> Ok []

        streams |> List.traverseResultM channelNotes |> Result.map List.concat

    let resolveLayout (mode: BmsMode) =
        match mode with
        | BmsMode.Keys5 -> 5, 1, Some [ 0 ]
        | BmsMode.Keys7 -> 7, 1, Some [ 0 ]
        | BmsMode.Keys10 -> 10, 2, Some [ 0; 11 ]
        | BmsMode.Keys14 -> 14, 2, Some [ 0; 15 ]
        | BmsMode.Pms9 -> 9, 0, None
        | BmsMode.Pms18 -> 18, 0, None
