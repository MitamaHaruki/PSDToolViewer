using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using PSDToolViewer.Core.Model;

namespace PSDToolViewer.ViewModels;

public enum RowToggleKind
{
    CheckBox,
    Radio,
    Forced,
}

/// <summary>レイヤー欄の 1 行。</summary>
public sealed class LayerRow : ObservableObject
{
    private readonly MainViewModel _owner;
    private int _depth;
    private bool _isSelected;
    private bool _isEditing;
    private string _editName = string.Empty;
    private bool _isEffectivelyVisible = true;
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;

    public LayerRow(LayerNode node, MainViewModel owner)
    {
        Node = node;
        _owner = owner;
        node.PropertyChanged += OnNodePropertyChanged;
    }

    public LayerNode Node { get; }

    public bool IsGroup => Node is LayerGroup;

    public int Depth
    {
        get => _depth;
        set
        {
            if (SetField(ref _depth, value)) OnPropertyChanged(nameof(Indent));
        }
    }

    public Thickness Indent => new(_depth * 16, 0, 0, 0);

    public bool HasChildren => Node is LayerGroup { Children.Count: > 0 };

    public bool IsExpanded
    {
        get => Node is LayerGroup { IsExpanded: true };
        set
        {
            if (Node is LayerGroup group && group.IsExpanded != value)
            {
                group.IsExpanded = value;
                _owner.OnExpandedChanged(this);
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        set => SetField(ref _isEditing, value);
    }

    public string EditName
    {
        get => _editName;
        set => SetField(ref _editName, value);
    }

    public string DisplayName => Node.DisplayName;

    public RowToggleKind ToggleKind => Node.IsForced ? RowToggleKind.Forced : Node.IsRadio ? RowToggleKind.Radio : RowToggleKind.CheckBox;

    /// <summary>チェックボックス／ラジオボタンの状態。変更は ViewModel 経由で行う。</summary>
    public bool IsChecked
    {
        get => Node.IsVisible;
        set
        {
            _owner.SetVisible(this, value);
            OnPropertyChanged();
        }
    }

    /// <summary>親を含めて表示されているか（非表示なら薄く表示する）。</summary>
    public bool IsEffectivelyVisible
    {
        get => _isEffectivelyVisible;
        set => SetField(ref _isEffectivelyVisible, value);
    }

    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested && Node is LayerItem)
            {
                _thumbnailRequested = true;
                _owner.Thumbnails.Request(this);
            }
            return _thumbnail;
        }
        set => SetField(ref _thumbnail, value);
    }

    public void RefreshStructure()
    {
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(IsExpanded));
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LayerNode.InternalName):
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(ToggleKind));
                break;
            case nameof(LayerNode.IsVisible):
                OnPropertyChanged(nameof(IsChecked));
                break;
            case nameof(LayerGroup.IsExpanded):
                OnPropertyChanged(nameof(IsExpanded));
                break;
        }
    }

    public void Detach() => Node.PropertyChanged -= OnNodePropertyChanged;

    /// <summary>読み上げソフトなどに行の名前として伝わる。</summary>
    public override string ToString() => DisplayName;
}
