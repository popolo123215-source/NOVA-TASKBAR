using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace Nova.WindowsClient;

internal sealed class WidgetDefinition(
    string id,
    string title,
    string subtitle,
    string glyph,
    FrameworkElement view,
    Action? onActivated = null,
    Action? onDeactivated = null) : INotifyPropertyChanged
{
    private bool _isSelected;
    private double _iconSize = 24;
    private bool _isVisible = true;

    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string Glyph { get; } = glyph;
    public FrameworkElement View { get; } = view;
    public Action? OnActivated { get; } = onActivated;
    public Action? OnDeactivated { get; } = onDeactivated;
    public double IconSize => _iconSize;
    public double NavigationButtonHeight => Math.Max(48, _iconSize + 16);
    public bool IsSelected
    {
        get => _isSelected;
        private set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }
    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged();
        }
    }

    internal void SetSelected(bool selected) => IsSelected = selected;

    internal void SetIconSize(double size)
    {
        size = Math.Clamp(size, 20, 50);
        if (Math.Abs(_iconSize - size) < 0.01) return;
        _iconSize = size;
        OnPropertyChanged(nameof(IconSize));
        OnPropertyChanged(nameof(NavigationButtonHeight));
    }

    internal void SetVisible(bool visible) => IsVisible = visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class WidgetRegistry
{
    private readonly Dictionary<string, WidgetDefinition> _byId = new(StringComparer.Ordinal);

    public ObservableCollection<WidgetDefinition> Widgets { get; } = [];
    public WidgetDefinition? SelectedWidget { get; private set; }

    public void Register(WidgetDefinition widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        if (string.IsNullOrWhiteSpace(widget.Id))
            throw new ArgumentException("Widget ID cannot be empty.", nameof(widget));
        if (_byId.ContainsKey(widget.Id))
            throw new InvalidOperationException($"Widget '{widget.Id}' is already registered.");

        _byId.Add(widget.Id, widget);
        Widgets.Add(widget);
    }

    public void SetIconSize(double size)
    {
        size = Math.Clamp(size, 20, 50);
        foreach (var widget in Widgets)
            widget.SetIconSize(size);
    }

    public void SetVisibleWidgets(IReadOnlyCollection<string>? ids)
    {
        foreach (var widget in Widgets)
            widget.SetVisible(ids is null || ids.Contains(widget.Id, StringComparer.OrdinalIgnoreCase));
    }

    public WidgetDefinition Select(string id)
    {
        if (!_byId.TryGetValue(id, out var widget))
            throw new KeyNotFoundException($"Widget '{id}' is not registered.");

        SelectedWidget?.SetSelected(false);
        SelectedWidget = widget;
        widget.SetSelected(true);
        return widget;
    }
}

internal sealed class WidgetHost
{
    private readonly Grid _pageRoot;
    private readonly WidgetRegistry _registry;

    public WidgetHost(Grid pageRoot, WidgetRegistry registry)
    {
        _pageRoot = pageRoot;
        _registry = registry;
    }

    public WidgetDefinition? SelectedWidget => _registry.SelectedWidget;

    public WidgetDefinition Select(string id, bool activate = true)
    {
        var previous = _registry.SelectedWidget;
        var selected = _registry.Select(id);

        if (!ReferenceEquals(previous, selected))
            previous?.OnDeactivated?.Invoke();

        foreach (UIElement page in _pageRoot.Children)
            page.Visibility = Visibility.Collapsed;

        if (!ReferenceEquals(selected.View.Parent, _pageRoot))
            throw new InvalidOperationException($"Widget '{selected.Id}' is not a direct child of its host.");

        selected.View.Visibility = Visibility.Visible;
        if (activate) selected.OnActivated?.Invoke();
        return selected;
    }
}
