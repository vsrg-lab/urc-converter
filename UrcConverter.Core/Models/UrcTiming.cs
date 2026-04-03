namespace UrcConverter.Core.Models;

public record UrcTiming(
    int Timestamp,
    double Bpm,
    string Meter,
    double Multiplier = 1.0
);
