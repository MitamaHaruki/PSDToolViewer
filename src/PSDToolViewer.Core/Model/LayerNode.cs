using PSDToolViewer.Core.Imaging;

namespace PSDToolViewer.Core.Model;

/// <summary>レイヤーまたはフォルダ。</summary>
public abstract class LayerNode : ObservableObject
{
    private string _internalName;
    private bool _isVisible = true;

    protected LayerNode(string internalName)
    {
        _internalName = internalName;
    }

    /// <summary>出力用の名前。先頭に PSDTool の記号（<c>*</c> / <c>!</c>）を含む。</summary>
    public string InternalName
    {
        get => _internalName;
        set
        {
            if (!SetField(ref _internalName, value)) return;
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(IsRadio));
            OnPropertyChanged(nameof(IsForced));
            OnPropertyChanged(nameof(IsShown));
        }
    }

    /// <summary>画面表示用の名前（記号なし）。</summary>
    public string DisplayName => LayerNaming.ToDisplayName(_internalName);

    public bool IsRadio => LayerNaming.HasRadioMark(_internalName);
    public bool IsForced => LayerNaming.HasForcedMark(_internalName);

    /// <summary>表示フラグ。強制表示の場合は合成時に無視される。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetField(ref _isVisible, value)) OnPropertyChanged(nameof(IsShown));
        }
    }

    /// <summary>このノード自身が表示されるか（親の状態は含まない）。</summary>
    public bool IsShown => IsForced || _isVisible;

    public byte Opacity { get; set; } = 255;
    public byte FillOpacity { get; set; } = 255;
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;
    public bool Clipping { get; set; }
    public LayerMask? Mask { get; set; }

    public LayerGroup? Parent { get; internal set; }

    public abstract bool IsGroup { get; }

    /// <summary>親を含めて表示されるか。</summary>
    public bool IsEffectivelyVisible
    {
        get
        {
            for (LayerNode? n = this; n is not null && !(n is LayerGroup { IsRoot: true }); n = n.Parent)
                if (!n.IsShown) return false;
            return true;
        }
    }

    /// <summary>ルート直下を 0 とした階層の深さ。</summary>
    public int Depth
    {
        get
        {
            int depth = 0;
            for (var p = Parent; p is not null && !p.IsRoot; p = p.Parent) depth++;
            return depth;
        }
    }

    public bool IsDescendantOf(LayerNode ancestor)
    {
        for (var p = Parent; p is not null; p = p.Parent)
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }

    public override string ToString() => _internalName;
}
