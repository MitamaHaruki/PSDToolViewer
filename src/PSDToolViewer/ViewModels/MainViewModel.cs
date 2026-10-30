using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PSDToolViewer.Core.Formats;
using PSDToolViewer.Core.Imaging;
using PSDToolViewer.Core.Model;
using PSDToolViewer.Core.Rendering;
using PSDToolViewer.Rendering;
using PSDToolViewer.Services;

namespace PSDToolViewer.ViewModels;

public enum ZoomAction
{
    In,
    Out,
    Fit,
    Actual,
}

public sealed class MainViewModel : ObservableObject
{
    private const string AppName = "PSDTool Viewer";

    private const string UnsupportedOverwriteWarning =
        "この PSD には、PSDTool Viewer では保持できない情報（調整レイヤー、レイヤー効果、テキストの編集情報、スマートオブジェクトなど）が含まれています。\n" +
        "上書き保存するとこれらの情報は失われ、画像として保存されます。\n\n続けますか？";

    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly UpdateService _updates;
    private readonly Dispatcher _dispatcher;
    private readonly Compositor _compositor = new();
    private readonly Dictionary<LayerNode, LayerRow> _rowMap = [];

    private LayerDocument? _document;
    private EditHistory _history = new();
    private long _savedStateId;
    private string? _filePath;
    private string? _documentName;
    private bool _psdOverwriteConfirmed;
    private LayerRow? _anchor;
    private LayerRow? _focusRow;

    /// <summary>ドキュメントの色プロファイルから sRGB への変換（表示・コピー・ドラッグ出力に使う）。不要なら null。</summary>
    private ColorTransform? _displayTransform;

    private BitmapSource? _previewBitmap;
    private Canvas? _lastCanvas;
    private int _renderVersion;
    private int _lastCanvasVersion = -1;
    private CancellationTokenSource? _renderCts;

    private bool _isBusy;
    private string _busyMessage = string.Empty;
    private double _busyProgress;
    private bool _busyCancelable;
    private CancellationTokenSource? _busyCts;

    private bool _isUpdateAvailable;
    private bool _isUpdateDownloading;
    private string _updateMessage = string.Empty;

    public MainViewModel(AppSettings settings, IDialogService dialogs, UpdateService updates, Dispatcher dispatcher)
    {
        _settings = settings;
        _dialogs = dialogs;
        _updates = updates;
        _dispatcher = dispatcher;
        Thumbnails = new ThumbnailService(dispatcher);

        NewCommand = new RelayCommand(async () => await NewAsync(), () => !IsBusy);
        OpenCommand = new RelayCommand(async () => await OpenWithDialogAsync(), () => !IsBusy);
        OpenFolderCommand = new RelayCommand(async () => await OpenFolderWithDialogAsync(), () => !IsBusy);
        OpenRecentCommand = new RelayCommand(async p => await OpenRecentAsync(p as string), _ => !IsBusy);
        SaveCommand = new RelayCommand(async () => await SaveAsync(), () => CanEdit);
        SaveAsCommand = new RelayCommand(async () => await SaveAsAsync(), () => CanEdit);
        ExportCommand = new RelayCommand(async () => await ExportAsync(), () => CanEdit && HasCanvas);
        UndoCommand = new RelayCommand(Undo, () => CanEdit && _history.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => CanEdit && _history.CanRedo);
        CopyCommand = new RelayCommand(CopyToClipboard, () => CanEdit && HasCanvas);
        CreateFolderCommand = new RelayCommand(CreateFolder, () => CanEdit);
        ToggleRadioCommand = new RelayCommand(ToggleRadio, () => CanEdit && HasSelection);
        ToggleForcedCommand = new RelayCommand(ToggleForced, () => CanEdit && HasSelection);
        DeleteCommand = new RelayCommand(DeleteSelection, () => CanEdit && HasSelection);
        RenameCommand = new RelayCommand(() => BeginRename(), () => CanEdit && HasSelection);
        SelectAllCommand = new RelayCommand(SelectAll, () => CanEdit);
        ExpandAllCommand = new RelayCommand(() => SetAllExpanded(true), () => CanEdit);
        CollapseAllCommand = new RelayCommand(() => SetAllExpanded(false), () => CanEdit);
        ZoomInCommand = new RelayCommand(() => ZoomRequested?.Invoke(this, ZoomAction.In), () => HasCanvas);
        ZoomOutCommand = new RelayCommand(() => ZoomRequested?.Invoke(this, ZoomAction.Out), () => HasCanvas);
        ZoomFitCommand = new RelayCommand(() => ZoomRequested?.Invoke(this, ZoomAction.Fit), () => HasCanvas);
        ZoomActualCommand = new RelayCommand(() => ZoomRequested?.Invoke(this, ZoomAction.Actual), () => HasCanvas);
        CheckUpdateCommand = new RelayCommand(async () => await CheckForUpdatesAsync(manual: true), () => !IsBusy);
        ApplyUpdateCommand = new RelayCommand(async () => await ApplyUpdateAsync(), () => !IsBusy && !_isUpdateDownloading);
        DismissUpdateCommand = new RelayCommand(() => IsUpdateAvailable = false, () => !_isUpdateDownloading);
        CancelBusyCommand = new RelayCommand(() => _busyCts?.Cancel(), () => _busyCancelable);
    }

