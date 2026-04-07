namespace UrcConverter.Tests.Fixtures;

public sealed class QuaFileFixture : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public string CreateTempQua(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.qua");
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

    // Pre-built .qua content

    /// <summary>
    /// Minimal valid 4K chart: BPM 180, 4 normal notes.
    /// </summary>
    public static string Minimal4K => 
        """
        AudioFile: test.mp3
        Title: Test Song
        Artist: Test Artist
        Creator: TestMapper
        DifficultyName: Hard
        Mode: Keys4
        TimingPoints:
        - StartTime: 0
          Bpm: 180
          Signature: 4
        HitObjects:
        - StartTime: 1000
          Lane: 1
          EndTime: 0
        - StartTime: 1500
          Lane: 2
          EndTime: 0
        - StartTime: 2000
          Lane: 3
          EndTime: 0
        - StartTime: 2500
          Lane: 4
          EndTime: 0
        """;

    /// <summary>
    /// 7K chart.
    /// </summary>
    public static string SevenKey => 
        """
        AudioFile: test.mp3
        Title: 7K Test
        Artist: Test
        Creator: Test
        DifficultyName: Expert
        Mode: Keys7
        TimingPoints:
        - StartTime: 0
          Bpm: 150
          Signature: 4
        HitObjects:
        - StartTime: 1000
          Lane: 1
          EndTime: 0
        - StartTime: 1000
          Lane: 7
          EndTime: 0
        """;

    /// <summary>
    /// Chart with a hold note (EndTime > 0).
    /// </summary>
    public static string WithHoldNote => 
        """
        AudioFile: test.mp3
        Title: Hold Test
        Artist: Test
        Creator: Test
        DifficultyName: Hard
        Mode: Keys4
        TimingPoints:
        - StartTime: 0
          Bpm: 150
          Signature: 4
        HitObjects:
        - StartTime: 1000
          Lane: 1
          EndTime: 2000
        - StartTime: 1500
          Lane: 3
          EndTime: 0
        """;

    /// <summary>
    /// Chart with BPM change and slider velocity.
    /// </summary>
    public static string BpmChangeAndSv => 
        """
        AudioFile: test.mp3
        Title: BPM+SV Test
        Artist: Test
        Creator: Test
        DifficultyName: Hard
        Mode: Keys4
        TimingPoints:
        - StartTime: 0
          Bpm: 120
          Signature: 4
        - StartTime: 5000
          Bpm: 180
          Signature: 4
        SliderVelocities:
        - StartTime: 5000
          Multiplier: 0.8
        HitObjects:
        - StartTime: 1000
          Lane: 1
          EndTime: 0
        - StartTime: 6000
          Lane: 2
          EndTime: 0
        """;

    /// <summary>
    /// Chart with 3/4 time signature.
    /// </summary>
    public static string ThreeFourTime => 
        """
        AudioFile: test.mp3
        Title: 3/4 Test
        Artist: Test
        Creator: Test
        DifficultyName: Waltz
        Mode: Keys4
        TimingPoints:
        - StartTime: 0
          Bpm: 120
          Signature: 3
        HitObjects:
        - StartTime: 1000
          Lane: 1
          EndTime: 0
        """;

    /// <summary>
    /// Chart with HasScratchKey = true.
    /// </summary>
    public static string WithScratchKey => 
        """
        AudioFile: test.mp3
        Title: Scratch Test
        Artist: Test
        Creator: Test
        DifficultyName: Hard
        Mode: Keys4
        HasScratchKey: true
        TimingPoints:
        - StartTime: 0
          Bpm: 150
          Signature: 4
        HitObjects:
        - StartTime: 1000
          Lane: 1
          EndTime: 0
        """;

    /// <summary>
    /// Invalid content — should fail.
    /// </summary>
    public static string InvalidContent => 
        """
        This is not valid YAML for Quaver.
        No TimingPoints or HitObjects here.
        """;
}