using System.Windows;
using Microsoft.Win32;

namespace PSDToolViewer.Services;

public enum SaveChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>ダイアログ表示。ViewModel から画面を直接触らないための窓口。</summary>
public interface IDialogService
{
    string? ShowOpenFile();
    string? ShowOpenFolder();
    string? ShowSaveFile(string title, string filter, string? defaultName, int filterIndex = 1);
    SaveChoice AskSaveChanges(string documentName);
    bool Confirm(string message);
    void ShowError(string message);
    void ShowInfo(string message);
}

public sealed class DialogService(Func<Window?> owner) : IDialogService
{
    public const string OpenFilter = "対応ファイル (*.psd;*.ptv;*.png)|*.psd;*.ptv;*.png|PSD ファイル (*.psd)|*.psd|PSDTool Viewer ファイル (*.ptv)|*.ptv|PNG 画像 (*.png)|*.png";
    public const string SaveFilter = "PSD ファイル (*.psd)|*.psd|PSDTool Viewer ファイル (*.ptv)|*.ptv";
    public const string ExportFilter = "PSD ファイル (*.psd)|*.psd";

    private const string AppTitle = "PSDTool Viewer";

    public string? ShowOpenFile()
    {
        var dialog = new OpenFileDialog { Filter = OpenFilter, Title = "ファイルを開く" };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? ShowOpenFolder()
    {
        var dialog = new OpenFolderDialog { Title = "フォルダを開く" };
        return dialog.ShowDialog(owner()) == true ? dialog.FolderName : null;
    }

    public string? ShowSaveFile(string title, string filter, string? defaultName, int filterIndex = 1)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FilterIndex = filterIndex,
            FileName = defaultName ?? string.Empty,
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public SaveChoice AskSaveChanges(string documentName)
    {
        var result = Show($"「{documentName}」への変更を保存しますか？", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => SaveChoice.Save,
            MessageBoxResult.No => SaveChoice.Discard,
            _ => SaveChoice.Cancel,
        };
    }

    public bool Confirm(string message)
        => Show(message, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    public void ShowError(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Information);

    private MessageBoxResult Show(string message, MessageBoxButton buttons, MessageBoxImage image)
    {
        var window = owner();
        return window is null
            ? MessageBox.Show(message, AppTitle, buttons, image)
            : MessageBox.Show(window, message, AppTitle, buttons, image);
    }
}
