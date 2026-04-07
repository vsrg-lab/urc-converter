namespace UrcConverter.Tests.Fixtures;

public sealed class OsuFileFixture : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public string CreateTempOsu(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.osu");
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

    // Pre-built .osu content
    
    /// <summary>
    /// Minimal valid osu!mania 4K chart with 3 normal notes and 1 hold note.
    /// BPM 180, OD 8.
    /// </summary>
    public static string Minimal4K =>
        """
        osu file format v14

        [General]
        Mode: 3

        [Metadata]
        Title:Test Song
        TitleUnicode:テスト曲
        Artist:Test Artist
        ArtistUnicode:テストアーティスト
        Creator:TestMapper
        Version:Hard

        [Difficulty]
        CircleSize:4
        OverallDifficulty:8

        [TimingPoints]
        0,333.333333333333,4,1,0,100,1,0

        [HitObjects]
        64,192,1000,1,0,0:0:0:0:
        192,192,1500,1,0,0:0:0:0:
        320,192,2000,128,0,3000:0:0:0:0:
        448,192,2500,1,0,0:0:0:0:
        """;

    /// <summary>
    /// Chart with simultaneous BPM change (red line) and SV change (green line)
    /// at timestamp 5000.
    /// BPM: 150 → 180, SV: 0.8× at the same point.
    /// </summary>
    public static string SimultaneousBpmAndSv =>
        """
        osu file format v14

        [General]
        Mode: 3

        [Metadata]
        Title:SV Test
        Artist:Test
        Creator:Test
        Version:Test

        [Difficulty]
        CircleSize:4
        OverallDifficulty:5

        [TimingPoints]
        0,400,4,1,0,100,1,0
        5000,333.333333333333,4,1,0,100,1,0
        5000,-125,4,1,0,100,0,0

        [HitObjects]
        64,192,1000,1,0,0:0:0:0:
        """;

    /// <summary>
    /// Chart with multiple SV changes (green lines only, no BPM change after initial).
    /// </summary>
    public static string MultipleSvChanges =>
        """
        osu file format v14

        [General]
        Mode: 3

        [Metadata]
        Title:Multi SV
        Artist:Test
        Creator:Test
        Version:Test

        [Difficulty]
        CircleSize:7
        OverallDifficulty:9

        [TimingPoints]
        0,333.333333333333,4,1,0,100,1,0
        2000,-200,4,1,0,100,0,0
        4000,-50,4,1,0,100,0,0
        6000,-100,4,1,0,100,0,0

        [HitObjects]
        64,192,500,1,0,0:0:0:0:
        """;

    /// <summary>
    /// A standard (non-mania) osu! chart — Mode: 0. Should be rejected.
    /// </summary>
    public static string StandardMode =>
        """
        osu file format v14

        [General]
        Mode: 0

        [Metadata]
        Title:Not Mania
        Artist:Test
        Creator:Test
        Version:Normal

        [Difficulty]
        CircleSize:4
        OverallDifficulty:5

        [TimingPoints]
        0,500,4,1,0,100,1,0

        [HitObjects]
        256,192,1000,1,0,0:0:0:0:
        """;
}