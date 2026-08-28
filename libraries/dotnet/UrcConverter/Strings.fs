namespace UrcConverter

/// Shared URC vocabulary.
module Strings =

    let syntax = "syntax"
    let unsupportedVersion = "unsupported-version"

    let sections = [ "@URC"; "@Metadata"; "@Judgment"; "@Layout"; "@Timing"; "@Notes" ]
    let sectionUrc = "@URC"
    let sectionMetadata = "@Metadata"
    let sectionJudgment = "@Judgment"
    let sectionLayout = "@Layout"
    let sectionTiming = "@Timing"
    let sectionNotes = "@Notes"

    let requiredSections =
        [ sectionMetadata; sectionLayout; sectionTiming; sectionNotes ]

    let metadataFields = [ "Original"; "Title"; "Artist"; "Creator"; "Version" ]
    let judgmentFields = [ "Window"; "Rate" ]
    let layoutFields = [ "Type"; "Special" ]
    let judgmentFieldWindow = "Window"
    let judgmentFieldRate = "Rate"
    let layoutFieldType = "Type"
    let layoutFieldSpecial = "Special"
    let noteTokens = [ "N"; "LS"; "LE"; "M"; "F" ]
    let specialNone = "None"

    let ruleDescriptions =
        Map.ofList
            [
                1, "First line is '@URC <version>'"
                2, "All required sections present"
                3, "Sections in correct order"
                4, "All required fields present"
                5, "All required metadata fields have values"
                6, "Field names are valid"
                7, "Window and Rate have same count"
                8, "Window values ascending"
                9, "Rate values descending"
                10, "Rate values in range 0-100"
                11, "Type matches note lane count (enforced as rule 18)"
                12, "Special lanes are valid indices"
                13, "No duplicate special lanes"
                14, "First timing point at timestamp 0"
                15, "Timestamps ascending"
                16, "BPM positive"
                17, "Valid meter format"
                18, "All lanes valid (0 to key_count-1)"
                19, "Valid note types"
                20, "LS/LE properly paired"
                21, "No overlapping long notes on same lane"
                22, "Timestamps non-negative"
            ]

    /// Category string for spec validation rule `number` (1-22).
    let rule number = $"rule:{number}"
