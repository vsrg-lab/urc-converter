using System.Windows;
using UrcConverter.Gui.Services;
using UrcConverter.Gui.ViewModels;
using Wpf.Ui.Controls;

namespace UrcConverter.Gui;

public partial class MainWindow : FluentWindow
{
	public MainWindow()
	{
		InitializeComponent();
		DataContext = new MainWindowViewModel(new DialogService(this));
	}

	private void Window_DragOver(object sender, DragEventArgs e)
	{
		e.Handled = true;
		e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
			? DragDropEffects.Copy
			: DragDropEffects.None;
	}

	private void Window_Drop(object sender, DragEventArgs e)
	{
		if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
		{
			((MainWindowViewModel)DataContext).LoadPath(files[0]);
		}
	}
}
