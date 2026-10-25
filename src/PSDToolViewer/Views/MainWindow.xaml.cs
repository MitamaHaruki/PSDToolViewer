using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PSDToolViewer.Interop;
using PSDToolViewer.Services;
using PSDToolViewer.ViewModels;

namespace PSDToolViewer.Views;

public partial class MainWindow : Window
{
    private const string MaximizeGlyph = "";
    private const string RestoreGlyph = "";

    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private bool _closeConfirmed;
    private bool _maxButtonPressed;

    public MainWindow(MainViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        LayerPanel.Attach(vm);
        PreviewPanel.Attach(vm);

        RestorePlacement();
        ApplyLayout();
        PreviewPanel.SetBackgroundKind(settings.PreviewBackground);

        StateChanged += (_, _) => UpdateMaximizeState();
        Activated += (_, _) => TitleBar.Opacity = 1.0;
        Deactivated += (_, _) => TitleBar.Opacity = 0.6;
        ThemeService.ThemeChanged += (_, _) => PreviewPanel.SetBackgroundKind(_settings.PreviewBackground);
        vm.CloseRequested += (_, _) =>
        {
            // 更新のための終了。未保存の確認は済んでいるのでそのまま閉じる
            _closeConfirmed = true;
            SavePlacement();
            Close();
        };
    }

