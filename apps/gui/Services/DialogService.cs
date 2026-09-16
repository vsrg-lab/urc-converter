using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace UrcConverter.Gui.Services;

public interface IDialogService
{
	string? PickSourceFile();

	string? PickSaveFile(string defaultFileName);
}

public sealed class DialogService(Window owner) : IDialogService
{
	private const string SourceFilter =
		"Charts (*.osu;*.qua;*.bms;*.bme;*.bml;*.pms;*.sm;*.ssc;*.ojn;*.urc)"
		+ "|*.osu;*.qua;*.bms;*.bme;*.bml;*.pms;*.sm;*.ssc;*.ojn;*.urc"
		+ "|All files (*.*)|*.*";

	public string? PickSourceFile()
	{
		var dialog = new OpenFileDialog { Filter = SourceFilter, Title = "Open a chart" };
		return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
	}

	public string? PickSaveFile(string defaultFileName)
	{
		var dialog = new SaveFileDialog
		{
			Filter = "URC chart (*.urc)|*.urc",
			Title = "Save as URC",
			FileName = defaultFileName,
		};
		return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
	}
}
