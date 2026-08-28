namespace UrcConverter

/// Parse failure with a machine-readable category and source line.
type UrcError =
    {
        Category: string
        Line: int
        Message: string
    }

    override this.ToString() =
        $"{this.Category} at line {this.Line}: {this.Message}"

    static member Syntax(line: int, message: string) =
        {
            Category = Strings.syntax
            Line = line
            Message = message
        }

    static member Rule(number: int, line: int, message: string) =
        {
            Category = Strings.rule number
            Line = line
            Message = message
        }

    static member UnsupportedVersion(line: int, message: string) =
        {
            Category = Strings.unsupportedVersion
            Line = line
            Message = message
        }
