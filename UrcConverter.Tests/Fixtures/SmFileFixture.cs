namespace UrcConverter.Tests.Fixtures;

public sealed class SmFileFixture : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public string CreateTempSm(string content, string ext = ".sm")
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{ext}");
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

    // Pre-built .sm content

    /// <summary>
    /// Minimal .sm file: dance-single (4K), BPM 180, 4 taps across 2 measures.
    /// </summary>
    public static string Minimal4K => 
        """
        #TITLE:Test Song;
        #SUBTITLE:;
        #ARTIST:Test Artist;
        #CREDIT:TestMapper;
        #BPMS:0.000=180.000;
        #STOPS:;
         
        #NOTES:
             dance-single:
             :
             Hard:
             8:
             0.0,0.0,0.0,0.0,0.0:
        1000
        0100
        0010
        0001
        ,
        0000
        0000
        0000
        0000
        ;
        """;

    /// <summary>
    /// .sm file with a hold note (2 = hold head, 3 = tail).
    /// </summary>
    public static string WithHoldNote => 
        """
        #TITLE:Hold Test;
        #ARTIST:Test;
        #CREDIT:Test;
        #BPMS:0.000=150.000;
        #STOPS:;
         
        #NOTES:
             dance-single:
             :
             Challenge:
             10:
             0.0,0.0,0.0,0.0,0.0:
        2000
        0000
        0000
        3000
        ,
        0000
        0100
        0000
        0000
        ;
        """;

    /// <summary>
    /// .sm file with BPM change and STOP.
    /// </summary>
    public static string BpmChangeAndStop => 
        """
        #TITLE:BPM+Stop;
        #ARTIST:Test;
        #CREDIT:Test;
        #BPMS:0.000=120.000,4.000=180.000;
        #STOPS:4.000=0.500;
         
        #NOTES:
             dance-single:
             :
             Hard:
             7:
             0.0,0.0,0.0,0.0,0.0:
        1000
        0000
        0000
        0000
        ,
        0001
        0000
        0000
        0000
        ;
        """;

    /// <summary>
    /// .sm file with two charts (Easy and Challenge). Parser should parse all of them.
    /// </summary>
    public static string MultipleCharts => 
        """
        #TITLE:Multi Chart;
        #ARTIST:Test;
        #CREDIT:Test;
        #BPMS:0.000=160.000;
        #STOPS:;
         
        #NOTES:
             dance-single:
             :
             Easy:
             3:
             0.0,0.0,0.0,0.0,0.0:
        1000
        0000
        0000
        0000
        ;
         
        #NOTES:
             dance-single:
             :
             Challenge:
             12:
             0.0,0.0,0.0,0.0,0.0:
        1010
        0101
        1010
        0101
        ;
        """;

    /// <summary>
    /// .sm file with mine and fake notes.
    /// </summary>
    public static string MinesAndFakes => 
        """
        #TITLE:Mine Test;
        #ARTIST:Test;
        #CREDIT:Test;
        #BPMS:0.000=120.000;
        #STOPS:;
         
        #NOTES:
             dance-single:
             :
             Hard:
             6:
             0.0,0.0,0.0,0.0,0.0:
        1000
        0M00
        00F0
        0001
        ;
        """;

    /// <summary>
    /// .sm file with pump-single (5K).
    /// </summary>
    public static string PumpSingle5K => 
        """
        #TITLE:Pump Test;
        #ARTIST:Test;
        #CREDIT:Test;
        #BPMS:0.000=150.000;
        #STOPS:;
         
        #NOTES:
             pump-single:
             :
             Hard:
             8:
             0.0,0.0,0.0,0.0,0.0:
        10000
        01000
        00100
        00010
        ;
        """;

    // Pre-built .ssc content

    /// <summary>
    /// Minimal .ssc file with per-chart SCROLLS.
    /// </summary>
    public static string SscWithScrolls => 
        """
        #VERSION:0.83;
        #TITLE:SSC Test;
        #ARTIST:Test;
        #CREDIT:Test;
        #BPMS:0.000=160.000;
        #STOPS:;
         
        #NOTEDATA:;
        #STEPSTYPE:dance-single;
        #DESCRIPTION:;
        #DIFFICULTY:Challenge;
        #METER:10;
        #SCROLLS:0.000=1.000,4.000=0.5;
        #NOTES:
        1000
        0100
        0010
        0001
        ,
        1000
        0000
        0000
        0000
        ;
        """;

    /// <summary>
    /// Invalid content — should fail.
    /// </summary>
    public static string InvalidContent =>
        """
        This is not a StepMania file at all.
        Just some random text without tags.
        """;
}
