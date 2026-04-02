namespace UrcConverter.Core.Models;

public record UrcLayout(int KeyCount, int SpecialKeyCount, IReadOnlyList<int> SpecialLanes);
