using NRadioManager.Infrastructure;

namespace NRadioManager.ViewModels;

/// <summary>左侧导航项：承载图标、标题与对应模块。</summary>
public sealed class NavItem : ObservableObject
{
    public NavItem(string title, string glyph, ModuleViewModel module, bool isSettings = false)
    {
        Title = title;
        Glyph = glyph;
        Module = module;
        IsSettings = isSettings;
    }

    public string Title { get; }
    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets 字形。</summary>
    public string Glyph { get; }
    public ModuleViewModel Module { get; }
    public bool IsSettings { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}
