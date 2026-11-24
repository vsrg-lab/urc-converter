using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UrcConverter.Gui.Models;
using UrcConverter.Gui.Services;

namespace UrcConverter.Gui.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
	private readonly IDialogService _dialogs;
	private string? _loadedPath;
	private LoadResult? _loaded;
	private ScaledChartView? _activeView;
	private bool _suppressRateTextSync;

	public MainWindowViewModel(IDialogService dialogs)
	{
		_dialogs = dialogs;
	}

	[ObservableProperty]
	private string? _filePath;

	[ObservableProperty]
	private string _fileName = "";

	[ObservableProperty]
	private bool _hasFile;

	[ObservableProperty]
	private ChartItem? _selectedChart;

	[ObservableProperty]
	private string _summaryText = "";

	[ObservableProperty]
	private string _summaryTitle = "";

	[ObservableProperty]
	private string _summarySubtitle = "";

	[ObservableProperty]
	private string _summaryOriginal = "";

	[ObservableProperty]
	private string _summaryFormatVersion = "";

	[ObservableProperty]
	private string _summaryLayout = "";

	[ObservableProperty]
	private string _summaryBpm = "";

	[ObservableProperty]
	private int _summaryTimingCount;

	[ObservableProperty]
	private int _summaryTotalNotes;

	[ObservableProperty]
	private int _summaryNormal;

	[ObservableProperty]
	private int _summaryHoldStart;

	[ObservableProperty]
	private int _summaryHoldEnd;

	[ObservableProperty]
	private int _summaryMine;

	[ObservableProperty]
	private int _summaryFake;

	[ObservableProperty]
	private string _previewText = "";

	[ObservableProperty]
	private string _previewStats = "";

	[ObservableProperty]
	private string _statusMessage = "Drop a chart file here or click Open to begin.";

	[ObservableProperty]
	private bool _isError;

	[ObservableProperty]
	private string _errorMessage = "";

	[ObservableProperty]
	private bool _hasWarnings;

	[ObservableProperty]
	private string _warningBannerTitle = "";

	[ObservableProperty]
	private string _warningBannerMessage = "";

	[ObservableProperty]
	private bool _isBmsSource;

	[ObservableProperty]
	private string? _seedText;

	[ObservableProperty]
	private string? _branchesText;

	[ObservableProperty]
	private double _rate = 1.0;

	[ObservableProperty]
	private string _rateText = "1.00";

	[ObservableProperty]
	private string _rateBadgeText = "1.00x";

	[ObservableProperty]
	private bool _isRateModified;

	public ObservableCollection<ChartItem> Charts { get; } = [];

	public ObservableCollection<StructuredWarning> Warnings { get; } = [];

	[RelayCommand]
	private void OpenFile()
	{
		var path = _dialogs.PickSourceFile();
		if (path is not null)
		{
			LoadPath(path);
		}
	}

	private bool CanSave() =>
		_loaded is not null && SelectedChart is not null && _activeView is not null
		&& (_loaded.Kind != SourceKind.Urc || IsRateModified || HasWarnings);

	[RelayCommand(CanExecute = nameof(CanSave))]
	private void Save()
	{
		if (!CanSave() || _loaded is not { } loaded || _loadedPath is null
			|| SelectedChart is not { } selected || _activeView is not { } view)
		{
			return;
		}

		var stem = Path.GetFileNameWithoutExtension(_loadedPath);
		var chartSuffix = loaded.Charts.Count > 1 ? $".{selected.Index}" : "";
		var rateSuffix = IsRateModified
			? $"_{Rate.ToString("0.00", CultureInfo.InvariantCulture)}x"
			: "";
		var target = _dialogs.PickSaveFile($"{stem}{chartSuffix}{rateSuffix}.urc");
		if (target is null)
		{
			return;
		}

		try
		{
			File.WriteAllText(target, view.UrcText, new UTF8Encoding(false));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			SetError($"Save failed: {ex.Message}");
			return;
		}

		ClearError();
		StatusMessage = $"Saved {Path.GetFileName(target)}";
	}

	private bool CanCopyPreview() => !string.IsNullOrEmpty(PreviewText);

	[RelayCommand(CanExecute = nameof(CanCopyPreview))]
	private void CopyPreview()
	{
		if (string.IsNullOrEmpty(PreviewText))
		{
			return;
		}

		try
		{
			Clipboard.SetText(PreviewText);
			StatusMessage = "Copied URC output to clipboard.";
		}
		catch (Exception ex)
		{
			SetError($"Copy failed: {ex.Message}");
		}
	}

	[RelayCommand]
	private void SetRatePreset(string value)
	{
		if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
		{
			Rate = ClampRate(parsed);
		}
	}

	[RelayCommand]
	private void StepRate(string delta)
	{
		if (double.TryParse(delta, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
		{
			Rate = ClampRate(Rate + parsed);
		}
	}

	[RelayCommand]
	private void ResetRate()
	{
		Rate = 1.0;
	}

	[RelayCommand]
	private void ApplyBmsOptions() => ReloadIfBms();

	public void LoadPath(string path, bool keepCurrentOnError = false)
	{
		var restore = SelectedChart?.Index;
		var outcome = ConverterService.Load(path, CurrentBmsOptions(), Rate);

		switch (outcome)
		{
			case Outcome<LoadResult>.Fail fail:
				if (!keepCurrentOnError)
				{
					_loaded = null;
					_loadedPath = null;
					_activeView = null;
					FilePath = null;
					FileName = "";
					HasFile = false;
					IsBmsSource = false;
					Charts.Clear();
					SelectedChart = null;
					ClearSummaryAndPreview();
				}

				SetError($"{Path.GetFileName(path)}: {fail.Error}");
				break;

			case Outcome<LoadResult>.Ok ok:
				_loaded = ok.Value;
				_loadedPath = path;
				FilePath = path;
				FileName = Path.GetFileName(path);
				HasFile = true;
				IsBmsSource = ok.Value.Kind == SourceKind.Bms;
				Charts.Clear();
				foreach (var chart in ok.Value.Charts)
				{
					Charts.Add(chart.Item);
				}

				ClearError();
				SelectedChart = ok.Value.Charts.FirstOrDefault(c => c.Item.Index == restore)?.Item
					?? Charts.FirstOrDefault();
				RefreshActiveChartView();

				StatusMessage = ok.Value.Charts.Count switch
				{
					0 => $"{FileName}: no charts found",
					1 => $"Loaded {FileName} (1 chart).",
					var count => $"Loaded {FileName} ({count} charts).",
				};
				break;
		}

		NotifyActionStates();
	}

	partial void OnSelectedChartChanged(ChartItem? value)
	{
		RefreshActiveChartView();
		NotifyActionStates();
	}

	partial void OnRateChanged(double value)
	{
		var normalized = ClampRate(value);
		if (Math.Abs(normalized - value) > 1e-9)
		{
			Rate = normalized;
			return;
		}

		IsRateModified = Math.Abs(normalized - 1.0) > 1e-9;
		var formatted = normalized.ToString("0.00", CultureInfo.InvariantCulture);
		RateBadgeText = $"{formatted}x";

		if (!_suppressRateTextSync)
		{
			_suppressRateTextSync = true;
			RateText = formatted;
			_suppressRateTextSync = false;
		}

		RefreshActiveChartView();
		NotifyActionStates();
	}

	partial void OnRateTextChanged(string value)
	{
		if (_suppressRateTextSync)
		{
			return;
		}

		var cleaned = (value ?? "").Trim().TrimEnd('x', 'X');
		if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
		{
			var clamped = ClampRate(parsed);
			_suppressRateTextSync = true;
			Rate = clamped;
			RateText = clamped.ToString("0.00", CultureInfo.InvariantCulture);
			_suppressRateTextSync = false;
		}
		else
		{
			_suppressRateTextSync = true;
			RateText = Rate.ToString("0.00", CultureInfo.InvariantCulture);
			_suppressRateTextSync = false;
		}
	}

	partial void OnSeedTextChanged(string? value) => ReloadIfBms();

	partial void OnBranchesTextChanged(string? value) => ReloadIfBms();

	private void RefreshActiveChartView()
	{
		Warnings.Clear();
		HasWarnings = false;
		WarningBannerTitle = "";
		WarningBannerMessage = "";

		if (_loaded is not { } loaded || SelectedChart is not { } selected
			|| selected.Index < 0 || selected.Index >= loaded.Charts.Count)
		{
			_activeView = null;
			ClearSummaryAndPreview();
			return;
		}

		var entry = loaded.Charts[selected.Index];
		var outcome = ConverterService.ApplyRate(entry.BaseChart, Rate);
		if (outcome is Outcome<ScaledChartView>.Fail fail)
		{
			_activeView = null;
			SetError(fail.Error.ToString());
			return;
		}

		var view = ((Outcome<ScaledChartView>.Ok)outcome).Value;
		_activeView = view;

		var summary = view.Summary;
		SummaryText = summary.ToString();
		SummaryTitle = summary.Title;
		SummarySubtitle = $"{summary.Artist} · Chart by {summary.Creator} · {summary.ChartVersion}";
		SummaryOriginal = summary.Original;
		SummaryFormatVersion = $"URC {summary.FormatVersion}";
		SummaryLayout = summary.LayoutText;
		SummaryBpm = summary.BpmText;
		SummaryTimingCount = summary.TimingPoints;
		SummaryTotalNotes = summary.TotalNotes;
		SummaryNormal = summary.NotesNormal;
		SummaryHoldStart = summary.NotesHoldStart;
		SummaryHoldEnd = summary.NotesHoldEnd;
		SummaryMine = summary.NotesMine;
		SummaryFake = summary.NotesFake;

		bool useOriginalSource = loaded.Kind == SourceKind.Urc && !IsRateModified && view.Warnings.Count == 0;
		PreviewText = useOriginalSource ? loaded.SourceText : view.UrcText;
		int lineCount = CountLines(PreviewText);
		PreviewStats = $"{lineCount:N0} lines";

		if (view.Warnings.Count > 0)
		{
			foreach (var warning in view.Warnings)
			{
				Warnings.Add(warning);
			}

			HasWarnings = true;
			WarningBannerTitle = view.Warnings.Count == 1
				? "1 source spacing adjustment"
				: $"{view.Warnings.Count} source spacing adjustments";
			WarningBannerMessage = view.Warnings[0].ToString();
		}
	}

	private void ReloadIfBms()
	{
		if (_loaded is not { Kind: SourceKind.Bms } || _loadedPath is null)
		{
			return;
		}

		if (!TryBmsOptions(out _))
		{
			SetError("Invalid BMS seed or branch pick list (expected integer seed and comma-separated positive integers).");
			return;
		}

		LoadPath(_loadedPath, keepCurrentOnError: true);
	}

	private bool TryBmsOptions(out BmsOptions options)
	{
		long? seed = null;
		if (!string.IsNullOrWhiteSpace(SeedText))
		{
			if (!long.TryParse(SeedText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
			{
				options = BmsOptions.Default;
				return false;
			}

			seed = parsed;
		}

		IReadOnlyList<ulong>? branches = null;
		if (!string.IsNullOrWhiteSpace(BranchesText))
		{
			var values = new List<ulong>();
			foreach (var part in BranchesText.Split(','))
			{
				if (!ulong.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
				{
					options = BmsOptions.Default;
					return false;
				}

				values.Add(parsed);
			}

			branches = values;
		}

		options = new BmsOptions(seed, branches);
		return true;
	}

	private BmsOptions CurrentBmsOptions() => TryBmsOptions(out var options) ? options : BmsOptions.Default;

	private void ClearSummaryAndPreview()
	{
		SummaryText = "";
		SummaryTitle = "";
		SummarySubtitle = "";
		SummaryOriginal = "";
		SummaryFormatVersion = "";
		SummaryLayout = "";
		SummaryBpm = "";
		SummaryTimingCount = 0;
		SummaryTotalNotes = 0;
		SummaryNormal = 0;
		SummaryHoldStart = 0;
		SummaryHoldEnd = 0;
		SummaryMine = 0;
		SummaryFake = 0;
		PreviewText = "";
		PreviewStats = "";
		Warnings.Clear();
		HasWarnings = false;
		WarningBannerTitle = "";
		WarningBannerMessage = "";
	}

	private void SetError(string message)
	{
		IsError = true;
		ErrorMessage = message;
		StatusMessage = message;
	}

	private void ClearError()
	{
		IsError = false;
		ErrorMessage = "";
	}

	private void NotifyActionStates()
	{
		SaveCommand.NotifyCanExecuteChanged();
		CopyPreviewCommand.NotifyCanExecuteChanged();
	}

	private static double ClampRate(double value) =>
		Math.Round(Math.Clamp(value, 0.50, 3.00), 2, MidpointRounding.AwayFromZero);

	private static int CountLines(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return 0;
		}

		int count = 1;
		foreach (char c in text)
		{
			if (c == '\n')
			{
				count++;
			}
		}

		return text.EndsWith('\n') ? count - 1 : count;
	}
}
