module internal UrcConverter.Parser.Ojn.Parsing

open System
open System.IO
open System.Text
open FsToolkit.ErrorHandling
open UrcConverter.Parser.Ojn.Internal.Models

// #region Helpers

let private readFixedString (r: BinaryReader) (size: int): string =
    let bytes = r.ReadBytes size
    let nullIdx = bytes |> Array.tryFindIndex ((=) 0uy)
    let len = nullIdx |> Option.defaultValue bytes.Length

    Encoding.UTF8.GetString(bytes, 0, len)

// #endregion

// #region Header (300 bytes)

let private readHeader (r: BinaryReader): Result<OjnHeader, string> =
    result {
        let songId = r.ReadInt32()
        let signature = r.ReadBytes 4

        do! Result.requireTrue 
                "Invalid OJN signature"
                (signature.Length >= 3 && signature[0] = byte 'o' &&  signature[1] = byte 'j' && signature[2] = byte 'n')
            
        let _encodeVersion = r.ReadSingle()
        let _genre = r.ReadInt32()
        let bpm = r.ReadSingle() |> float
        let level = [| for _ in 1 .. 4 -> r.ReadInt16() |> int |]
        let _eventCount = [| for _ in 1 .. 3-> r.ReadInt32() |]
        let noteCount = [| for _ in 1 .. 3 -> r.ReadInt32() |]
        let _measureCount = [| for _ in 1 .. 3 -> r.ReadInt32() |]
        let packageCount = [| for _ in 1 .. 3 -> r.ReadInt32() |]
        let _oldEncodeVersion = r.ReadInt16()
        let _oldSongId = r.ReadInt16()
        let _oldGenre = r.ReadBytes 20
        let _bmpSize = r.ReadInt32()
        let _oldFileVersion = r.ReadInt32()
        let title = readFixedString r 64
        let artist = readFixedString r 32
        let noter = readFixedString r 32
        let _ojmFile = r.ReadBytes 32
        let _coverSize = r.ReadInt32()
        let _time = [| for _ in 1 .. 3 -> r.ReadInt32() |]
        let noteOffset = [| for _ in 1 .. 3 -> r.ReadInt32() |]
        let coverOffset = r.ReadInt32()

        do! Result.requireTrue "No valid BPM found" (bpm > 0.0)

        return {
            SongId = songId
            Bpm = bpm
            Level = level[0 .. 2]
            NoteCount = noteCount
            PackageCount = packageCount
            Title = title
            Artist = artist
            Noter = noter
            NoteOffset = noteOffset
            CoverOffset = coverOffset
        }
    }

// #endregion

// #region Note Events (4 bytes each)

let private readEvent (r: BinaryReader) (channel: int): OjnEvent =
    match channel with
    | 0 ->
        let v = r.ReadSingle() |> float
        if v > 0.0 then MeasureFraction v else Padding
    | 1 ->
        let v = r.ReadSingle() |> float
        if v > 0.0 then BpmChange v else Padding
    | _ ->
        let value = r.ReadInt16() |> int
        let _volPan = r.ReadByte()
        let noteType = r.ReadByte() |> int
        if value = 0 then Padding
        else Note { Value = value; NoteType = noteType }

// #endregion

// #region Packages

/// Read packages from a note section.
/// Each package: header (8 bytes) + events (count * 4 bytes)
let private readPackages (r: BinaryReader) (count: int): OjnPackage list =
    [
        for _ in 1 .. count do
            let measure = r.ReadInt32()
            let channel = r.ReadInt16() |> int
            let eventCount = r.ReadInt16() |> int
            let events = [ for _ in 1 .. eventCount -> readEvent r channel ]
            { Measure = measure; Channel = channel; Events = events }
    ]

// #endregion

// Public API
let parseOjnFile (filePath: string): Result<OjnFile, string> =
    try
        use stream = File.OpenRead filePath
        use reader = new BinaryReader(stream, Encoding.UTF8, false)

        result {
            do! Result.requireTrue "File too small for OJN header" (stream.Length >= 300L)

            let! header = readHeader reader

            let packages =
                [|
                    for i in 0 .. 2 do
                        stream.Position <- int64 header.NoteOffset[i]
                        readPackages reader header.PackageCount[i]
                |]

            do! Result.requireTrue
                    "No playable note data found"
                    (packages |> Array.exists (fun pkgs ->
                        pkgs |> List.exists (fun p ->
                            p.Channel >= 2 && p.Channel <= 8 && 
                            p.Events 
                            |> List.exists (fun e -> 
                                match e with 
                                | Note _ -> true 
                                | _ -> false))))

            return { Header = header; Packages = packages }
        }
    with ex ->
        Result.Error $"Failed to read file: {ex.Message}"