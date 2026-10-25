using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PSDToolViewer.Services;
using PSDToolViewer.ViewModels;

namespace PSDToolViewer.Views;

/// <summary>
/// プレビュー。ホイールまたは 2 本指のピンチで拡大縮小、中ボタン・Space＋ドラッグ・2 本指で移動、
/// 左ドラッグ（1 本指）で画像を外へドラッグする。
/// </summary>
public partial class PreviewPanel : UserControl
{
    private const double MinZoom = 0.02;
    private const double MaxZoom = 32;
    private const double FitMargin = 16;

    private static readonly double[] ZoomLevels =
        [0.02, 0.03, 0.05, 0.0625, 0.0833, 0.1, 0.125, 0.1667, 0.25, 0.3333, 0.5, 0.6667, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32];

    private MainViewModel? _vm;
    private double _zoom = 1;   // 画像 1 ピクセルあたりの画面ピクセル数
    private bool _fit = true;
    private Point _offset;      // 画像左上の位置（DIP）
    private int _imageWidth;
    private int _imageHeight;

    private bool _panning;
    private MouseButton _panButton;
    private Point _panStart;
    private Point _offsetStart;
    private bool _spacePressed;
    private bool _maybeDrag;
    private Point _dragStart;

    // 2 本指のピンチ
    private readonly List<TouchDevice> _touches = [];
    private readonly Dictionary<int, Point> _touchPositions = [];
    private bool _pinching;
    private bool _suppressTouch;
    private double _pinchStartDistance;
    private double _pinchStartZoom;
    private Point _pinchImagePoint;

