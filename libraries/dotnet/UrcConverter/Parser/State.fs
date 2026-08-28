namespace UrcConverter.Parser

open UrcConverter

module internal State =

    [<RequireQualifiedAccess>]
    type Section =
        | UrcHeader
        | Metadata
        | Judgment
        | Layout
        | Timing
        | Notes

    let private allSections =
        [
            Section.UrcHeader
            Section.Metadata
            Section.Judgment
            Section.Layout
            Section.Timing
            Section.Notes
        ]

    let byName =
        List.zip Strings.sections allSections
        |> List.indexed
        |> List.map (fun (index, (name, section)) -> name, (section, index))
        |> Map.ofList

    type RawNote =
        {
            Timestamp: int
            Lane: int
            TypeToken: string
            Line: int
        }

    // TimingPoints and Notes accumulate reversed; build reverses them back.
    type ParseState =
        {
            Seen: Set<string>
            LastIndex: int
            Version: Version
            Metadata: (string * string) list
            Windows: float list option
            Rates: float list option
            LayoutType: (int * int) option
            Special: int list option
            SpecialSeen: bool
            TimingPoints: TimingPoint list
            Notes: RawNote list
        }

    let initial =
        {
            Seen = Set.ofList [ Strings.sectionUrc ]
            LastIndex = 0
            Version = { Major = 1; Minor = 1 }
            Metadata = []
            Windows = None
            Rates = None
            LayoutType = None
            Special = None
            SpecialSeen = false
            TimingPoints = []
            Notes = []
        }