    // ---- ウィンドウの枠 ----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source.AddHook(WndProc);
        if (ThemeService.IsMicaSupported)
        {
            // Mica を透かして見せる
            Background = Brushes.Transparent;
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
        // 枠をクライアント領域まで広げると Windows 標準のボタンも描かれるため、システムメニューの指定を外す
        // （システムメニューはアイコンのクリックと Alt+Space で表示する）
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, (nint)((long)style & ~NativeMethods.WS_SYSMENU));
        ThemeService.ApplyToWindow(this, mica: true);
        UpdateMaximizeState();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateMaximizeState();
    }

    /// <summary>最大化時はウィンドウが画面外にはみ出すため、その分だけ内側に余白を取る。</summary>
    private void UpdateMaximizeState()
    {
        if (WindowState == WindowState.Maximized)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            uint dpiValue = (uint)Math.Round(96 * dpi.DpiScaleX);
            int padded = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXPADDEDBORDER, dpiValue);
            int frameX = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXFRAME, dpiValue) + padded;
            int frameY = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CYFRAME, dpiValue) + padded;
            double x = frameX / dpi.DpiScaleX, y = frameY / dpi.DpiScaleY;
            RootBorder.Padding = new Thickness(x, y, x, y);
            MaximizeButton.Content = RestoreGlyph;
            MaximizeButton.ToolTip = "元に戻す（縮小）";
        }
        else
        {
            RootBorder.Padding = new Thickness(0);
            MaximizeButton.Content = MaximizeGlyph;
            MaximizeButton.ToolTip = "最大化";
        }
    }

    /// <summary>
    /// 最大化ボタンの位置で HTMAXBUTTON を返し、Windows 11 のスナップレイアウトを表示させる。
    /// この場合 WPF にはマウスイベントが届かないため、押下と表示状態をここで扱う。
    /// </summary>
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_NCHITTEST:
                if (IsOverMaximizeButton(lParam))
                {
                    MaximizeButton.Tag = _maxButtonPressed ? "pressed" : "hover";
                    handled = true;
                    return NativeMethods.HTMAXBUTTON;
                }
                ClearMaximizeHover();
                break;
            case NativeMethods.WM_NCLBUTTONDOWN:
            case NativeMethods.WM_NCLBUTTONDBLCLK:
                if (wParam == NativeMethods.HTMAXBUTTON)
                {
                    _maxButtonPressed = true;
                    MaximizeButton.Tag = "pressed";
                    handled = true;
                }
                break;
            case NativeMethods.WM_NCLBUTTONUP:
                if (wParam == NativeMethods.HTMAXBUTTON)
                {
                    if (_maxButtonPressed) ToggleMaximize();
                    _maxButtonPressed = false;
                    MaximizeButton.Tag = null;
                    handled = true;
                }
                break;
            case NativeMethods.WM_NCMOUSELEAVE:
            case NativeMethods.WM_MOUSEMOVE:
                _maxButtonPressed = false;
                ClearMaximizeHover();
                break;
        }
        return 0;
    }

    private bool IsOverMaximizeButton(nint lParam)
    {
        if (!MaximizeButton.IsVisible) return false;
        try
        {
            var point = MaximizeButton.PointFromScreen(new Point(NativeMethods.GetX(lParam), NativeMethods.GetY(lParam)));
            return point.X >= 0 && point.Y >= 0 && point.X < MaximizeButton.ActualWidth && point.Y < MaximizeButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ClearMaximizeHover()
    {
        if (MaximizeButton.Tag is not null) MaximizeButton.Tag = null;
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void AppIcon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Close();
            return;
        }
        var point = AppIconImage.PointToScreen(new Point(0, AppIconImage.ActualHeight + 8));
        var dpi = VisualTreeHelper.GetDpi(this);
        SystemCommands.ShowSystemMenu(this, new Point(point.X / dpi.DpiScaleX, point.Y / dpi.DpiScaleY));
        e.Handled = true;
    }

    // ---- 配置・設定 ----

    private void RestorePlacement()
    {
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top)
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(new Rect(left, top, Width, Height)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settings.LayerPanelWidth = CurrentLayerPanelWidth;
        _settings.Save();
    }

    private double CurrentLayerPanelWidth => (_settings.SwapPanels ? SecondColumn : FirstColumn).ActualWidth is > 0 and var w ? w : _settings.LayerPanelWidth;

    /// <summary>レイヤー欄とプレビューの左右を設定に合わせる。</summary>
    private void ApplyLayout()
    {
        bool swap = _settings.SwapPanels;
        var layerWidth = new GridLength(Math.Max(180, _settings.LayerPanelWidth));
        var star = new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(LayerPanel, swap ? 2 : 0);
        Grid.SetColumn(PreviewPanel, swap ? 0 : 2);
        FirstColumn.Width = swap ? star : layerWidth;
        FirstColumn.MinWidth = swap ? 200 : 180;
        SecondColumn.Width = swap ? layerWidth : star;
        SecondColumn.MinWidth = swap ? 180 : 200;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _settings.LayerPanelWidth = CurrentLayerPanelWidth;
        dialog.ApplyTo(_settings);
        _settings.Save();
        ApplyRenderMode();
        ThemeService.Apply(_settings.Theme);
        ApplyLayout();
        PreviewPanel.SetBackgroundKind(_settings.PreviewBackground);
        _vm.OnSettingsChanged();
    }

    /// <summary>描画方法（GPU／ソフトウェア）を設定に合わせる。開いているウィンドウにもすぐ反映する。</summary>
    private void ApplyRenderMode()
    {
        var mode = _settings.SoftwareRendering ? RenderMode.SoftwareOnly : RenderMode.Default;
        RenderOptions.ProcessRenderMode = mode;
        foreach (Window window in Application.Current.Windows)
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.CompositionTarget.RenderMode = mode;
    }

    private void About_Click(object sender, RoutedEventArgs e)
        => new AboutWindow { Owner = this }.ShowDialog();

    private void ColorHelp_Click(object sender, RoutedEventArgs e)
        => new ColorHelpWindow { Owner = this }.ShowDialog();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void RecentMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, RecentMenu)) return;
        RecentMenu.Items.Clear();
        if (_settings.RecentFiles.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "（なし）", IsEnabled = false });
            return;
        }
        int number = 1;
        foreach (var path in _settings.RecentFiles)
        {
            RecentMenu.Items.Add(new MenuItem
            {
                Header = new TextBlock { Text = $"{number++}. {path}", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 },
                Command = _vm.OpenRecentCommand,
                CommandParameter = path,
                ToolTip = path,
            });
        }
        RecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "一覧をクリア" };
        clear.Click += (_, _) =>
        {
            _settings.RecentFiles.Clear();
            _settings.Save();
        };
        RecentMenu.Items.Add(clear);
    }

    // ---- 終了 ----

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            base.OnClosing(e);
            return;
        }
        if (_vm.IsBusy)
        {
            e.Cancel = true;
            return;
        }
        if (_vm.IsDirty)
        {
            e.Cancel = true;
            if (await _vm.ConfirmDiscardAsync())
            {
                _closeConfirmed = true;
                SavePlacement();
                // 閉じる処理の途中では Close を呼べないため、処理が終わってから閉じる
                _ = Dispatcher.BeginInvoke(Close);
            }
            return;
        }
        SavePlacement();
        _closeConfirmed = true;
        base.OnClosing(e);
    }

    // ---- ファイルのドロップ ----

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (e.Handled) return;
        e.Effects = !_vm.IsDraggingOut && !_vm.IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDragEnter(DragEventArgs e) => OnDragOver(e);

    protected override async void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (e.Handled || _vm.IsDraggingOut) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        e.Handled = true;
        Activate();
        await _vm.HandleDroppedPathsAsync(paths);
    }

    // ---- Space＋ドラッグでのプレビュー移動 ----

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.System && e.SystemKey == Key.Space)
        {
            var point = TitleBar.PointToScreen(new Point(0, TitleBar.ActualHeight));
            var dpi = VisualTreeHelper.GetDpi(this);
            SystemCommands.ShowSystemMenu(this, new Point(point.X / dpi.DpiScaleX, point.Y / dpi.DpiScaleY));
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox && PreviewPanel.IsMouseOver)
        {
            PreviewPanel.SetSpacePressed(true);
            e.Handled = true;
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key == Key.Space) PreviewPanel.SetSpacePressed(false);
    }
}
