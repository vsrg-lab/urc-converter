namespace UrcConverter

/// Machine-readable error category for URC parsing, conversion, and rate scaling.
[<RequireQualifiedAccess>]
type ErrorCategory =
    | Syntax
    | UnsupportedVersion
    | ZeroLengthHold
    | InvalidRate
    | Rule of number: int

    member this.Code =
        match this with
        | ErrorCategory.Syntax -> Strings.syntax
        | ErrorCategory.UnsupportedVersion -> Strings.unsupportedVersion
        | ErrorCategory.ZeroLengthHold -> Strings.zeroLengthHold
        | ErrorCategory.InvalidRate -> Strings.invalidRate
        | ErrorCategory.Rule number -> Strings.rule number

/// Structured failure with a machine-readable category and 1-based source line (or byte offset).
type UrcError =
    {
        CategoryKind: ErrorCategory
        Line: int
        Message: string
    }

    member this.Category = this.CategoryKind.Code

    override this.ToString() =
        $"{this.Category} at line {this.Line}: {this.Message}"

    static member Syntax(line: int, message: string) =
        {
            CategoryKind = ErrorCategory.Syntax
            Line = line
            Message = message
        }

    static member Rule(number: int, line: int, message: string) =
        {
            CategoryKind = ErrorCategory.Rule number
            Line = line
            Message = message
        }

    static member UnsupportedVersion(line: int, message: string) =
        {
            CategoryKind = ErrorCategory.UnsupportedVersion
            Line = line
            Message = message
        }

    static member ZeroLengthHold(line: int, message: string) =
        {
            CategoryKind = ErrorCategory.ZeroLengthHold
            Line = line
            Message = message
        }

    static member InvalidRate(line: int, message: string) =
        {
            CategoryKind = ErrorCategory.InvalidRate
            Line = line
            Message = message
        }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module UrcError =

    let syntax (line: int) (message: string) : UrcError =
        UrcError.Syntax(line, message)

    let rule (number: int) (line: int) (message: string) : UrcError =
        UrcError.Rule(number, line, message)

    let unsupportedVersion (line: int) (message: string) : UrcError =
        UrcError.UnsupportedVersion(line, message)

    let zeroLengthHold (line: int) (message: string) : UrcError =
        UrcError.ZeroLengthHold(line, message)

    let invalidRate (line: int) (message: string) : UrcError =
        UrcError.InvalidRate(line, message)
