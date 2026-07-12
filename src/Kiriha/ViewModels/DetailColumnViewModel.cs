using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kiriha.ViewModels;

/// <summary>詳細表示の1カラム。幅・表示状態・ソート表示を所有タブへ中継する。</summary>
public partial class DetailColumnViewModel : ObservableObject
{
    public DetailColumnViewModel(TabViewModel owner, string key, string title)
    {
        Owner = owner;
        Key = key;
        Title = title;
        owner.PropertyChanged += Owner_PropertyChanged;
    }

    public TabViewModel Owner { get; }
    public string Key { get; }
    public string Title { get; }

    [ObservableProperty] private bool _isDragging;
    [ObservableProperty] private bool _isDropTarget;

    public bool IsName => Key == "Name";
    public bool IsModified => Key == "Modified";
    public bool IsCreated => Key == "Created";
    public bool IsType => Key == "Type";
    public bool IsSize => Key == "Size";

    public double Width
    {
        get => Key switch
        {
            "Name" => Owner.ColNameWidth,
            "Modified" => Owner.ColModifiedWidth,
            "Created" => Owner.ColCreatedWidth,
            "Type" => Owner.ColTypeWidth,
            "Size" => Owner.ColSizeWidth,
            _ => 100,
        };
        set
        {
            switch (Key)
            {
                case "Name": Owner.ColNameWidth = Math.Max(100, value); break;
                case "Modified": Owner.ColModifiedWidth = Math.Max(60, value); break;
                case "Created": Owner.ColCreatedWidth = Math.Max(60, value); break;
                case "Type": Owner.ColTypeWidth = Math.Max(60, value); break;
                case "Size": Owner.ColSizeWidth = Math.Max(60, value); break;
            }
        }
    }

    public bool IsVisible => Key switch
    {
        "Name" => true,
        "Modified" => Owner.ShowColModified,
        "Created" => Owner.ShowColCreated,
        "Type" => Owner.ShowColType,
        "Size" => Owner.ShowColSize,
        _ => false,
    };

    public string SortGlyph => Key switch
    {
        "Name" => Owner.NameSortGlyph,
        "Modified" => Owner.ModifiedSortGlyph,
        "Created" => Owner.CreatedSortGlyph,
        "Type" => Owner.TypeSortGlyph,
        "Size" => Owner.SizeSortGlyph,
        _ => "",
    };

    private void Owner_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.ColNameWidth) or nameof(TabViewModel.ColModifiedWidth)
            or nameof(TabViewModel.ColCreatedWidth) or nameof(TabViewModel.ColTypeWidth) or nameof(TabViewModel.ColSizeWidth))
            OnPropertyChanged(nameof(Width));

        if (e.PropertyName is nameof(TabViewModel.ShowColModified) or nameof(TabViewModel.ShowColCreated)
            or nameof(TabViewModel.ShowColType) or nameof(TabViewModel.ShowColSize))
            OnPropertyChanged(nameof(IsVisible));

        if (e.PropertyName is nameof(TabViewModel.SortKey) or nameof(TabViewModel.SortAscendingFlag))
            OnPropertyChanged(nameof(SortGlyph));
    }

    public void Detach() => Owner.PropertyChanged -= Owner_PropertyChanged;
}
