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

public sealed record StructuredWarning(string Code, int Lane, int OriginalMs, int AdjustedMs, string Message)
{
	public override string ToString() => $"[{Code}] {Message}";
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
	double MinBpm,
	double MaxBpm,
	double Rate,
	int TimingPoints,
	int NotesNormal,
	int NotesHoldStart,
	int NotesHoldEnd,
	int NotesMine,
	int NotesFake)
{
	public int TotalNotes => NotesNormal + NotesHoldStart + NotesHoldEnd + NotesMine + NotesFake;

	public string LayoutText => SpecialKeys > 0 ? $"{Keys}+{SpecialKeys}K" : $"{Keys}K";

	public string BpmText =>
		TimingPoints == 0
			? "—"
			: Math.Abs(MinBpm - MaxBpm) < 1e-6
				? $"{MinBpm:0.##} BPM"
				: $"{MinBpm:0.##}–{MaxBpm:0.##} BPM";

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
			$"BPM:      {BpmText}",
			$"Timing:   {TimingPoints} points",
			$"Notes:    {TotalNotes} (N: {NotesNormal}, LS: {NotesHoldStart}, LE: {NotesHoldEnd}, M: {NotesMine}, F: {NotesFake})");
	}
}

public sealed record LoadedChart(
	ChartItem Item,
	Chart BaseChart,
	ChartSummary Summary,
	string UrcText,
	IReadOnlyList<StructuredWarning> Warnings);

/// <summary>Everything produced by loading one file. For URC sources,
/// <see cref="SourceText"/> holds the original file when unmodified.</summary>
public sealed record LoadResult(SourceKind Kind, string SourceText, IReadOnlyList<LoadedChart> Charts);

public sealed record ScaledChartView(
	ChartSummary Summary,
	string UrcText,
	IReadOnlyList<StructuredWarning> Warnings);

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

	public static Outcome<LoadResult> Load(string path, BmsOptions bms, double rate = 1.0)
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
					kind.Value, Sources.Osu.Parse.parse(ReadText(path)), Sources.Osu.Convert.convert, rate),
				SourceKind.Qua => ConvertSingle(
					kind.Value, Sources.Qua.Parse.parse(ReadText(path)), Sources.Qua.Convert.convert, rate),
				SourceKind.Bms => ConvertSingle(
					kind.Value,
					Sources.Bms.Parse.parse(File.ReadAllBytes(path), ToParseOptions(path, bms)),
					Sources.Bms.Convert.convert,
					rate),
				SourceKind.Sm => ConvertList(
					kind.Value, Sources.Sm.Parse.parse(ReadText(path)), Sources.Sm.Convert.convert, rate),
				SourceKind.Ojn => ConvertList(
					kind.Value,
					Sources.Ojn.Parse.parse(File.ReadAllBytes(path)),
					Sources.Ojn.Convert.convert,
					rate),
				SourceKind.Urc => LoadUrc(path, rate),
				_ => throw new InvalidOperationException($"unhandled kind {kind}"),
			};
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return new Outcome<LoadResult>.Fail(new StructuredError("io", 0, ex.Message));
		}
	}

	public static Outcome<ScaledChartView> ApplyRate(Chart baseChart, double rate)
	{
		var scaled = Urc.scaleRate(baseChart, rate);
		if (!scaled.IsOk)
		{
			return new Outcome<ScaledChartView>.Fail(ToError(scaled.ErrorValue));
		}

		var result = scaled.ResultValue;
		var warnings = result.Warnings.Select(ToWarning).ToArray();
		var summary = BuildSummary(result.Chart, rate);
		return new Outcome<ScaledChartView>.Ok(new ScaledChartView(summary, Urc.write(result.Chart), warnings));
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
		Func<TSource, FSharpResult<Chart, UrcError>> convert,
		double rate)
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

		return BuildLoaded(kind, "", [converted.ResultValue], rate);
	}

	private static Outcome<LoadResult> ConvertList<TSource>(
		SourceKind kind,
		FSharpResult<TSource, UrcError> parsed,
		Func<TSource, FSharpResult<FSharpList<Chart>, UrcError>> convert,
		double rate)
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

		return BuildLoaded(kind, "", converted.ResultValue.ToArray(), rate);
	}

	private static Outcome<LoadResult> LoadUrc(string path, double rate)
	{
		var text = ReadText(path);
		var parsed = Urc.parse(text);
		if (!parsed.IsOk)
		{
			return new Outcome<LoadResult>.Fail(ToError(parsed.ErrorValue));
		}

		return BuildLoaded(SourceKind.Urc, text, [parsed.ResultValue], rate);
	}

	private static Outcome<LoadResult> BuildLoaded(
		SourceKind kind,
		string sourceText,
		IReadOnlyList<Chart> baseCharts,
		double rate)
	{
		var loaded = new List<LoadedChart>(baseCharts.Count);
		for (int index = 0; index < baseCharts.Count; index++)
		{
			var baseChart = baseCharts[index];
			var scaled = ApplyRate(baseChart, rate);
			if (scaled is Outcome<ScaledChartView>.Fail fail)
			{
				return new Outcome<LoadResult>.Fail(fail.Error);
			}

			var view = ((Outcome<ScaledChartView>.Ok)scaled).Value;
			string name = baseChart.Metadata.Version;
			if (string.IsNullOrEmpty(name))
			{
				name = index == 0 ? "Main" : $"Chart {index + 1}";
			}

			loaded.Add(new LoadedChart(
				new ChartItem(index, name),
				baseChart,
				view.Summary,
				view.UrcText,
				view.Warnings));
		}

		return new Outcome<LoadResult>.Ok(new LoadResult(kind, sourceText, loaded));
	}

	private static StructuredError ToError(UrcError error) =>
		new(error.Category, error.Line, error.Message);

	private static StructuredWarning ToWarning(UrcWarning warning) =>
		new(warning.Code, warning.Lane, warning.OriginalMs, warning.AdjustedMs, warning.Message);

	private static ChartSummary BuildSummary(Chart chart, double rate)
	{
		int normal = 0, holdStart = 0, holdEnd = 0, mine = 0, fake = 0;
		foreach (var note in chart.Notes)
		{
			if (note.Type.Equals(NoteType.N)) normal++;
			else if (note.Type.Equals(NoteType.LS)) holdStart++;
			else if (note.Type.Equals(NoteType.LE)) holdEnd++;
			else if (note.Type.Equals(NoteType.M)) mine++;
			else fake++;
		}

		double minBpm = 0, maxBpm = 0;
		bool firstTiming = true;
		foreach (var tp in chart.TimingPoints)
		{
			if (firstTiming)
			{
				minBpm = tp.Bpm;
				maxBpm = tp.Bpm;
				firstTiming = false;
			}
			else
			{
				if (tp.Bpm < minBpm) minBpm = tp.Bpm;
				if (tp.Bpm > maxBpm) maxBpm = tp.Bpm;
			}
		}

		return new ChartSummary(
			$"{chart.FormatVersion.Major}.{chart.FormatVersion.Minor}",
			chart.Metadata.Title,
			chart.Metadata.Artist,
			chart.Metadata.Creator,
			chart.Metadata.Version,
			chart.Metadata.Original,
			chart.Layout.Keys,
			chart.Layout.SpecialKeys,
			minBpm,
			maxBpm,
			rate,
			chart.TimingPoints.Length,
			normal,
			holdStart,
			holdEnd,
			mine,
			fake);
	}
}
