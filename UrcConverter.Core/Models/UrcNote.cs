using UrcConverter.Core.Models.Enums;

namespace UrcConverter.Core.Models;

public record UrcNote(int Timestamp, int Lane, NoteType Type);
