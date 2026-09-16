using System.IO;
using System.Text;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using UrcConverter.Gui.Models;

namespace UrcConverter.Gui.Services;

public enum SourceKind
{
	Osu,
	Qua,
	Bms,
	Sm,
	Ojn,
	Urc,
}

/// <summary>Branch-expansion options; only meaningful for BMS sources.</summary>
public sealed record BmsOptions(long? Seed, IReadOnlyList<ulong>? Branches)
{
	public static BmsOptions Default { get; } = new(null, null);
}

public sealed record StructuredError(string Category, int Line, string Message)
{
	public override string ToString() => $"{Category} at line {Line}: {Message}";
}

public sealed record ChartSummary(
	string FormatVersion,
	string Title,
	string Artist,
	string Creator,
	string ChartVersion,
	string Original,
	int Keys,
	int SpecialKeys,
	int TimingPoints,
	int NotesNormal,
	int NotesHoldStart,
	int NotesHoldEnd,
	int NotesMine,
	int NotesFake)
{
	public int TotalNotes => NotesNormal + NotesHoldStart + NotesHoldEnd + NotesMine + NotesFake;

	public override string ToString()
	{
		string layout = SpecialKeys > 0 ? $"{Keys}+{SpecialKeys} keys" : $"{Keys} keys";
		return string.Join("\n",
			$"URC {FormatVersion}",
			$"Title:    {Title}",
			$"Artist:   {Artist}",
			$"Creator:  {Creator}",
			$"Chart:    {ChartVersion}",
			$"Original: {Original}",
			$"Layout:   {layout}",
			$"Timing:   {TimingPoints} points",
			$"Notes:    {TotalNotes} (N: {NotesNormal}, LS: {NotesHoldStart}, LE: {NotesHoldEnd}, M: {NotesMine}, F: {NotesFake})");
	}
}

public sealed record LoadedChart(ChartItem Item, ChartSummary Summary, string UrcText);

/// <summary>Everything produced by loading one file. For URC sources,
/// <see cref="SourceText"/> holds the original file and feeds the preview.</summary>
public sealed record LoadResult(SourceKind Kind, string SourceText, IReadOnlyList<LoadedChart> Charts);

public abstract record Outcome<T>
{
	private Outcome()
	{
	}

	public sealed record Ok(T Value) : Outcome<T>;

	public sealed record Fail(StructuredError Error) : Outcome<T>;
}

public static class ConverterService
{
	public static SourceKind? DetectKind(string path)
	{
		return Path.GetExtension(path).ToLowerInvariant() switch
		{
			".osu" => SourceKind.Osu,
			".qua" => SourceKind.Qua,
			".bms" or ".bme" or ".bml" or ".pms" => SourceKind.Bms,
			".sm" or ".ssc" => SourceKind.Sm,
			".ojn" => SourceKind.Ojn,
			".urc" => SourceKind.Urc,
			_ => null,
		};
	}

	public static Outcome<LoadResult> Load(string path, BmsOptions bms)
	{
		var kind = DetectKind(path);
		if (kind is null)
		{
			return new Outcome<LoadResult>.Fail(new StructuredError(
				"unsupported", 0, $"unsupported file type: {Path.GetFileName(path)}"));
		}

		try
		{
			return kind.Value switch
			{
				SourceKind.Osu => ConvertSingle(
					kind.Value, Sources.Osu.Parse.parse(ReadText(path)), Sources.Osu.Convert.convert),
				SourceKind.Qua => ConvertSingle(
					kind.Value, Sources.Qua.Parse.parse(ReadText(path)), Sources.Qua.Convert.convert),
				SourceKind.Bms => ConvertSingle(
					kind.Value,
					Sources.Bms.Parse.parseBms(File.ReadAllBytes(path), ToParseOptions(path, bms)),
					Sources.Bms.Convert.convertBms),
				SourceKind.Sm => ConvertList(
					kind.Value, Sources.Sm.Parse.parseSm(ReadText(path)), Sources.Sm.Convert.convertSm),
				SourceKind.Ojn => ConvertList(
					kind.Value,
					Sources.Ojn.Parse.parseOjn(File.ReadAllBytes(path)),
					Sources.Ojn.Convert.convertOjn),
				SourceKind.Urc => LoadUrc(path),
				_ => throw new InvalidOperationException($"unhandled kind {kind}"),
			};
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return new Outcome<LoadResult>.Fail(new StructuredError("io", 0, ex.Message));
		}
	}

