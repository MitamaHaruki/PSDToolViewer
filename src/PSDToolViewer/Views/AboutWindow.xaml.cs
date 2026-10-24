using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using PSDToolViewer.Services;

namespace PSDToolViewer.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"バージョン {AppVersion}";
        SourceInitialized += (_, _) => ThemeService.ApplyToWindow(this);
    }

    public static string AppVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            int plus = version.IndexOf('+');
            return plus >= 0 ? version[..plus] : version;
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
