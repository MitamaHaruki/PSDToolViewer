using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PSDToolViewer.ViewModels;

/// <summary>中身をまとめて置き換えられるコレクション（通知は 1 回だけ）。</summary>
public sealed class RowCollection : ObservableCollection<LayerRow>
{
    public void ReplaceAll(IReadOnlyList<LayerRow> rows)
    {
        if (rows.Count == Count && rows.SequenceEqual(this)) return;
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