    // ---- 公開プロパティ ----

    public RowCollection Rows { get; } = [];
    public ThumbnailService Thumbnails { get; }
    public AppSettings Settings => _settings;

    public LayerDocument? Document => _document;
    public bool HasDocument => _document is not null;
    public bool HasCanvas => _document is { Width: > 0, Height: > 0 };
    public bool CanEdit => _document is not null && !_isBusy;
    public bool HasSelection => Rows.Any(r => r.IsSelected);

    public bool IsDirty => _document is not null && _history.StateId != _savedStateId;

    public string Title => _document is null
        ? AppName
        : $"{AppName} - {_documentName ?? "無題"}{(IsDirty ? " ●" : string.Empty)}";

    public string DocumentSizeText => HasCanvas ? $"{_document!.Width} × {_document.Height}" : string.Empty;

    public bool TrimTransparent
    {
        get => _settings.TrimTransparent;
        set
        {
            if (_settings.TrimTransparent == value) return;
            _settings.TrimTransparent = value;
            OnPropertyChanged();
        }
    }

    public bool ShowThumbnails => _settings.ShowThumbnails;
    public double RowHeight => _settings.ShowThumbnails ? 36 : 28;

    public BitmapSource? PreviewBitmap
    {
        get => _previewBitmap;
        private set => SetField(ref _previewBitmap, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value)) OnPropertyChanged(nameof(CanEdit));
        }
    }

    public string BusyMessage
    {
        get => _busyMessage;
        private set => SetField(ref _busyMessage, value);
    }

    public double BusyProgress
    {
        get => _busyProgress;
        private set => SetField(ref _busyProgress, value);
    }

    public bool BusyCancelable
    {
        get => _busyCancelable;
        private set => SetField(ref _busyCancelable, value);
    }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set => SetField(ref _isUpdateAvailable, value);
    }

    public bool IsUpdateDownloading
    {
        get => _isUpdateDownloading;
        private set => SetField(ref _isUpdateDownloading, value);
    }

    public string UpdateMessage
    {
        get => _updateMessage;
        private set => SetField(ref _updateMessage, value);
    }

    /// <summary>プレビューからのドラッグ中（自分自身へのドロップを無視するため）。</summary>
    public bool IsDraggingOut { get; set; }

    public event EventHandler? DocumentReplaced;
    public event EventHandler<LayerRow>? ScrollIntoViewRequested;
    public event EventHandler<LayerRow>? RenameRequested;
    public event EventHandler<ZoomAction>? ZoomRequested;

    // ---- コマンド ----

    public ICommand NewCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenRecentCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand CreateFolderCommand { get; }
    public ICommand ToggleRadioCommand { get; }
    public ICommand ToggleForcedCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ZoomFitCommand { get; }
    public ICommand ZoomActualCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand ApplyUpdateCommand { get; }
    public ICommand DismissUpdateCommand { get; }
    public ICommand CancelBusyCommand { get; }

    // ---- ドキュメントの読み込み・保存 ----

    private async Task NewAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        SetDocument(null, null, null);
    }

    private async Task OpenWithDialogAsync()
    {
        var path = _dialogs.ShowOpenFile();
        if (path is not null) await OpenPathAsync(path);
    }

    private async Task OpenFolderWithDialogAsync()
    {
        var path = _dialogs.ShowOpenFolder();
        if (path is not null) await OpenPathAsync(path);
    }

    private async Task OpenRecentAsync(string? path)
    {
        if (path is null) return;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            _dialogs.ShowError($"「{path}」が見つかりません。\n一覧から削除します。");
            _settings.RecentFiles.Remove(path);
            _settings.Save();
            return;
        }
        await OpenPathAsync(path);
    }

    /// <summary>ファイル・フォルダを開く。PNG は現在のドキュメントの一番下に追加する（何も開いていなければ新規）。</summary>
    public async Task OpenPathAsync(string path)
    {
        if (IsBusy) return;
        var kind = DocumentIO.DetectKind(path);
        switch (kind)
        {
            case DocumentKind.Unknown:
                _dialogs.ShowError("対応していないファイル形式です。\nPSD・.ptv・PNG ファイル、またはフォルダを指定してください。");
                return;
            case DocumentKind.Png:
                await ImportPngsAsync([path], null, int.MaxValue);
                return;
        }

        if (!await ConfirmDiscardAsync()) return;
        var document = await RunBusyAsync("読み込み中…", (p, ct) => DocumentIO.Load(path, p, ct), cancelable: true);
        if (document is null) return;

        string name = kind == DocumentKind.Folder
            ? new DirectoryInfo(path).Name
            : Path.GetFileName(path);
        SetDocument(document, kind is DocumentKind.Psd or DocumentKind.Ptv ? path : null, name);
        _settings.AddRecent(path);
        _settings.Save();
    }

    /// <summary>ドロップされたファイルを処理する。PSD 等は開き直し、PNG は追加する。</summary>
    public async Task HandleDroppedPathsAsync(IReadOnlyList<string> paths, LayerGroup? target = null, int index = int.MaxValue)
    {
        if (IsBusy || IsDraggingOut) return;
        var document = paths.FirstOrDefault(p => DocumentIO.DetectKind(p) is DocumentKind.Psd or DocumentKind.Ptv or DocumentKind.Folder);
        var pngs = paths.Where(p => DocumentIO.DetectKind(p) == DocumentKind.Png).ToList();
        if (document is null && pngs.Count == 0)
        {
            _dialogs.ShowError("対応していないファイル形式です。\nPSD・.ptv・PNG ファイル、またはフォルダをドロップしてください。");
            return;
        }
        if (document is not null)
        {
            await OpenPathAsync(document);
            target = null;
            index = int.MaxValue;
        }
        if (pngs.Count > 0) await ImportPngsAsync(pngs, target, index);
    }

    private async Task ImportPngsAsync(IReadOnlyList<string> paths, LayerGroup? target, int index)
    {
        var sorted = paths.OrderBy(Path.GetFileName, Comparer<string?>.Create((a, b) => ImageImport.CompareNatural(a ?? string.Empty, b ?? string.Empty))).ToList();
        var documentProfile = _document?.ColorProfile;
        var imported = await RunBusyAsync("画像を読み込み中…", (p, ct) =>
        {
            // PNG は sRGB として読み込み、ドキュメントに色プロファイルがあればその色空間に合わせる
            var toDocument = documentProfile is null ? null : ColorTransform.Create(null, documentProfile);
            var list = new List<ImportedLayer>();
            for (int i = 0; i < sorted.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                list.Add(ImageImport.LoadPng(sorted[i], toDocument));
                p.Report((i + 1.0) / sorted.Count);
            }
            return list;
        }, cancelable: true);
        if (imported is null || imported.Count == 0) return;

        int width = imported.Max(i => i.CanvasWidth);
        int height = imported.Max(i => i.CanvasHeight);
        var layers = imported.Select(i => i.Layer).ToList();

        if (_document is null)
        {
            var document = new LayerDocument(width, height);
            foreach (var layer in layers) document.Root.Add(layer);
            document.NormalizeRadios();
            SetDocument(document, null, Path.GetFileName(sorted[0]));
            return;
        }

        var doc = _document;
        _history.Apply(doc, () =>
        {
            doc.EnsureCanvasContains(new PixelRect(0, 0, width, height));
            DocumentOperations.AddLayers(doc, layers, target, index);
            return true;
        });
        if (target is not null) target.IsExpanded = true;
        AfterEdit();
        SelectNodes(layers);
    }

    private void SetDocument(LayerDocument? document, string? filePath, string? name)
    {
        foreach (var row in _rowMap.Values) row.Detach();
        _rowMap.Clear();
        _document = document;
        _filePath = filePath;
        _documentName = name;
        _history = new EditHistory();
        _savedStateId = _history.StateId;
        _psdOverwriteConfirmed = false;
        _anchor = null;
        _focusRow = null;
        _displayTransform = document?.ColorProfile is { } profile ? ColorTransform.Create(profile, null) : null;
        Thumbnails.Transform = _displayTransform;

        RebuildRows();
        RequestRender();
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(HasCanvas));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(DocumentSizeText));
        RaiseDirtyChanged();
        DocumentReplaced?.Invoke(this, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>未保存の変更があれば保存するか確認する。続行してよければ true。</summary>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!IsDirty) return true;
        return _dialogs.AskSaveChanges(_documentName ?? "無題") switch
        {
            SaveChoice.Save => await SaveAsync(),
            SaveChoice.Discard => true,
            _ => false,
        };
    }

    public async Task<bool> SaveAsync()
    {
        if (_document is null || IsBusy) return false;
        if (_filePath is null) return await SaveAsAsync();
        if (!ConfirmPsdOverwrite(_filePath)) return false;
        return await SaveToAsync(_filePath);
    }

    private async Task<bool> SaveAsAsync()
    {
        if (_document is null || IsBusy) return false;
        int filterIndex = _filePath is not null && DocumentIO.DetectKind(_filePath) == DocumentKind.Ptv ? 2 : 1;
        var path = _dialogs.ShowSaveFile("名前を付けて保存", DialogService.SaveFilter, DefaultFileName(), filterIndex);
        if (path is null) return false;
        if (!ConfirmPsdOverwrite(path)) return false;
        if (!await SaveToAsync(path)) return false;
        _filePath = path;
        _documentName = Path.GetFileName(path);
        RaiseDirtyChanged();
        return true;
    }

    private bool ConfirmPsdOverwrite(string path)
    {
        if (_document is not { HasUnsupportedFeatures: true } || _psdOverwriteConfirmed) return true;
        if (_filePath is null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(_filePath), StringComparison.OrdinalIgnoreCase)) return true;
        if (DocumentIO.DetectKind(path) != DocumentKind.Psd) return true;
        if (!_dialogs.Confirm(UnsupportedOverwriteWarning)) return false;
        _psdOverwriteConfirmed = true;
        return true;
    }

    private async Task<bool> SaveToAsync(string path)
    {
        var document = _document!;
        if (document.Width <= 0 || document.Height <= 0)
        {
            _dialogs.ShowError("画像がないため保存できません。");
            return false;
        }
        bool ok = await RunBusyAsync("保存中…", (p, ct) =>
        {
            DocumentIO.Save(document, path, p, ct);
            return true;
        }, cancelable: false);
        if (!ok) return false;
        _savedStateId = _history.StateId;
        RaiseDirtyChanged();
        _settings.AddRecent(path);
        _settings.Save();
        return true;
    }

    private async Task ExportAsync()
    {
        if (_document is not { } document || IsBusy) return;
        var path = _dialogs.ShowSaveFile("書き出し", DialogService.ExportFilter, DefaultFileName());
        if (path is null) return;
        bool trim = TrimTransparent;
        await RunBusyAsync("書き出し中…", (p, ct) =>
        {
            DocumentIO.ExportPsd(document, path, trim, p, ct);
            return true;
        }, cancelable: false);
    }

    private string DefaultFileName()
        => Path.GetFileNameWithoutExtension(_documentName ?? "無題") is { Length: > 0 } n ? n : "無題";

    private async Task<T?> RunBusyAsync<T>(string message, Func<IProgress<double>, CancellationToken, T> work, bool cancelable)
    {
        var cts = new CancellationTokenSource();
        _busyCts = cts;
        BusyMessage = message;
        BusyProgress = 0;
        BusyCancelable = cancelable;
        IsBusy = true;
        CommandManager.InvalidateRequerySuggested();
        var progress = new Progress<double>(v => { if (_busyCts == cts) BusyProgress = v; });
        try
        {
            return await Task.Run(() => work(progress, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return default;
        }
        catch (DocumentFormatException ex)
        {
            _dialogs.ShowError(ex.Message);
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            _dialogs.ShowError("ファイルにアクセスできませんでした。読み取り専用か、別のアプリで使用中の可能性があります。");
            return default;
        }
        catch (IOException ex)
        {
            _dialogs.ShowError($"ファイルの読み書きに失敗しました。\n{ex.Message}");
            return default;
        }
        catch (OutOfMemoryException)
        {
            _dialogs.ShowError("メモリが不足しているため処理できませんでした。");
            return default;
        }
        finally
        {
            _busyCts = null;
            cts.Dispose();
            IsBusy = false;
            BusyCancelable = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ---- 行の構築・選択 ----

    private LayerRow GetRow(LayerNode node)
    {
        if (!_rowMap.TryGetValue(node, out var row))
        {
            row = new LayerRow(node, this);
            _rowMap[node] = row;
        }
        return row;
    }

    private void RebuildRows()
    {
        var list = new List<LayerRow>();
        if (_document is not null) Flatten(_document.Root, 0, list);

        var visible = list.ToHashSet();
        foreach (var row in _rowMap.Values)
        {
            if (!visible.Contains(row))
            {
                row.IsSelected = false;
                row.IsEditing = false;
            }
        }
        foreach (var row in list) row.RefreshStructure();
        Rows.ReplaceAll(list);
        if (_anchor is not null && !visible.Contains(_anchor)) _anchor = null;
        if (_focusRow is not null && !visible.Contains(_focusRow)) _focusRow = null;
        RefreshEffectiveVisibility();
    }

    private void Flatten(LayerGroup group, int depth, List<LayerRow> list)
    {
        foreach (var node in group.Children)
        {
            var row = GetRow(node);
            row.Depth = depth;
            list.Add(row);
            if (node is LayerGroup { IsExpanded: true } child) Flatten(child, depth + 1, list);
        }
    }

    private void RefreshEffectiveVisibility()
    {
        foreach (var row in Rows) row.IsEffectivelyVisible = row.Node.IsEffectivelyVisible;
    }

    public IReadOnlyList<LayerRow> SelectedRows => Rows.Where(r => r.IsSelected).ToList();

    public IReadOnlyList<LayerNode> SelectedNodes => Rows.Where(r => r.IsSelected).Select(r => r.Node).ToList();

    public LayerRow? FocusRow => _focusRow;

    public void SelectSingle(LayerRow row)
    {
        foreach (var r in Rows) r.IsSelected = ReferenceEquals(r, row);
        _anchor = row;
        _focusRow = row;
        CommandManager.InvalidateRequerySuggested();
    }

    public void ToggleSelect(LayerRow row)
    {
        row.IsSelected = !row.IsSelected;
        _anchor = row;
        _focusRow = row;
        CommandManager.InvalidateRequerySuggested();
    }

    public void SelectRange(LayerRow row, bool additive)
    {
        var anchor = _anchor is not null && Rows.Contains(_anchor) ? _anchor : row;
        int a = Rows.IndexOf(anchor), b = Rows.IndexOf(row);
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        for (int i = 0; i < Rows.Count; i++)
        {
            if (i >= lo && i <= hi) Rows[i].IsSelected = true;
            else if (!additive) Rows[i].IsSelected = false;
        }
        _anchor = anchor;
        _focusRow = row;
        CommandManager.InvalidateRequerySuggested();
    }

    public void ClearSelection()
    {
        foreach (var r in Rows) r.IsSelected = false;
        CommandManager.InvalidateRequerySuggested();
    }

    private void SelectAll()
    {
        foreach (var r in Rows) r.IsSelected = true;
        CommandManager.InvalidateRequerySuggested();
    }

    private void SelectNodes(IEnumerable<LayerNode> nodes)
    {
        var set = nodes.ToHashSet();
        LayerRow? first = null;
        foreach (var r in Rows)
        {
            r.IsSelected = set.Contains(r.Node);
            if (r.IsSelected) first ??= r;
        }
        _anchor = first;
        _focusRow = first;
        if (first is not null) ScrollIntoViewRequested?.Invoke(this, first);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>キーボードでの行の移動。</summary>
    public void MoveFocus(int delta, bool extend)
    {
        if (Rows.Count == 0) return;
        int current = _focusRow is not null ? Rows.IndexOf(_focusRow) : -1;
        // 未選択のときは、下方向なら先頭の手前、上方向なら末尾の次から数える
        if (current < 0) current = delta > 0 ? -1 : Rows.Count;
        int next = Math.Clamp(current + delta, 0, Rows.Count - 1);
        var row = Rows[next];
        if (extend) SelectRange(row, additive: false);
        else SelectSingle(row);
        ScrollIntoViewRequested?.Invoke(this, row);
    }

    /// <summary>キーボードで開閉する（←→）。</summary>
    public void ExpandFocused(bool expand)
    {
        if (_focusRow is not { } row) return;
        if (row.IsGroup && row.IsExpanded != expand)
        {
            row.IsExpanded = expand;
        }
        else if (!expand && row.Node.Parent is { IsRoot: false } parent)
        {
            SelectSingle(GetRow(parent));
            ScrollIntoViewRequested?.Invoke(this, GetRow(parent));
        }
    }

    public void OnExpandedChanged(LayerRow row)
    {
        RebuildRows();
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetAllExpanded(bool expanded)
    {
        if (_document is null) return;
        foreach (var group in _document.EnumerateGroups(includeRoot: false)) group.IsExpanded = expanded;
        RebuildRows();
    }

    // ---- 表示の切替 ----

    public void SetVisible(LayerRow row, bool visible)
    {
        if (IsBusy) return;
        if (!DocumentOperations.SetVisible(row.Node, visible)) return;
        RefreshEffectiveVisibility();
        RequestRender();
    }

    /// <summary>Space キー：選択中の項目の表示を切り替える。</summary>
    public void ToggleSelectedVisibility()
    {
        if (!CanEdit) return;
        var rows = SelectedRows;
        if (rows.Count == 0) return;
        var checkRows = rows.Where(r => r.ToggleKind == RowToggleKind.CheckBox).ToList();
        bool target = checkRows.Count > 0 && !checkRows[0].Node.IsVisible;
        bool changed = false;
        foreach (var row in checkRows) changed |= DocumentOperations.SetVisible(row.Node, target);
        foreach (var row in rows.Where(r => r.ToggleKind == RowToggleKind.Radio)) changed |= DocumentOperations.SetVisible(row.Node, true);
        if (!changed) return;
        RefreshEffectiveVisibility();
        RequestRender();
    }

    // ---- 構造の編集 ----

    private void AfterEdit()
    {
        RebuildRows();
        RequestRender();
        RaiseDirtyChanged();
        OnPropertyChanged(nameof(HasCanvas));
        OnPropertyChanged(nameof(DocumentSizeText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RaiseDirtyChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
    }

    private void Undo()
    {
        if (_document is null || !_history.Undo(_document)) return;
        AfterEdit();
    }

    private void Redo()
    {
        if (_document is null || !_history.Redo(_document)) return;
        AfterEdit();
    }

    private void CreateFolder()
    {
        if (_document is not { } doc) return;
        LayerGroup? group = null;
        var selection = SelectedNodes;
        _history.Apply(doc, () =>
        {
            group = DocumentOperations.CreateGroup(doc, selection);
            return true;
        });
        if (group?.Parent is { IsRoot: false } parent) parent.IsExpanded = true;
        AfterEdit();
        if (group is null) return;
        var row = GetRow(group);
        SelectSingle(row);
        BeginRename(row);
    }

    private void ToggleRadio()
    {
        if (_document is not { } doc) return;
        var nodes = SelectedNodes;
        if (_history.Apply(doc, () => DocumentOperations.ToggleRadio(doc, nodes))) AfterEdit();
    }

    private void ToggleForced()
    {
        if (_document is not { } doc) return;
        var nodes = SelectedNodes;
        if (_history.Apply(doc, () => DocumentOperations.ToggleForced(doc, nodes))) AfterEdit();
    }

    private void DeleteSelection()
    {
        if (_document is not { } doc) return;
        var nodes = SelectedNodes;
        if (nodes.Count == 0) return;
        int firstIndex = Rows.ToList().FindIndex(r => r.IsSelected);
        _history.Apply(doc, () =>
        {
            DocumentOperations.Delete(doc, nodes);
            return true;
        });
        AfterEdit();
        if (Rows.Count > 0)
        {
            var next = Rows[Math.Clamp(firstIndex, 0, Rows.Count - 1)];
            SelectSingle(next);
        }
    }

    /// <summary>選択中の項目を移動する（レイヤー欄でのドラッグ）。</summary>
    public void MoveSelection(LayerGroup target, int index)
    {
        if (_document is not { } doc || !CanEdit) return;
        var nodes = SelectedNodes;
        if (!DocumentOperations.CanMove(doc, nodes, target)) return;
        if (!_history.Apply(doc, () => DocumentOperations.Move(doc, nodes, target, index))) return;
        if (!target.IsRoot) target.IsExpanded = true;
        AfterEdit();
        SelectNodes(nodes);
    }

    public bool CanMoveSelection(LayerGroup target)
        => _document is { } doc && DocumentOperations.CanMove(doc, SelectedNodes, target);

    public void BeginRename(LayerRow? row = null)
    {
        if (!CanEdit) return;
        row ??= _focusRow is { IsSelected: true } ? _focusRow : Rows.FirstOrDefault(r => r.IsSelected);
        if (row is null) return;
        foreach (var r in _rowMap.Values)
            if (r.IsEditing && !ReferenceEquals(r, row)) CommitRename(r);
        row.EditName = row.DisplayName;
        row.IsEditing = true;
        ScrollIntoViewRequested?.Invoke(this, row);
        RenameRequested?.Invoke(this, row);
    }

    public void CommitRename(LayerRow row)
    {
        if (!row.IsEditing) return;
        row.IsEditing = false;
        if (_document is not { } doc) return;
        var text = row.EditName;
        if (_history.Apply(doc, () => DocumentOperations.Rename(row.Node, text))) AfterEdit();
    }

    public void CancelRename(LayerRow row) => row.IsEditing = false;

    // ---- プレビュー ----

    public void RequestRender()
    {
        _renderCts?.Cancel();
        if (_document is not { Width: > 0, Height: > 0 } doc)
        {
            PreviewBitmap = null;
            if (_lastCanvas is not null) _compositor.Release(_lastCanvas);
            _lastCanvas = null;
            return;
        }

        var tree = RenderTree.Build(doc);
        int width = doc.Width, height = doc.Height;
        int version = ++_renderVersion;
        var transform = _displayTransform;
        var cts = _renderCts = new CancellationTokenSource();
        var task = Task.Run(() =>
        {
            var canvas = _compositor.Render(tree, width, height, cts.Token);
            // 合成はドキュメントの色空間で行い、表示用にだけ sRGB へ変換する
            return (Canvas: canvas, Display: transform?.ApplyPremultiplied(canvas));
        }, cts.Token);
        task.ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            var (canvas, display) = t.Result;
            if (version != _renderVersion)
            {
                _compositor.Release(canvas);
                return;
            }
            ShowCanvas(canvas, version, display);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ShowCanvas(Canvas canvas, int version, byte[]? display)
    {
        // 合成結果は毎回固定のビットマップを作って表示する。
        // ARM 版 Windows で GPU 描画すると赤と青が入れ替わる不具合は、これでは防げない（AppSettings.SoftwareRendering を参照）
        var bitmap = BitmapSource.Create(canvas.Width, canvas.Height, 96, 96, PixelFormats.Pbgra32, null, display ?? canvas.Data, canvas.Stride);
        bitmap.Freeze();
        PreviewBitmap = bitmap;
        if (_lastCanvas is not null && !ReferenceEquals(_lastCanvas, canvas)) _compositor.Release(_lastCanvas);
        _lastCanvas = canvas;
        _lastCanvasVersion = version;
    }

    /// <summary>
    /// 現在のプレビュー画像（非乗算済み・sRGB）。<paramref name="trim"/> のとき透明な余白を除く。
    /// 出力先のアプリは色プロファイルのない画像を sRGB として扱うため、ドキュメントの色空間から変換しておく。
    /// </summary>
    public PixelImage? GetPreviewImage(bool trim)
    {
        if (_document is not { Width: > 0, Height: > 0 } doc) return null;
        if (_lastCanvas is null || _lastCanvasVersion != _renderVersion)
        {
            var canvas = _compositor.Render(RenderTree.Build(doc), doc.Width, doc.Height);
            ShowCanvas(canvas, _renderVersion, _displayTransform?.ApplyPremultiplied(canvas));
        }
        var current = _lastCanvas!;
        var region = trim ? current.ComputeOpaqueBounds() : current.Rect;
        if (region.IsEmpty) region = current.Rect;
        var image = current.ToStraightImage(region);
        return _displayTransform?.Apply(image) ?? image;
    }

    public void CopyToClipboard()
    {
        var image = GetPreviewImage(trim: false);
        if (image is null) return;
        var png = PngCodec.Encode(image);
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream(png), false);
        data.SetImage(CreateOpaqueBitmap(image));
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(60);
            }
            catch (COMException)
            {
                _dialogs.ShowError("クリップボードにコピーできませんでした。しばらくしてからもう一度お試しください。");
                return;
            }
        }
    }

    /// <summary>プレビューからドラッグするデータ（image.png）を作る。</summary>
    public DataObject? CreateDragData()
    {
        var image = GetPreviewImage(TrimTransparent);
        if (image is null) return null;
        var png = PngCodec.Encode(image);
        var dir = Path.Combine(DragTempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "image.png");
        File.WriteAllBytes(file, png);
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { file });
        data.SetData("PNG", new MemoryStream(png), false);
        return data;
    }

    public static string DragTempRoot => Path.Combine(Path.GetTempPath(), "PSDToolViewer", "drag");

    public static void CleanupDragTemp()
    {
        try
        {
            if (!Directory.Exists(DragTempRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(DragTempRoot))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>透明部分を白で埋めた画像（透過に対応していない貼り付け先用）。</summary>
    private static BitmapSource CreateOpaqueBitmap(PixelImage image)
    {
        var src = image.Data;
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            int a = src[i + 3], inv = 255 - a;
            dst[i] = (byte)((src[i] * a + 255 * inv) / 255);
            dst[i + 1] = (byte)((src[i + 1] * a + 255 * inv) / 255);
            dst[i + 2] = (byte)((src[i + 2] * a + 255 * inv) / 255);
            dst[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, dst, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    // ---- 設定 ----

    public void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(ShowThumbnails));
        OnPropertyChanged(nameof(RowHeight));
    }

    // ---- 更新 ----

    /// <summary>通知バーのボタンの文字。インストール版はその場で更新、ZIP 版などはダウンロードページを開く。</summary>
    public string UpdateActionText
    {
        get => _updateActionText;
        private set => SetField(ref _updateActionText, value);
    }

    private string _updateActionText = "更新して再起動";

    /// <summary>更新のためにアプリを閉じる（未保存の確認は済んでいる）。</summary>
    public event EventHandler? CloseRequested;

    public async Task CheckForUpdatesAsync(bool manual)
    {
        try
        {
            var version = await _updates.CheckAsync();
            if (version is not null)
            {
                bool installable = _updates.CanInstallPending;
                UpdateActionText = installable ? "更新して再起動" : "ダウンロードページを開く";
                UpdateMessage = installable
                    ? $"新しいバージョン {version} があります。"
                    : $"新しいバージョン {version} があります。ダウンロードページから入手してください。";
                IsUpdateAvailable = true;
            }
            else if (manual)
            {
                _dialogs.ShowInfo($"お使いのバージョン（{UpdateService.CurrentVersionText}）は最新です。");
            }
        }
        catch (Exception ex) when (manual)
        {
            _dialogs.ShowError($"更新の確認に失敗しました。インターネットに接続されているか確認してください。\n{ex.Message}");
        }
        catch (Exception)
        {
            // 起動時の確認に失敗しても通知しない（オフラインなど）
        }
    }

    private async Task ApplyUpdateAsync()
    {
        if (!_updates.CanInstallPending)
        {
            UpdateService.OpenInBrowser(_updates.PendingPageUrl);
            return;
        }
        if (!await ConfirmDiscardAsync()) return;
        IsUpdateDownloading = true;
        UpdateMessage = "更新をダウンロードしています… 0%";
        try
        {
            var progress = new Progress<int>(p => UpdateMessage = $"更新をダウンロードしています… {p}%");
            var installer = await _updates.DownloadAsync(progress);
            UpdateMessage = "インストーラーを起動しています…";
            _settings.Save();
            UpdateService.StartInstallAndRestart(installer);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            IsUpdateDownloading = false;
            UpdateMessage = "更新のダウンロードに失敗しました。";
            _dialogs.ShowError($"更新のダウンロードに失敗しました。\n{ex.Message}");
        }
    }
}
