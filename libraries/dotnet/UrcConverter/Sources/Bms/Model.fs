namespace UrcConverter.Sources.Bms

module Model =

    type BmsChart =
        {
            Pms: bool
            Base: int
            Title: string option
            Artist: string option
            PlayLevel: string option
            Bpm: float option
            LnType: int
            LnObj: string option
            BpmDefs: Map<string, float>
            StopDefs: Map<string, float>
            ScrollDefs: Map<string, float>
            Rates: Map<int, float>
            Measures: Map<int, Map<string, string list>>
        }
