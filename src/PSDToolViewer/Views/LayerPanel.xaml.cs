using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PSDToolViewer.Core.Formats;
using PSDToolViewer.Core.Model;
using PSDToolViewer.ViewModels;

namespace PSDToolViewer.Views;

/// <summary>
/// レイヤー欄。選択（Ctrl／Shift）とドラッグでの移動は独自に扱う。
/// </summary>
public partial class LayerPanel : UserControl
{
    private const string RowDragFormat = "PSDToolViewer.LayerRows";
    private const double ExpanderWidth = 22;
    private const double IndentWidth = 16;
    private const double RowMargin = 4;

    private MainViewModel? _vm;
    private LayerRow? _pressedRow;
    private LayerRow? _deferredSelect;
    private Point _pressPoint;
    private bool _dragging;

    public LayerPanel()
    {
        InitializeComponent();
    }

    public void Attach(MainViewModel vm)
    {
        _vm = vm;
        vm.ScrollIntoViewRequested += (_, row) => ScrollIntoView(row);
        vm.RenameRequested += (_, row) => FocusNameEditor(row);
        vm.PropertyChanged += OnViewModelPropertyChanged;
        UpdateEmptyText();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.HasDocument)) UpdateEmptyText();
    }

    private void UpdateEmptyText()
        => EmptyText.Visibility = _vm?.HasDocument == true ? Visibility.Collapsed : Visibility.Visible;

    // ---- 選択 ----

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || IsInside<TextBox>(e.OriginalSource)) return;
        var row = RowFromSource(e.OriginalSource);
        List.Focus();
        if (row is null)
        {
            if (Keyboard.Modifiers == ModifierKeys.None && !IsInside<ScrollBar>(e.OriginalSource)) _vm.ClearSelection();
            return;
        }
        // チェックボックス・ラジオボタン・開閉ボタンはそのまま操作させる（選択は変えない）
        if (IsInside<ToggleButton>(e.OriginalSource)) return;

        if (e.ClickCount == 2 && IsNameElement(e.OriginalSource))
        {
            _vm.SelectSingle(row);
            _vm.BeginRename(row);
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Shift))
            _vm.SelectRange(row, additive: modifiers.HasFlag(ModifierKeys.Control));
        else if (modifiers.HasFlag(ModifierKeys.Control))
            _vm.ToggleSelect(row);
        else if (row.IsSelected)
            _deferredSelect = row; // 複数選択のままドラッグできるよう、離したときに単独選択にする
        else
            _vm.SelectSingle(row);

        _pressedRow = row;
        _pressPoint = e.GetPosition(List);
        e.Handled = true;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm is null || _pressedRow is null || _dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(List) - _pressPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!_pressedRow.IsSelected || !_vm.CanEdit) return;

        _dragging = true;
        try
        {
            DragDrop.DoDragDrop(List, new DataObject(RowDragFormat, RowDragFormat), DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            _pressedRow = null;
            _deferredSelect = null;
            HideDropIndicator();
        }
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSelect is not null && !_dragging) _vm?.SelectSingle(_deferredSelect);
        _deferredSelect = null;
        _pressedRow = null;
    }

    private void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        List.Focus();
        var row = RowFromSource(e.OriginalSource);
        if (row is not null && !row.IsSelected) _vm.SelectSingle(row);
    }

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_vm is null || !_vm.HasDocument) e.Handled = true;
    }

    // ---- キーボード ----

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null || e.OriginalSource is TextBox) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Up: _vm.MoveFocus(-1, shift); break;
            case Key.Down: _vm.MoveFocus(1, shift); break;
            case Key.PageUp: _vm.MoveFocus(-10, shift); break;
            case Key.PageDown: _vm.MoveFocus(10, shift); break;
            case Key.Home: _vm.MoveFocus(-_vm.Rows.Count, shift); break;
            case Key.End: _vm.MoveFocus(_vm.Rows.Count, shift); break;
            case Key.Left: _vm.ExpandFocused(false); break;
            case Key.Right: _vm.ExpandFocused(true); break;
            case Key.Space: _vm.ToggleSelectedVisibility(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- 名前の変更 ----

    /// <summary>名前の入力欄にフォーカスを移す。行を作り直した直後は表示を待ってから行う。</summary>
    private void FocusNameEditor(LayerRow row)
    {
        if (TryFocusNameEditor(row)) return;
        Dispatcher.BeginInvoke(() => TryFocusNameEditor(row), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private bool TryFocusNameEditor(LayerRow row)
    {
        if (!row.IsEditing || _vm is null) return true;
        List.UpdateLayout();
        if (List.ItemContainerGenerator.ContainerFromItem(row) is not DependencyObject container) return false;
        if (FindChild<TextBox>(container) is not { IsVisible: true } box) return false;
        box.Focus();
        box.SelectAll();
        return box.IsKeyboardFocused;
    }

    private void NameEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null || sender is not TextBox { DataContext: LayerRow row }) return;
        if (e.Key == Key.Enter)
        {
            _vm.CommitRename(row);
            List.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.CancelRename(row);
            List.Focus();
            e.Handled = true;
        }
    }

    private void NameEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm is not null && sender is TextBox { DataContext: LayerRow { IsEditing: true } row })
            _vm.CommitRename(row);
    }

    // ---- ドラッグ＆ドロップ ----

    private sealed record DropTarget(LayerGroup Group, int Index);

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (_vm is null || _vm.IsBusy || _vm.IsDraggingOut)
        {
            HideDropIndicator();
            return;
        }
        AutoScroll(e.GetPosition(List));

        if (e.Data.GetDataPresent(RowDragFormat))
        {
            var target = ComputeTarget(e.GetPosition(List), showIndicator: true);
            if (target is not null && _vm.CanMoveSelection(target.Group)) e.Effects = DragDropEffects.Move;
            else HideDropIndicator();
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            bool hasDocument = paths.Any(p => DocumentIO.DetectKind(p) is DocumentKind.Psd or DocumentKind.Ptv or DocumentKind.Folder);
            bool hasPng = paths.Any(p => DocumentIO.DetectKind(p) == DocumentKind.Png);
            if (hasDocument || !hasPng || !_vm.HasDocument) HideDropIndicator();
            else ComputeTarget(e.GetPosition(List), showIndicator: true);
            e.Effects = hasDocument || hasPng ? DragDropEffects.Copy : DragDropEffects.None;
        }
    }

    private void List_DragLeave(object sender, DragEventArgs e) => HideDropIndicator();

    private async void List_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideDropIndicator();
        if (_vm is null || _vm.IsBusy || _vm.IsDraggingOut) return;

        if (e.Data.GetDataPresent(RowDragFormat))
        {
            if (ComputeTarget(e.GetPosition(List), showIndicator: false) is { } target)
                _vm.MoveSelection(target.Group, target.Index);
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            Window.GetWindow(this)?.Activate();
            var target = _vm.HasDocument ? ComputeTarget(e.GetPosition(List), showIndicator: false) : null;
            await _vm.HandleDroppedPathsAsync(paths, target?.Group, target?.Index ?? int.MaxValue);
        }
    }

    /// <summary>
    /// マウス位置からドロップ先を求める。フォルダの中央なら中へ、行の上半分なら前、下半分なら後ろ。
    /// </summary>
    private DropTarget? ComputeTarget(Point point, bool showIndicator)
    {
        if (_vm?.Document is not { } document) return null;

        var (row, bounds) = RowAt(point);
        if (row is null)
        {
            // 行のない所：一番下
            var last = LastRealizedRowBounds();
            if (showIndicator) ShowDropLine(0, last?.Bottom ?? 4);
            return new DropTarget(document.Root, int.MaxValue);
        }

        var node = row.Node;
        var parent = node.Parent ?? document.Root;
        int index = parent.IndexOf(node);
        double rel = (point.Y - bounds.Top) / Math.Max(1, bounds.Height);

        if (node is LayerGroup group && rel >= 0.25 && rel <= 0.75)
        {
            if (showIndicator) ShowDropBox(bounds, row.Depth);
            return new DropTarget(group, 0);
        }
        if (rel < 0.5)
        {
            if (showIndicator) ShowDropLine(row.Depth, bounds.Top);
            return new DropTarget(parent, index);
        }
        if (node is LayerGroup { IsExpanded: true, Children.Count: > 0 } expanded)
        {
            if (showIndicator) ShowDropLine(row.Depth + 1, bounds.Bottom);
            return new DropTarget(expanded, 0);
        }
        if (showIndicator) ShowDropLine(row.Depth, bounds.Bottom);
        return new DropTarget(parent, index + 1);
    }

    private (LayerRow? Row, Rect Bounds) RowAt(Point point)
    {
        if (_vm is null) return (null, Rect.Empty);
        foreach (var row in _vm.Rows)
        {
            if (List.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement container || !container.IsVisible) continue;
            var bounds = container.TransformToAncestor(List).TransformBounds(new Rect(container.RenderSize));
            if (point.Y >= bounds.Top && point.Y < bounds.Bottom) return (row, bounds);
        }
        return (null, Rect.Empty);
    }

    private Rect? LastRealizedRowBounds()
    {
        if (_vm is null) return null;
        for (int i = _vm.Rows.Count - 1; i >= 0; i--)
        {
            if (List.ItemContainerGenerator.ContainerFromItem(_vm.Rows[i]) is FrameworkElement { IsVisible: true } container)
                return container.TransformToAncestor(List).TransformBounds(new Rect(container.RenderSize));
        }
        return null;
    }

    private void ShowDropLine(int depth, double y)
    {
        double x = RowMargin + depth * IndentWidth + ExpanderWidth;
        DropLine.Width = Math.Max(20, List.ActualWidth - x - RowMargin - 4);
        Canvas.SetLeft(DropLine, x);
        Canvas.SetTop(DropLine, Math.Max(0, y - 1));
        DropLine.Visibility = Visibility.Visible;
        DropBox.Visibility = Visibility.Collapsed;
    }

    private void ShowDropBox(Rect bounds, int depth)
    {
        double x = RowMargin + depth * IndentWidth;
        DropBox.Width = Math.Max(20, List.ActualWidth - x - RowMargin - 4);
        DropBox.Height = bounds.Height;
        Canvas.SetLeft(DropBox, x);
        Canvas.SetTop(DropBox, bounds.Top);
        DropBox.Visibility = Visibility.Visible;
        DropLine.Visibility = Visibility.Collapsed;
    }

    private void HideDropIndicator()
    {
        DropLine.Visibility = Visibility.Collapsed;
        DropBox.Visibility = Visibility.Collapsed;
    }

    private void AutoScroll(Point point)
    {
        if (FindChild<ScrollViewer>(List) is not { } scroller) return;
        const double edge = 24;
        if (point.Y < edge) scroller.ScrollToVerticalOffset(scroller.VerticalOffset - 8);
        else if (point.Y > List.ActualHeight - edge) scroller.ScrollToVerticalOffset(scroller.VerticalOffset + 8);
    }

    // ---- 表示位置 ----

    private void ScrollIntoView(LayerRow row)
    {
        if (_vm is null) return;
        int index = _vm.Rows.IndexOf(row);
        if (index < 0) return;
        if (List.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
        {
            container.BringIntoView();
            return;
        }
        if (FindChild<VirtualizingStackPanel>(List) is { } panel)
        {
            panel.BringIndexIntoViewPublic(index);
        }
    }

    // ---- 補助 ----

    private static LayerRow? RowFromSource(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = GetParent(d))
        {
            if (d is FrameworkElement { DataContext: LayerRow row }) return row;
            if (d is ItemsControl) return null;
        }
        return null;
    }

    private static bool IsInside<T>(object source) where T : DependencyObject
    {
        for (var d = source as DependencyObject; d is not null; d = GetParent(d))
        {
            if (d is T) return true;
            if (d is ItemsControl) return false;
        }
        return false;
    }

    private static bool IsNameElement(object source)
        => source is TextBlock { Name: "NameText" } || (source is DependencyObject d && GetParent(d) is TextBlock { Name: "NameText" });

    private static DependencyObject? GetParent(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
