using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

	private void RateTextBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && sender is System.Windows.Controls.TextBox textBox)
		{
			textBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
			e.Handled = true;
		}
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
