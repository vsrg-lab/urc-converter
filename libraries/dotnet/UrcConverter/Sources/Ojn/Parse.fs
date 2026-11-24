namespace UrcConverter.Sources.Ojn

[<RequireQualifiedAccess>]
module Parse =

    open System
    open System.Buffers.Binary
    open System.Text
    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Ojn.Model

    let private stringRanges = [ (108, 172); (172, 204); (204, 236) ]

    let private signature = [| 0x6fuy; 0x6auy; 0x6euy; 0uy |]

    let private readI32 (data: byte[]) (offset: int) : int =
        BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan<byte>(data, offset, 4))

    let private readU16 (data: byte[]) (offset: int) : uint16 =
        BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan<byte>(data, offset, 2))

    let private readI16 (data: byte[]) (offset: int) : int16 =
        BinaryPrimitives.ReadInt16LittleEndian(ReadOnlySpan<byte>(data, offset, 2))

    let private readF32 (data: byte[]) (offset: int) : float =
        float (BinaryPrimitives.ReadSingleLittleEndian(ReadOnlySpan<byte>(data, offset, 4)))

    let private decrypt (data: byte[]) : byte[] =
        let block = int data[3]

        if block = 0 then
            data
        else
            let key = Array.create block data[4]
            key[0] <- data[6]
            key[block / 2] <- data[5]
            let size = data.Length
            Array.init (size - 8) (fun i -> data[size - 1 - i] ^^^ key[i % block])

    let private decodeField (encodings: Encoding list) (field: byte[]) : Result<string, UrcError> =
        let rec tryNext remaining =
            match remaining with
            | [] -> Error(UrcError.syntax 108 "undecodable OJN metadata string")
            | (encoding: Encoding) :: rest ->
                try
                    Ok(encoding.GetString field)
                with :? DecoderFallbackException ->
                    tryNext rest

        tryNext encodings

    let private decodeStrings (data: byte[]) : Result<string * string * string, UrcError> =
        result {
            Shared.ensureCodePages ()

            let encodings =
                [
                    UTF8Encoding(false, true) :> Encoding
                    Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                ]

            let fields =
                stringRanges
                |> List.map (fun (start, end') ->
                    let field = data[start .. end' - 1]

                    match field |> Array.tryFindIndex (fun byte -> byte = 0uy) with
                    | Some nul -> field[.. nul - 1]
                    | None -> field)

            let! strings = fields |> List.traverseResultM (decodeField encodings)

            match strings with
            | [ title; artist; noter ] -> return title, artist, noter
            | _ -> return! Error(UrcError.syntax 108 "missing header string fields")
        }

    let private sectionEnd (start: int) (noteOffsets: int[]) (coverOffset: int) (fileLen: int) : int =
        let candidates =
            [| noteOffsets[0]; noteOffsets[1]; noteOffsets[2]; coverOffset |]
            |> Array.filter (fun candidate -> candidate > start)

        let bound = if candidates.Length = 0 then fileLen else Array.min candidates
        min bound fileLen

    let rec private readEvents
        (data: byte[])
        (offset: int)
        (bound: int)
        (channel: int)
        (measure: int)
        (total: int)
        (eventIndex: int)
        (acc: OjnEvent list)
        : Result<int * OjnEvent list, UrcError> =
        if eventIndex >= total then
            Ok(offset, List.rev acc)
        elif offset + 4 > bound then
            Error(UrcError.syntax offset "note section truncated")
        else
            let position = float eventIndex / float total

            let mkEvent kind =
                {
                    Measure = measure
                    Position = position
                    Kind = kind
                    Offset = offset
                }

            let acc' =
                if channel = 0 then
                    let value = readF32 data offset

                    if not (Double.IsFinite value) || value <= 0.0 then
                        acc
                    else
                        mkEvent (OjnEventKind.MeasureFraction value) :: acc
                elif channel = 1 then
                    let value = readF32 data offset

                    if not (Double.IsFinite value) || value <= 0.0 then
                        acc
                    else
                        mkEvent (OjnEventKind.BpmChange value) :: acc
                elif channel >= 9 then
                    acc
                else
                    let value = readI16 data offset
                    let noteType = int data[offset + 3]

                    if value = 0s then
                        acc
                    else
                        let lane = channel - 2

                        match noteType % 4 with
                        | 0 -> mkEvent (OjnEventKind.Note(lane, OjnNoteKind.Normal)) :: acc
                        | 2 -> mkEvent (OjnEventKind.Note(lane, OjnNoteKind.HoldStart)) :: acc
                        | 3 -> mkEvent (OjnEventKind.Note(lane, OjnNoteKind.HoldEnd)) :: acc
                        | _ -> acc

            readEvents data (offset + 4) bound channel measure total (eventIndex + 1) acc'

    let rec private readPackages
        (data: byte[])
        (offset: int)
        (bound: int)
        (count: int)
        (packageIndex: int)
        (acc: OjnEvent list list)
        : Result<OjnEvent list, UrcError> =
        if packageIndex >= count then
            Ok(acc |> List.rev |> List.concat)
        elif offset + 8 > bound then
            Error(UrcError.syntax offset "note section truncated")
        else
            let measure = readI32 data offset
            let channel = int (readU16 data (offset + 4))
            let total = int (readU16 data (offset + 6))

            if measure < 0 then
                Error(UrcError.syntax offset "negative measure index")
            else
                match readEvents data (offset + 8) bound channel measure total 0 [] with
                | Error error -> Error error
                | Ok(nextOffset, acc') -> readPackages data nextOffset bound count (packageIndex + 1) (acc' :: acc)

    let private readDifficulty
        (data: byte[])
        (index: int)
        (start: int)
        (bound: int)
        (count: int)
        : Result<OjnDifficulty, UrcError> =
        if start < 300 then
            Error(UrcError.syntax start "note section overlaps the header")
        else
            readPackages data start bound count 0 []
            |> Result.map (fun events -> { Index = index; Events = events })

    /// Parses a plain or encrypted ("new" magic) OJN file into its source model.
    let parse (bytes: byte[]) : Result<OjnFile, UrcError> =
        result {
            let data =
                if
                    bytes.Length >= 8
                    && bytes[0] = 0x6euy
                    && bytes[1] = 0x65uy
                    && bytes[2] = 0x77uy
                    && bytes[4..7] <> signature
                then
                    decrypt bytes
                else
                    bytes

            if data.Length < 300 then
                return! Error(UrcError.syntax data.Length "file too short for OJN header")
            elif data[4..7] <> signature then
                return! Error(UrcError.syntax 4 "invalid OJN signature")
            else
                let bpm = readF32 data 16

                if not (Double.IsFinite bpm) || bpm <= 0.0 then
                    return! Error(UrcError.syntax 16 "header BPM must be positive")
                else
                    let! title, artist, noter = decodeStrings data
                    let packageCounts = [| readI32 data 64; readI32 data 68; readI32 data 72 |]
                    let noteOffsets = [| readI32 data 284; readI32 data 288; readI32 data 292 |]
                    let coverOffset = readI32 data 296

                    let! difficulties =
                        [ 0 .. 2 ]
                        |> List.traverseResultM (fun index ->
                            let count = packageCounts[index]

                            if count < 0 then
                                Error(UrcError.syntax (64 + index * 4) "negative package count")
                            elif count = 0 then
                                Ok None
                            else
                                let start = noteOffsets[index]
                                let endBound = sectionEnd start noteOffsets coverOffset data.Length
                                readDifficulty data index start endBound count |> Result.map Some)
                        |> Result.map (List.choose id)

                    if difficulties.IsEmpty then
                        return! Error(UrcError.syntax 0 "no difficulty with note packages")
                    else
                        return
                            {
                                Title = title
                                Artist = artist
                                Noter = noter
                                Bpm = bpm
                                Difficulties = difficulties
                            }
        }
