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

    [<RequireQualifiedAccess>]
    type SpecialField =
        | Unseen
        | NoneExplicit
        | Lanes of int list

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
            Metadata: Map<string, string>
            ValidatedMetadata: Metadata option
            Windows: float list option
            Rates: float list option
            ValidatedJudgment: Judgment option
            LayoutType: (int * int) option
            Special: SpecialField
            ValidatedLayout: Layout option
            TimingPoints: TimingPoint list
            Notes: RawNote list
        }

    let initial =
        {
            Seen = Set.ofList [ Strings.sectionUrc ]
            LastIndex = 0
            Version = { Major = 1; Minor = 1 }
            Metadata = Map.empty
            ValidatedMetadata = None
            Windows = None
            Rates = None
            ValidatedJudgment = None
            LayoutType = None
            Special = SpecialField.Unseen
            ValidatedLayout = None
            TimingPoints = []
            Notes = []
        }
