using System.Windows;
using PSDToolViewer.Services;

namespace PSDToolViewer.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        LayerPositionBox.SelectedIndex = settings.SwapPanels ? 1 : 0;
        ThemeBox.SelectedIndex = (int)settings.Theme;
        BackgroundBox.SelectedIndex = (int)settings.PreviewBackground;
        ThumbnailBox.IsChecked = settings.ShowThumbnails;
        SoftwareRenderingBox.IsChecked = settings.SoftwareRendering;
        UpdateBox.IsChecked = settings.CheckUpdatesOnStartup;
        SourceInitialized += (_, _) => ThemeService.ApplyToWindow(this);
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.SwapPanels = LayerPositionBox.SelectedIndex == 1;
        settings.Theme = (ThemeSetting)Math.Max(0, ThemeBox.SelectedIndex);
        settings.PreviewBackground = (PreviewBackgroundKind)Math.Max(0, BackgroundBox.SelectedIndex);
        settings.ShowThumbnails = ThumbnailBox.IsChecked == true;
        settings.SoftwareRendering = SoftwareRenderingBox.IsChecked == true;
        settings.CheckUpdatesOnStartup = UpdateBox.IsChecked == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
