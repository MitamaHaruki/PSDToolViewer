using Microsoft.Win32;
using PSDToolViewer.Interop;

namespace PSDToolViewer.Services;

/// <summary>
/// ファイルの関連付け（現在のユーザーのみ）。.ptv は既定のアプリにし、PSD・PNG は「プログラムから開く」の候補に登録するだけにする。
/// </summary>
public static class FileAssociation
{
    private const string ExeName = "PSDToolViewer.exe";
    private const string PtvProgId = "PSDToolViewer.ptv";
    private const string PsdProgId = "PSDToolViewer.psd";
    private const string ClassesRoot = @"Software\Classes";

    public static void Register()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        string command = $"\"{exe}\" \"%1\"";
        string icon = $"\"{exe}\",0";

        using (var classes = Registry.CurrentUser.CreateSubKey(ClassesRoot))
        {
            using (var ext = classes.CreateSubKey(".ptv"))
                ext.SetValue(string.Empty, PtvProgId);
            RegisterProgId(classes, PtvProgId, "PSDTool Viewer ファイル", icon, command);

            RegisterProgId(classes, PsdProgId, "PSD ファイル", icon, command);
            using (var psd = classes.CreateSubKey(@".psd\OpenWithProgids"))
                psd.SetValue(PsdProgId, Array.Empty<byte>(), RegistryValueKind.None);

            using var app = classes.CreateSubKey($@"Applications\{ExeName}");
            app.SetValue("FriendlyAppName", "PSDTool Viewer");
            using (var types = app.CreateSubKey("SupportedTypes"))
            {
                types.SetValue(".psd", string.Empty);
                types.SetValue(".ptv", string.Empty);
                types.SetValue(".png", string.Empty);
            }
            using var cmd = app.CreateSubKey(@"shell\open\command");
            cmd.SetValue(string.Empty, command);
        }
        NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, 0, 0, 0);
    }

    public static void Unregister()
    {
        using (var classes = Registry.CurrentUser.OpenSubKey(ClassesRoot, writable: true))
        {
            if (classes is null) return;
            classes.DeleteSubKeyTree(PtvProgId, throwOnMissingSubKey: false);
            classes.DeleteSubKeyTree(PsdProgId, throwOnMissingSubKey: false);
            classes.DeleteSubKeyTree($@"Applications\{ExeName}", throwOnMissingSubKey: false);
            using (var ext = classes.OpenSubKey(".ptv", writable: true))
            {
                if (ext?.GetValue(string.Empty) as string == PtvProgId)
                {
                    ext.Close();
                    classes.DeleteSubKeyTree(".ptv", throwOnMissingSubKey: false);
                }
            }
            using var psd = classes.OpenSubKey(@".psd\OpenWithProgids", writable: true);
            psd?.DeleteValue(PsdProgId, throwOnMissingValue: false);
        }
        NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, 0, 0, 0);
    }

    private static void RegisterProgId(RegistryKey classes, string progId, string description, string icon, string command)
    {
        using var key = classes.CreateSubKey(progId);
        key.SetValue(string.Empty, description);
        using (var iconKey = key.CreateSubKey("DefaultIcon"))
            iconKey.SetValue(string.Empty, icon);
        using var cmd = key.CreateSubKey(@"shell\open\command");
        cmd.SetValue(string.Empty, command);
    }
}
