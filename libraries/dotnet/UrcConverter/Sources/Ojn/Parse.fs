namespace UrcConverter.Sources.Ojn

module Parse =

    open System
    open System.Buffers.Binary
    open System.Text
    open FsToolkit.ErrorHandling
    open UrcConverter
    open UrcConverter.Sources
    open UrcConverter.Sources.Ojn.Model

    let private stringRanges = [ (108, 172); (172, 204); (204, 236); (236, 268) ]

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

    let private decodeStrings (data: byte[]) : Result<string * string * string, UrcError> =
        result {
            let fields =
                stringRanges
                |> List.map (fun (start, end') ->
                    let field = data[start .. end' - 1]

                    match field |> Array.tryFindIndex (fun byte -> byte = 0uy) with
                    | Some nul -> field[.. nul - 1]
                    | None -> field)

            let blob = Array.concat fields

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)
            let utf8 = UTF8Encoding(false, true)
            let cp949 = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)

            let validates (encoding: Encoding) (bytes: byte[]) =
                try
                    encoding.GetString bytes |> ignore
                    true
                with :? DecoderFallbackException ->
                    false

            let charset: Encoding option =
                if blob |> Array.forall (fun byte -> byte < 0x80uy) then Some utf8
                elif validates utf8 blob then Some utf8
                elif validates cp949 blob then Some cp949
                else None

            match charset with
            | None -> return! Error(UrcError.syntax 108 "strings are neither ASCII, UTF-8, nor CP949")
            | Some encoding ->
                let decoded =
                    fields
                    |> List.map (fun field ->
                        try
                            Ok(encoding.GetString field)
                        with :? DecoderFallbackException ->
                            Error(UrcError.syntax 108 "invalid string byte sequence"))

                let! strings = Shared.sequence decoded
                return strings[0], strings[1], strings[2]
        }

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

            let event kind value =
                { Measure = measure; Position = position; Channel = channel; Value = value; Kind = kind; Offset = offset }

            let acc' =
                if channel <= 1 then
                    let value = readF32 data offset
                    if value <= 0.0 then acc else event 0 value :: acc
                else
                    let value = readI16 data offset
                    let noteType = int data[offset + 3]

                    if channel >= 9 || value = 0s || noteType % 4 = 1 then
                        acc
                    else
                        event (noteType % 4) 0.0 :: acc

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
            let total = int (readI16 data (offset + 6))

            if measure < 0 then
                Error(UrcError.syntax offset "negative measure index")
            else
                match readEvents data (offset + 8) bound channel measure (max total 0) 0 [] with
                | Error error -> Error error
                | Ok(nextOffset, acc') -> readPackages data nextOffset bound count (packageIndex + 1) (acc' :: acc)

    let private readDifficulty
        (data: byte[])
        (index: int)
        (start: int)
        (bound: int)
        (count: int)
        : Result<OjnDifficulty, UrcError> =
        result {
            if start < 300 then
                return! Error(UrcError.syntax start "note section overlaps the header")
            else
                let! events = readPackages data start bound count 0 []
                return { Index = index; Events = events }
        }

    /// Parses a plain or encrypted ("new" magic) OJN file into its source model.
    let parseOjn (bytes: byte[]) : Result<OjnFile, UrcError> =
        result {
            let data =
                if bytes.Length >= 8 && bytes[0] = 0x6euy && bytes[1] = 0x65uy && bytes[2] = 0x77uy then
                    decrypt bytes
                else
                    bytes

            if data.Length < 300 then
                return! Error(UrcError.syntax data.Length "file too short for OJN header")
            elif data[4..7] <> signature then
                return! Error(UrcError.syntax 4 "invalid OJN signature")
            else
                let bpm = readF32 data 16

                if bpm <= 0.0 then
                    return! Error(UrcError.syntax 16 "header BPM must be positive")
                else
                    let! title, artist, noter = decodeStrings data
                    let packageCounts = [| readI32 data 64; readI32 data 68; readI32 data 72 |]
                    let noteOffsets = [| readI32 data 284; readI32 data 288; readI32 data 292 |]
                    let coverOffset = readI32 data 296

                    let! difficulties =
                        [ 0 .. 2 ]
                        |> List.filter (fun index -> packageCounts[index] <> 0)
                        |> List.map (fun index ->
                            let endRaw = if index < 2 then noteOffsets[index + 1] else coverOffset
                            let endBound = min (max endRaw 0) data.Length
                            readDifficulty data index noteOffsets[index] endBound packageCounts[index])
                        |> Shared.sequence

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
