using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Kiriha.Models;

/// <summary>
/// ファイル一覧の表示コレクション。
///
/// 一覧を丸ごと差し替えると ListBox は全行のコンテナを作り直す（＝スクロール位置・選択・
/// 進行中のクリックが巻き添えになる）。フォルダー監視で 1 件増減しただけの更新では、
/// 増減した行だけを Add / Remove / Move で通知したいので、通常の
/// <see cref="ObservableCollection{T}"/> に「まとめて置換して 1 回だけ Reset を出す」
/// <see cref="Reset"/> を足してある。移動やソートのように全体が変わるときだけそちらを使う。
/// </summary>
public sealed class FileEntryCollection : ObservableCollection<FileSystemEntry>
{
    /// <summary>中身をまとめて置き換え、通知は Reset 1 回だけにする。</summary>
    public void Reset(IReadOnlyList<FileSystemEntry> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