    public PreviewPanel()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            if (_fit) Fit();
            else UpdateTransform();
        };
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_fit) Fit();
        else UpdateTransform();
    }

    public void Attach(MainViewModel vm)
    {
        _vm = vm;
        vm.ZoomRequested += (_, action) => OnZoomRequested(action);
        // 別のファイルを開いたら全体表示に戻す（同じ大きさの画像でも拡大率を引き継がない）
        vm.DocumentReplaced += (_, _) => Fit();
        vm.PropertyChanged += OnViewModelPropertyChanged;
        OnImageChanged();
    }

    public void SetBackgroundKind(PreviewBackgroundKind kind)
    {
        switch (kind)
        {
            case PreviewBackgroundKind.White: Host.Background = Brushes.White; break;
            case PreviewBackgroundKind.Black: Host.Background = Brushes.Black; break;
            case PreviewBackgroundKind.Gray: Host.Background = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)); break;
            default: Host.SetResourceReference(Panel.BackgroundProperty, "App.CheckerBrush"); break;
        }
    }

    public void SetSpacePressed(bool pressed)
    {
        _spacePressed = pressed;
        UpdateCursor();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PreviewBitmap)) OnImageChanged();
    }

    private void OnImageChanged()
    {
        var bitmap = _vm?.PreviewBitmap;
        bool hasImage = bitmap is not null;
        Placeholder.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        ZoomBar.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        if (bitmap is null)
        {
            _imageWidth = _imageHeight = 0;
            _fit = true;
            return;
        }
        _imageWidth = bitmap.PixelWidth;
        _imageHeight = bitmap.PixelHeight;
        PreviewImage.Width = _imageWidth;
        PreviewImage.Height = _imageHeight;
        // 利用者が拡大率を変えていなければ、キャンバスの大きさが変わっても全体が見えるようにする
        if (_fit) Fit();
        else UpdateTransform();
    }

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private double DipScale => _zoom / DpiScale;

    private Point ViewCenter => new(Host.ActualWidth / 2, Host.ActualHeight / 2);

    // ---- 拡大縮小 ----

    private void OnZoomRequested(ZoomAction action)
    {
        if (_imageWidth == 0) return;
        switch (action)
        {
            case ZoomAction.Fit:
                Fit();
                break;
            case ZoomAction.Actual:
                ZoomAt(1, ViewCenter);
                break;
            case ZoomAction.In:
                ZoomAt(ZoomLevels.FirstOrDefault(z => z > _zoom * 1.001, MaxZoom), ViewCenter);
                break;
            case ZoomAction.Out:
                ZoomAt(ZoomLevels.LastOrDefault(z => z < _zoom / 1.001, MinZoom), ViewCenter);
                break;
        }
    }

    private void Fit()
    {
        _fit = true;
        if (_imageWidth == 0 || Host.ActualWidth <= 0 || Host.ActualHeight <= 0) return;
        double availableWidth = Math.Max(1, Host.ActualWidth - FitMargin * 2);
        double availableHeight = Math.Max(1, Host.ActualHeight - FitMargin * 2);
        double dip = Math.Min(availableWidth / _imageWidth, availableHeight / _imageHeight);
        // 小さい画像は拡大せず等倍で表示する
        _zoom = Math.Clamp(Math.Min(dip * DpiScale, 1.0), MinZoom, MaxZoom);
        double s = DipScale;
        _offset = new Point((Host.ActualWidth - _imageWidth * s) / 2, (Host.ActualHeight - _imageHeight * s) / 2);
        UpdateTransform();
    }

    private void ZoomAt(double zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        double oldScale = DipScale;
        _zoom = zoom;
        double ratio = DipScale / oldScale;
        _offset = new Point(anchor.X - (anchor.X - _offset.X) * ratio, anchor.Y - (anchor.Y - _offset.Y) * ratio);
        _fit = false;
        ClampOffset();
        UpdateTransform();
    }

    /// <summary>画像が画面外へ完全に出てしまわないようにする。</summary>
    private void ClampOffset()
    {
        double s = DipScale;
        double w = _imageWidth * s, h = _imageHeight * s;
        const double keep = 48;
        double minX = Math.Min(keep - w, (Host.ActualWidth - w) / 2), maxX = Math.Max(Host.ActualWidth - keep, (Host.ActualWidth - w) / 2);
        double minY = Math.Min(keep - h, (Host.ActualHeight - h) / 2), maxY = Math.Max(Host.ActualHeight - keep, (Host.ActualHeight - h) / 2);
        _offset = new Point(Math.Clamp(_offset.X, minX, maxX), Math.Clamp(_offset.Y, minY, maxY));
    }

    private void UpdateTransform()
    {
        if (_imageWidth == 0) return;
        double dpi = DpiScale;
        double s = DipScale;
        // 等倍付近でくっきり表示されるよう、位置をデバイスピクセルに合わせる
        double x = Math.Round(_offset.X * dpi) / dpi;
        double y = Math.Round(_offset.Y * dpi) / dpi;
        PreviewImage.RenderTransform = new MatrixTransform(s, 0, 0, s, x, y);
        var mode = _zoom < 1 ? BitmapScalingMode.HighQuality : _zoom < 3 ? BitmapScalingMode.Linear : BitmapScalingMode.NearestNeighbor;
        RenderOptions.SetBitmapScalingMode(PreviewImage, mode);
        ZoomText.Text = $"{_zoom * 100:0.#}%";
    }

    // ---- マウス操作 ----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_imageWidth == 0) return;
        e.Handled = true;
        // ホイールでマウス位置を中心に拡大縮小する（タッチパッドのピンチも Ctrl＋ホイールとして届く）
        ZoomAt(_zoom * Math.Pow(1.0015, e.Delta), e.GetPosition(Host));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_imageWidth == 0) return;
        var position = e.GetPosition(Host);
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _spacePressed))
        {
            _panning = true;
            _panButton = e.ChangedButton;
            _panStart = position;
            _offsetStart = _offset;
            CaptureMouse();
            UpdateCursor();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && IsOverImage(position))
        {
            if (e.ClickCount == 2)
            {
                if (_fit) ZoomAt(1, position);
                else Fit();
                e.Handled = true;
                return;
            }
            _maybeDrag = true;
            _dragStart = position;
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(Host);
        if (_panning)
        {
            _offset = _offsetStart + (position - _panStart);
            _fit = false;
            ClampOffset();
            UpdateTransform();
            return;
        }
        if (_maybeDrag && !_pinching && e.LeftButton == MouseButtonState.Pressed)
        {
            var delta = position - _dragStart;
            // 指の場合は、ピンチの 2 本目が触れる前に書き出しが始まらないよう少し大きく動かしたときだけ始める
            double threshold = e.StylusDevice is null ? SystemParameters.MinimumHorizontalDragDistance : TouchDragThreshold;
            if (Math.Abs(delta.X) >= threshold || Math.Abs(delta.Y) >= threshold)
            {
                _maybeDrag = false;
                StartDragOut();
            }
        }
    }

    // ---- タッチ操作（2 本指で拡大縮小・移動） ----

    private const double TouchDragThreshold = 16;

    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        if (!_touches.Contains(e.TouchDevice)) _touches.Add(e.TouchDevice);
        _touchPositions[e.TouchDevice.Id] = e.GetTouchPoint(Host).Position;
        if (_touches.Count >= 2 && _imageWidth > 0)
        {
            BeginPinch();
            e.Handled = true;
        }
        else if (_suppressTouch)
        {
            e.Handled = true;
        }
        // 1 本指のときは処理済みにしない（マウス操作として扱われ、左ドラッグと同じく画像を書き出せる）
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        base.OnTouchMove(e);
        if (!_touchPositions.ContainsKey(e.TouchDevice.Id)) return;
        _touchPositions[e.TouchDevice.Id] = e.GetTouchPoint(Host).Position;
        if (_pinching)
        {
            UpdatePinch();
            e.Handled = true;
        }
        else if (_suppressTouch)
        {
            e.Handled = true;
        }
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        base.OnTouchUp(e);
        RemoveTouch(e.TouchDevice);
    }

    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        base.OnLostTouchCapture(e);
        if (_pinching) RemoveTouch(e.TouchDevice);
    }

    private void RemoveTouch(TouchDevice device)
    {
        _touches.Remove(device);
        _touchPositions.Remove(device.Id);
        if (_pinching && _touches.Count < 2)
        {
            // 残った指でそのまま書き出しが始まらないよう、全部の指が離れるまでは無視する
            _pinching = false;
            _suppressTouch = _touches.Count > 0;
            foreach (var touch in _touches) touch.Capture(null);
        }
        if (_touches.Count == 0) _suppressTouch = false;
    }

    private (Point A, Point B) PinchPoints()
        => (_touchPositions[_touches[0].Id], _touchPositions[_touches[1].Id]);

    private void BeginPinch()
    {
        _pinching = true;
        _maybeDrag = false;
        if (_panning)
        {
            _panning = false;
            ReleaseMouseCapture();
        }
        foreach (var touch in _touches) touch.Capture(this);

        var (a, b) = PinchPoints();
        _pinchStartDistance = Math.Max(1, (a - b).Length);
        _pinchStartZoom = _zoom;
        var center = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        double s = DipScale;
        // 2 本指の中点にある画像上の位置を、ピンチ中ずっと指の中点に合わせる
        _pinchImagePoint = new Point((center.X - _offset.X) / s, (center.Y - _offset.Y) / s);
        UpdateCursor();
    }

    private void UpdatePinch()
    {
        var (a, b) = PinchPoints();
        double distance = Math.Max(1, (a - b).Length);
        _zoom = Math.Clamp(_pinchStartZoom * distance / _pinchStartDistance, MinZoom, MaxZoom);
        var center = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        double s = DipScale;
        _offset = new Point(center.X - _pinchImagePoint.X * s, center.Y - _pinchImagePoint.Y * s);
        _fit = false;
        ClampOffset();
        UpdateTransform();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        _maybeDrag = false;
        if (_panning && e.ChangedButton == _panButton)
        {
            _panning = false;
            ReleaseMouseCapture();
            UpdateCursor();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _panning = false;
        UpdateCursor();
    }

    private bool IsOverImage(Point point)
    {
        double s = DipScale;
        return point.X >= _offset.X && point.Y >= _offset.Y && point.X < _offset.X + _imageWidth * s && point.Y < _offset.Y + _imageHeight * s;
    }

    private void UpdateCursor() => Cursor = _panning || _spacePressed ? Cursors.SizeAll : null;

    /// <summary>現在のプレビューを image.png としてドラッグする。</summary>
    private void StartDragOut()
    {
        if (_vm is null || _vm.IsBusy) return;
        DataObject? data;
        try
        {
            data = _vm.CreateDragData();
        }
        catch (IOException)
        {
            return;
        }
        if (data is null) return;
        _vm.IsDraggingOut = true;
        try
        {
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        finally
        {
            _vm.IsDraggingOut = false;
        }
    }
}