	private static string ReadText(string path) => File.ReadAllText(path, Encoding.UTF8);

	private static Sources.Bms.Parse.ParseOptions ToParseOptions(string path, BmsOptions bms)
	{
		var pms = Path.GetExtension(path).ToLowerInvariant() == ".pms";
		var seed = bms.Seed is long s ? new FSharpOption<long>(s) : null;
		var branches = bms.Branches is null
			? null
			: new FSharpOption<FSharpList<ulong>>(ListModule.OfSeq(bms.Branches));
		return new Sources.Bms.Parse.ParseOptions(pms, seed, branches);
	}

	private static Outcome<LoadResult> ConvertSingle<TSource>(
		SourceKind kind,
		FSharpResult<TSource, UrcError> parsed,
		Func<TSource, FSharpResult<Chart, UrcError>> convert)
	{
		if (!parsed.IsOk)
		{
			return new Outcome<LoadResult>.Fail(ToError(parsed.ErrorValue));
		}

		var converted = convert(parsed.ResultValue);
		if (!converted.IsOk)
		{
			return new Outcome<LoadResult>.Fail(ToError(converted.ErrorValue));
		}

		return new Outcome<LoadResult>.Ok(new LoadResult(kind, "", [ToLoaded(0, converted.ResultValue)]));
	}

	private static Outcome<LoadResult> ConvertList<TSource>(
		SourceKind kind,
		FSharpResult<TSource, UrcError> parsed,
		Func<TSource, FSharpResult<FSharpList<Chart>, UrcError>> convert)
	{
		if (!parsed.IsOk)
		{
			return new Outcome<LoadResult>.Fail(ToError(parsed.ErrorValue));
		}

		var converted = convert(parsed.ResultValue);
		if (!converted.IsOk)
		{
			return new Outcome<LoadResult>.Fail(ToError(converted.ErrorValue));
		}

		return new Outcome<LoadResult>.Ok(new LoadResult(
			kind,
			"",
			converted.ResultValue.Select((chart, index) => ToLoaded(index, chart)).ToArray()));
	}

	private static Outcome<LoadResult> LoadUrc(string path)
	{
		var text = ReadText(path);
		var parsed = Parser.Scan.parse(text);
		if (!parsed.IsOk)
		{
			return new Outcome<LoadResult>.Fail(ToError(parsed.ErrorValue));
		}

		return new Outcome<LoadResult>.Ok(new LoadResult(
			SourceKind.Urc, text, [ToLoaded(0, parsed.ResultValue)]));
	}

	private static StructuredError ToError(UrcError error) =>
		new(error.Category, error.Line, error.Message);

	private static LoadedChart ToLoaded(int index, Chart chart)
	{
		string name = chart.Metadata.Version;
		if (string.IsNullOrEmpty(name))
		{
			name = index == 0 ? "Main" : $"Chart {index + 1}";
		}

		int normal = 0, holdStart = 0, holdEnd = 0, mine = 0, fake = 0;
		foreach (var note in chart.Notes)
		{
			if (note.Type.Equals(NoteType.N)) normal++;
			else if (note.Type.Equals(NoteType.LS)) holdStart++;
			else if (note.Type.Equals(NoteType.LE)) holdEnd++;
			else if (note.Type.Equals(NoteType.M)) mine++;
			else fake++;
		}

		var summary = new ChartSummary(
			$"{chart.FormatVersion.Major}.{chart.FormatVersion.Minor}",
			chart.Metadata.Title,
			chart.Metadata.Artist,
			chart.Metadata.Creator,
			chart.Metadata.Version,
			chart.Metadata.Original,
			chart.Layout.Keys,
			chart.Layout.SpecialKeys,
			chart.TimingPoints.Length,
			normal, holdStart, holdEnd, mine, fake);

		return new LoadedChart(new ChartItem(index, name), summary, Writer.write(chart));
	}
}
