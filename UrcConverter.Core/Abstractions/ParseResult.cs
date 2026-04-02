using UrcConverter.Core.Models;

namespace UrcConverter.Core.Abstractions;

public abstract record ParseResult
{
    public sealed record Success(UrcChart Chart) : ParseResult;

    public sealed record Failure(string Error) : ParseResult;
}
