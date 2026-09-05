namespace UrcConverter.Sources.Sm

module Model =

    /// Timing segments in effect at the song level or for one chart.
    type Timing =
        {
            Offset: float
            Bpms: (float * float) list
            Stops: (float * float) list
            Delays: (float * float) list
            Warps: (float * float) list
            Scrolls: (float * float) list
            TimeSignatures: (float * int * int) list
            Fakes: (float * float) list
        }

        static member Empty =
            {
                Offset = 0.0
                Bpms = []
                Stops = []
                Delays = []
                Warps = []
                Scrolls = []
                TimeSignatures = []
                Fakes = []
            }

    /// Kind of a note head in the note data.
    type NoteKind =
        | Tap
        | Hold
        | Roll
        | Mine
        | Lift
        | FakeNote

    /// One note head; hold/roll pairs carry the tail row.
    type SmNote =
        {
            Row: int
            Track: int
            Kind: NoteKind
            TailRow: int option
        }

    /// One chart block (#NOTES in .sm, #NOTEDATA in .ssc).
    type SmChart =
        {
            StepsType: string
            Description: string
            Difficulty: string
            ChartName: string
            Credit: string
            /// None: inherit the song-level timing.
            Timing: Timing option
            Notes: SmNote list
        }

        static member Empty =
            {
                StepsType = ""
                Description = ""
                Difficulty = ""
                ChartName = ""
                Credit = ""
                Timing = None
                Notes = []
            }

    /// Parsed simfile: song metadata, song timing, and charts.
    type SmFile =
        {
            Title: string
            Subtitle: string
            Artist: string
            Credit: string
            Timing: Timing
            Charts: SmChart list
        }

        static member Empty =
            {
                Title = ""
                Subtitle = ""
                Artist = ""
                Credit = ""
                Timing = Timing.Empty
                Charts = []
            }
