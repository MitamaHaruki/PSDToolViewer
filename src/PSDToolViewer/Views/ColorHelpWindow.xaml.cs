using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using PSDToolViewer.Services;

namespace PSDToolViewer.Views;

/// <summary>端末側の自動補正で色が変わって見える場合の案内。</summary>
public partial class ColorHelpWindow : Window
{
    public ColorHelpWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.ApplyToWindow(this);
    }

    private void OpenDisplaySettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            MessageBox.Show(this, "ディスプレイの設定を開けませんでした。「設定」→「システム」→「ディスプレイ」を開いてください。",
                "PSDTool Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
