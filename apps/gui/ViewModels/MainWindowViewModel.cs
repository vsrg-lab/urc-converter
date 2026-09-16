using System.Collections.ObjectModel;
using System.IO;
using System.Text;
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

	public MainWindowViewModel(IDialogService dialogs)
	{
		_dialogs = dialogs;
	}

	[ObservableProperty]
	private string? _filePath;

	[ObservableProperty]
	private ChartItem? _selectedChart;

	[ObservableProperty]
	private string _summaryText = "";

	[ObservableProperty]
	private string _previewText = "";

	[ObservableProperty]
	private string _statusMessage = "Open a chart file to get started.";

	[ObservableProperty]
	private bool _isError;

	[ObservableProperty]
	private bool _isBmsSource;

	[ObservableProperty]
	private string? _seedText;

	[ObservableProperty]
	private string? _branchesText;

	public ObservableCollection<ChartItem> Charts { get; } = [];

	[RelayCommand]
	private void OpenFile()
	{
		var path = _dialogs.PickSourceFile();
		if (path is not null)
		{
			LoadPath(path);
		}
	}

	private bool CanSave() => _loaded is { Kind: not SourceKind.Urc } && SelectedChart is not null;

	[RelayCommand]
	private void Save()
	{
		if (!CanSave() || _loaded is not { } loaded || _loadedPath is null || SelectedChart is not { } selected)
		{
			return;
		}

		var stem = Path.GetFileNameWithoutExtension(_loadedPath);
		var suffix = loaded.Charts.Count > 1 ? $".{selected.Index}" : "";
		var target = _dialogs.PickSaveFile($"{stem}{suffix}.urc");
		if (target is null)
		{
			return;
		}

		try
		{
			File.WriteAllText(target, loaded.Charts[selected.Index].UrcText, new UTF8Encoding(false));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			IsError = true;
			StatusMessage = $"Save failed: {ex.Message}";
			return;
		}

		IsError = false;
		StatusMessage = $"Saved {target}";
	}

	public void LoadPath(string path)
	{
		var restore = SelectedChart?.Index;
		var outcome = ConverterService.Load(path, CurrentBmsOptions());

		switch (outcome)
		{
			case Outcome<LoadResult>.Fail fail:
				_loaded = null;
				_loadedPath = null;
				FilePath = null;
				IsBmsSource = false;
				Charts.Clear();
				SelectedChart = null;
				IsError = true;
				StatusMessage = $"{Path.GetFileName(path)}: {fail.Error}";
				break;

			case Outcome<LoadResult>.Ok ok:
				_loaded = ok.Value;
				_loadedPath = path;
				FilePath = path;
				IsBmsSource = ok.Value.Kind == SourceKind.Bms;
				Charts.Clear();
				foreach (var chart in ok.Value.Charts)
				{
					Charts.Add(chart.Item);
				}

				SelectedChart = ok.Value.Charts.FirstOrDefault(c => c.Item.Index == restore)?.Item
					?? Charts.FirstOrDefault();
				IsError = false;
				StatusMessage = ok.Value.Charts.Count switch
				{
					0 => $"{Path.GetFileName(path)}: no charts found",
					1 => "Loaded 1 chart.",
					var count => $"Loaded {count} charts.",
				};
				break;
		}

		SaveCommand.NotifyCanExecuteChanged();
	}

	partial void OnSelectedChartChanged(ChartItem? value)
	{
		if (_loaded is { } loaded && value is { } selected)
		{
			var chart = loaded.Charts[selected.Index];
			SummaryText = chart.Summary.ToString();
			PreviewText = loaded.Kind == SourceKind.Urc ? loaded.SourceText : chart.UrcText;
		}
		else
		{
			SummaryText = "";
			PreviewText = "";
		}

		SaveCommand.NotifyCanExecuteChanged();
	}

	partial void OnSeedTextChanged(string? value) => ReloadIfBms();

	partial void OnBranchesTextChanged(string? value) => ReloadIfBms();

	private void ReloadIfBms()
	{
		if (_loaded is not { Kind: SourceKind.Bms } || _loadedPath is null)
		{
			return;
		}

		if (!TryBmsOptions(out _))
		{
			IsError = true;
			StatusMessage = "Invalid seed or branches value.";
			return;
		}

		LoadPath(_loadedPath);
	}

	private bool TryBmsOptions(out BmsOptions options)
	{
		long? seed = null;
		if (!string.IsNullOrWhiteSpace(SeedText))
		{
			if (!long.TryParse(SeedText.Trim(), out var parsed))
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
				if (!ulong.TryParse(part.Trim(), out var parsed))
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
}
