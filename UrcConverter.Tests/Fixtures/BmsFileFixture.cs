namespace UrcConverter.Tests.Fixtures;

public sealed class BmsFileFixture : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public string CreateTempBms(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.bms");
        File.WriteAllText(path, content);
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

    // Pre-built .bms content

    /// <summary>
    /// Minimal valid 7K BMS chart: BPM 180, 4 notes across channels 11~15, 18~19.
    /// </summary>
    public static string Minimal7K =>
        """
        #PLAYER 1
        #TITLE Test BMS
        #ARTIST Test Artist
        #BPM 180
        #PLAYLEVEL 7
        #RANK 2
 
        #00011:01000000
        #00012:00010000
        #00018:00000100
        #00019:00000001
        """;

    /// <summary>
    /// 5K+Scratch chart: notes on channels 11-15 and scratch (16).
    /// </summary>
    public static string FiveKeyWithScratch =>
        """
        #PLAYER 1
        #TITLE 5K+SC
        #ARTIST Test
        #BPM 150
        #PLAYLEVEL 5
        #RANK 2
 
        #00011:01000000
        #00012:00010000
        #00013:00000100
        #00016:00000001
        """;

    /// <summary>
    /// Chart with simultaneous BPM change and SV (scroll) change.
    /// BPM 150 → 180 at measure 1, with SCROLL 0.8× at the same point.
    /// </summary>
    public static string BpmAndScrollChange =>
        """
        #PLAYER 1
        #TITLE BPM+Scroll
        #ARTIST Test
        #BPM 150
        #PLAYLEVEL 5
        #RANK 2
        #SCROLL01 0.8
 
        #00011:01000000
        #00103:B4
        #001SC:01
        #00111:01000000
        """;

    /// <summary>
    /// Chart with extended BPM (channel 08).
    /// </summary>
    public static string ExtBpm =>
        """
        #PLAYER 1
        #TITLE ExtBPM Test
        #ARTIST Test
        #BPM 120
        #BPM01 200
        #PLAYLEVEL 3
        #RANK 2

        #00011:01000000
        #00008:01
        #00111:01000000
        """;

    /// <summary>
    /// Chart with STOP (channel 09).
    /// </summary>
    public static string StopAddsTime =>
        """
        #PLAYER 1
        #TITLE Stop Test
        #ARTIST Test
        #BPM 120
        #STOP01 48
        #PLAYLEVEL 3
        #RANK 2

        #00011:01000000
        #00009:01
        #00111:01000000
        """;

    /// <summary>
    /// Chart with LNTYPE 1: long notes on channels 51-59.
    /// </summary>
    public static string LnType1 =>
        """
        #PLAYER 1
        #TITLE LN Test
        #ARTIST Test
        #BPM 150
        #PLAYLEVEL 8
        #RANK 2
        #LNTYPE 1
 
        #00011:01000000
        #00051:01000001
        """;

    /// <summary>
    /// Chart with LNOBJ: object ID ZZ marks LN end.
    /// </summary>
    public static string LnObj =>
        """
        #PLAYER 1
        #TITLE LNOBJ Test
        #ARTIST Test
        #BPM 150
        #PLAYLEVEL 8
        #RANK 2
        #LNOBJ ZZ
 
        #00011:010000ZZ
        """;

    /// <summary>
    /// Chart with measure length 0.75 (3/4 time) in measure 0.
    /// </summary>
    public static string MeasureLength34 =>
        """
        #PLAYER 1
        #TITLE 3/4 Test
        #ARTIST Test
        #BPM 120
        #PLAYLEVEL 3
        #RANK 2
 
        #00002:0.75
        #00011:010100
        #00111:01000000
        """;

    /// <summary>
    /// Chart with measure length 0.875 (7/8 time) in measure 1.
    /// </summary>
    public static string MeasureLength78 =>
        """
        #PLAYER 1
        #TITLE 7/8 Test
        #ARTIST Test
        #BPM 120
        #PLAYLEVEL 3
        #RANK 2
 
        #00011:01000000
        #00102:0.875
        #00111:01000000
        """;

    /// <summary>
    /// Non-BMS content — should fail.
    /// </summary>
    public static string InvalidContent =>
        """
        This is not a BMS file at all.
        Just some random text.
        """;
}
