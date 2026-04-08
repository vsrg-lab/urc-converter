using System.Text;

namespace UrcConverter.Tests.Fixtures;

public sealed class OjnFileFixture : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public string CreateTempOjn(byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.ojn");
        File.WriteAllBytes(path, data);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try
            {
                File.Delete(f);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }
    
    // Pre-built .ojn content

    #region Event Builders (4 bytes each)

    /// <summary>
    /// Note event: value(2) + volPan(1) + noteType(1).
    /// </summary>
    private static byte[] NoteEvt(short value, byte noteType = 0)
    {
        var b = new byte[4];
        BitConverter.TryWriteBytes(b.AsSpan(0, 2), value);
        b[3] = noteType;
        return b;
    }

    /// <summary>
    /// Padding event (value = 0, ignored by parser).
    /// </summary>
    private static byte[] Pad() => new byte[4];

    /// <summary>
    /// BPM change or measure fraction event (4-byte float).
    /// </summary>
    private static byte[] FloatEvt(float value)
    {
        var b = new byte[4];
        BitConverter.TryWriteBytes(b.AsSpan(), value);
        return b;
    }

    #endregion

    #region Package Builder

    private static byte[] BuildPackage(int measure, short channel, byte[][] events)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        
        w.Write(measure);                   // int32  measure
        w.Write(channel);                   // int16  channel
        w.Write((short)events.Length);       // int16  event count
        
        foreach (var e in events) 
            w.Write(e);
        
        return ms.ToArray();
    }

    #endregion

    #region Full File Builder

    private record SectionData(byte[][] Packages, int NoteCount);

    private static byte[] BuildOjn(
        float bpm, string title, string artist, string noter,
        short[] levels,
        byte[][][] sectionPackages)  // [3][packageCount][packageBytes]
    {
        // Flatten package bytes per section
        var sectionBytes = new byte[3][];
        var packageCounts = new int[3];
        var noteCounts = new int[3];

        for (var i = 0; i < 3; i++)
        {
            using var sms = new MemoryStream();
            foreach (var pkg in sectionPackages[i])
                sms.Write(pkg);
            sectionBytes[i] = sms.ToArray();
            packageCounts[i] = sectionPackages[i].Length;
            noteCounts[i] = levels.Length > i ? (i < levels.Length ? CountNotes(sectionPackages[i]) : 0) : 0;
        }

        const int headerSize = 300;
        var noteOffsets = new int[3];
        noteOffsets[0] = headerSize;
        noteOffsets[1] = noteOffsets[0] + sectionBytes[0].Length;
        noteOffsets[2] = noteOffsets[1] + sectionBytes[1].Length;
        var coverOffset = noteOffsets[2] + sectionBytes[2].Length;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // --- Header (300 bytes) ---
        w.Write(1);                                             // song_id
        w.Write("ojn\0"u8);                                    // signature
        w.Write(2.9f);                                          // encode_version
        w.Write(0);                                             // genre
        w.Write(bpm);                                           // bpm
        
        for (var i = 0; i < 4; i++)                             // level[4]
            w.Write(i < levels.Length ? levels[i] : (short)0);
        
        for (var i = 0; i < 3; i++) w.Write(0);                 // event_count[3] (informational)
        for (var i = 0; i < 3; i++) w.Write(noteCounts[i]);     // note_count[3]
        for (var i = 0; i < 3; i++) w.Write(0);                 // measure_count[3] (informational)
        for (var i = 0; i < 3; i++) w.Write(packageCounts[i]);  // package_count[3]
        w.Write((short)29);                                     // old_encode_version
        w.Write((short)0);                                      // old_song_id
        w.Write(new byte[20]);                            // old_genre
        w.Write(0);                                             // bmp_size
        w.Write(0);                                             // old_file_version
        WriteFixed(w, title, 64);                               // title
        WriteFixed(w, artist, 32);                              // artist
        WriteFixed(w, noter, 32);                               // noter
        WriteFixed(w, "test.ojm", 32);                          // ojm_file
        w.Write(0);                                             // cover_size
        w.Write(120); w.Write(120); w.Write(120);               // time[3]
        foreach (var o in noteOffsets) 
            w.Write(o);                                         // note_offset[3]
        w.Write(coverOffset);                                   // cover_offset

        // --- Note sections ---
        for (var i = 0; i < 3; i++) w.Write(sectionBytes[i]);

        return ms.ToArray();
    }

    private static void WriteFixed(BinaryWriter w, string s, int size)
    {
        var buf = new byte[size];
        var bytes = Encoding.UTF8.GetBytes(s ?? "");
        Array.Copy(bytes, buf, Math.Min(bytes.Length, size));
        w.Write(buf);
    }

    /// <summary>Count non-padding note events in channels 2-8 from raw package bytes.</summary>
    private static int CountNotes(byte[][] packages)
    {
        var count = 0;
        foreach (var pkg in packages)
        {
            using var ms = new MemoryStream(pkg);
            using var r = new BinaryReader(ms);
            _ = r.ReadInt32();
            var channel = r.ReadInt16();
            var events = r.ReadInt16();
            if (channel is < 2 or > 8) continue;
            for (var i = 0; i < events; i++)
            {
                var value = r.ReadInt16();
                _ = r.ReadByte();
                _ = r.ReadByte();
                if (value != 0) count++;
            }
        }

        return count;
    }

    #endregion

    #region Shared Note Packages

    /// <summary>
    /// 4 normal notes on lanes 0-3, one per beat in measure 0.
    /// </summary>
    private static byte[][] FourNotePackages =>
    [
        BuildPackage(0, 2, [NoteEvt(1), Pad(), Pad(), Pad()]),          // lane 0, beat 0
        BuildPackage(0, 3, [Pad(), NoteEvt(2), Pad(), Pad()]),          // lane 1, beat 1
        BuildPackage(0, 4, [Pad(), Pad(), NoteEvt(3), Pad()]),          // lane 2, beat 2
        BuildPackage(0, 5, [Pad(), Pad(), Pad(), NoteEvt(4)])           // lane 3, beat 3
    ];

    #endregion

    #region Test Data

    /// <summary>
    /// Minimal 7K chart: 4 normal notes at BPM 180, only HX has notes.
    /// </summary>
    public static byte[] Minimal7K => BuildOjn(
        bpm: 180f,
        title: "Test O2Jam",
        artist: "Test Artist",
        noter: "TestNoter",
        levels: [1, 5, 10],
        sectionPackages:
        [
            [],                 // EX: empty
            [],                 // NX: empty
            FourNotePackages    // HX: 4 notes
        ]);

    /// <summary>
    /// All three difficulties have notes.
    /// </summary>
    public static byte[] ThreeDifficulties => BuildOjn(
        bpm: 150f,
        title: "Triple",
        artist: "Artist",
        noter: "Noter",
        levels: [3, 7, 12],
        sectionPackages:
        [
            [BuildPackage(0, 2, [NoteEvt(1)])],                 // EX: 1 note
            [BuildPackage(0, 2, [NoteEvt(1), NoteEvt(2)])],     // NX: 2 notes
            FourNotePackages                                     // HX: 4 notes
        ]);

    /// <summary>
    /// BPM change on channel 1 at measure 1.
    /// </summary>
    public static byte[] WithBpmChange => BuildOjn(
        bpm: 120f,
        title: "BPM Test",
        artist: "Artist",
        noter: "Noter",
        levels: [1, 5, 10],
        sectionPackages:
        [
            [],
            [],
            [
                BuildPackage(0, 2, [NoteEvt(1), Pad(), Pad(), Pad()]),  // note at beat 0
                BuildPackage(1, 1, [FloatEvt(200f)]),                    // BPM → 200 at measure 1
                BuildPackage(1, 2, [NoteEvt(2), Pad(), Pad(), Pad()])   // note at measure 1 beat 0
            ]
        ]);

    /// <summary>
    /// Long note: LN start (type 2) and LN end (type 3) on lane 0.
    /// </summary>
    public static byte[] WithLongNote => BuildOjn(
        bpm: 180f,
        title: "LN Test",
        artist: "Artist",
        noter: "Noter",
        levels: [1, 5, 10],
        sectionPackages:
        [
            [],
            [],
            [
                BuildPackage(0, 2, [NoteEvt(1, 2), Pad(), Pad(), Pad()]),   // LN start at beat 0
                BuildPackage(0, 2, [Pad(), Pad(), NoteEvt(1, 3), Pad()])    // LN end at beat 2
            ]
        ]);

    /// <summary>
    /// Measure fraction 0.75 on channel 0 → 3/4 time.
    /// </summary>
    public static byte[] MeasureFraction34 => BuildOjn(
        bpm: 120f,
        title: "Fraction Test",
        artist: "Artist",
        noter: "Noter",
        levels: [1, 5, 10],
        sectionPackages:
        [
            [],
            [],
            [
                BuildPackage(0, 0, [FloatEvt(0.75f)]),                      // measure 0 = 3/4
                BuildPackage(0, 2, [NoteEvt(1), Pad(), Pad()]),             // note at beat 0
                BuildPackage(1, 2, [NoteEvt(2)])                            // note at measure 1
            ]
        ]);

    /// <summary>
    /// Invalid signature bytes.
    /// </summary>
    public static byte[] InvalidSignature
    {
        get
        {
            var data = Minimal7K;
            data[4] = (byte)'x'; // corrupt signature
            return data;
        }
    }

    #endregion
}
