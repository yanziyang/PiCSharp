namespace Pi.Tui;

/// <summary>One selectable item displayed by <see cref="SelectList"/>.</summary>
public sealed record SelectItem
{
    /// <summary>Value returned when the item is selected.</summary>
    public required string Value { get; init; }

    /// <summary>Primary display label.</summary>
    public required string Label { get; init; }

    /// <summary>Optional explanatory text displayed beside the label.</summary>
    public string? Description { get; init; }
}

/// <summary>Styling callbacks used by <see cref="SelectList"/>.</summary>
public sealed class SelectListTheme
{
    /// <summary>Styles a selected prefix.</summary>
    public required Func<string, string> SelectedPrefix { get; init; }

    /// <summary>Styles a selected row.</summary>
    public required Func<string, string> SelectedText { get; init; }

    /// <summary>Styles an unselected description.</summary>
    public required Func<string, string> Description { get; init; }

    /// <summary>Styles the scroll-position row.</summary>
    public required Func<string, string> ScrollInfo { get; init; }

    /// <summary>Styles the message displayed when the filter has no matches.</summary>
    public required Func<string, string> NoMatch { get; init; }
}

/// <summary>Context supplied to a custom primary-column truncation callback.</summary>
public sealed record SelectListTruncatePrimaryContext
{
    /// <summary>Primary text before truncation.</summary>
    public required string Text { get; init; }

    /// <summary>Maximum visible width available to the returned text.</summary>
    public required int MaxWidth { get; init; }

    /// <summary>Width reserved for the complete primary column.</summary>
    public required int ColumnWidth { get; init; }

    /// <summary>Item being rendered.</summary>
    public required SelectItem Item { get; init; }

    /// <summary>Whether the item is selected.</summary>
    public required bool IsSelected { get; init; }
}

/// <summary>Optional primary-column layout controls for <see cref="SelectList"/>.</summary>
public sealed class SelectListLayoutOptions
{
    /// <summary>Minimum primary-column width.</summary>
    public int? MinPrimaryColumnWidth { get; init; }

    /// <summary>Maximum primary-column width.</summary>
    public int? MaxPrimaryColumnWidth { get; init; }

    /// <summary>Optional custom primary-text truncation callback.</summary>
    public Func<SelectListTruncatePrimaryContext, string>? TruncatePrimary { get; init; }
}

/// <summary>Scrollable, filterable terminal selection list.</summary>
public class SelectList : IComponent
{
    private const int _defaultPrimaryColumnWidth = 32;
    private const int _primaryColumnGap = 2;
    private const int _minDescriptionWidth = 10;

    private readonly IReadOnlyList<SelectItem> _items;
    private IReadOnlyList<SelectItem> _filteredItems;
    private int _selectedIndex;
    private readonly int _maxVisible;
    private readonly SelectListTheme _theme;
    private readonly SelectListLayoutOptions _layout;

    /// <summary>Creates a selection list.</summary>
    public SelectList(
        IReadOnlyList<SelectItem> items,
        int maxVisible,
        SelectListTheme theme,
        SelectListLayoutOptions? layout = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(theme);
        _items = items;
        _filteredItems = items;
        _maxVisible = maxVisible;
        _theme = theme;
        _layout = layout ?? new SelectListLayoutOptions();
    }

    /// <summary>Called with the selected item after confirmation.</summary>
    public Action<SelectItem>? OnSelect { get; set; }

    /// <summary>Called when selection is cancelled.</summary>
    public Action? OnCancel { get; set; }

    /// <summary>Called after keyboard navigation changes the selected item.</summary>
    public Action<SelectItem>? OnSelectionChange { get; set; }

    /// <summary>Filters items by a case-insensitive value prefix and resets selection.</summary>
    public void SetFilter(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filteredItems = _items
            .Where(item => item.Value.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _selectedIndex = 0;
    }

    /// <summary>Sets and clamps the selected index.</summary>
    public void SetSelectedIndex(int index) =>
        _selectedIndex = Math.Max(0, Math.Min(index, _filteredItems.Count - 1));

    /// <inheritdoc />
    public virtual void Invalidate()
    {
    }

    /// <inheritdoc />
    public virtual IReadOnlyList<string> Render(int width)
    {
        var lines = new List<string>();
        if (_filteredItems.Count == 0)
        {
            lines.Add(_theme.NoMatch("  No matching commands"));
            return lines;
        }

        var primaryColumnWidth = GetPrimaryColumnWidth();
        var startIndex = Math.Max(
            0,
            Math.Min(_selectedIndex - (int)Math.Floor(_maxVisible / 2d), _filteredItems.Count - _maxVisible));
        var endIndex = Math.Min(startIndex + _maxVisible, _filteredItems.Count);

        for (var index = startIndex; index < endIndex; index++)
        {
            var item = _filteredItems[index];
            var isSelected = index == _selectedIndex;
            var description = item.Description is null ? null : NormalizeToSingleLine(item.Description);
            lines.Add(RenderItem(item, isSelected, width, description, primaryColumnWidth));
        }

        if (startIndex > 0 || endIndex < _filteredItems.Count)
        {
            var scrollText = $"  ({_selectedIndex + 1}/{_filteredItems.Count})";
            lines.Add(_theme.ScrollInfo(TextMeasurement.TruncateToWidth(scrollText, width - 2, string.Empty)));
        }

        return lines;
    }

    /// <inheritdoc />
    public virtual void HandleInput(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var keybindings = KeybindingsManager.GetKeybindings();
        if (keybindings.Matches(data, "tui.select.up"))
        {
            _selectedIndex = _selectedIndex == 0 ? _filteredItems.Count - 1 : _selectedIndex - 1;
            NotifySelectionChange();
        }
        else if (keybindings.Matches(data, "tui.select.down"))
        {
            _selectedIndex = _selectedIndex == _filteredItems.Count - 1 ? 0 : _selectedIndex + 1;
            NotifySelectionChange();
        }
        else if (keybindings.Matches(data, "tui.select.confirm"))
        {
            var selectedItem = GetItemAt(_selectedIndex);
            if (selectedItem is not null)
            {
                OnSelect?.Invoke(selectedItem);
            }
        }
        else if (keybindings.Matches(data, "tui.select.cancel"))
        {
            OnCancel?.Invoke();
        }
    }

    /// <summary>Returns the selected item, or null when the filtered list is empty.</summary>
    public SelectItem? GetSelectedItem() => GetItemAt(_selectedIndex);

    private static string NormalizeToSingleLine(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, "[\\r\\n]+", " ").Trim();

    private string RenderItem(
        SelectItem item,
        bool isSelected,
        int width,
        string? descriptionSingleLine,
        int primaryColumnWidth)
    {
        var prefix = isSelected ? "→ " : "  ";
        var prefixWidth = TextMeasurement.VisibleWidth(prefix);

        if (descriptionSingleLine is not null && width > 40)
        {
            var effectivePrimaryColumnWidth = Math.Max(1, Math.Min(primaryColumnWidth, width - prefixWidth - 4));
            var maxPrimaryWidth = Math.Max(1, effectivePrimaryColumnWidth - _primaryColumnGap);
            var truncatedValue = TruncatePrimary(item, isSelected, maxPrimaryWidth, effectivePrimaryColumnWidth);
            var truncatedValueWidth = TextMeasurement.VisibleWidth(truncatedValue);
            var spacing = new string(' ', Math.Max(1, effectivePrimaryColumnWidth - truncatedValueWidth));
            var descriptionStart = prefixWidth + truncatedValueWidth + spacing.Length;
            var remainingWidth = width - descriptionStart - 2;

            if (remainingWidth > _minDescriptionWidth)
            {
                var truncatedDescription = TextMeasurement.TruncateToWidth(descriptionSingleLine, remainingWidth, string.Empty);
                if (isSelected)
                {
                    return _theme.SelectedText(prefix + truncatedValue + spacing + truncatedDescription);
                }

                var descriptionText = _theme.Description(spacing + truncatedDescription);
                return prefix + truncatedValue + descriptionText;
            }
        }

        var maxWidth = width - prefixWidth - 2;
        var truncatedPrimary = TruncatePrimary(item, isSelected, maxWidth, maxWidth);
        return isSelected
            ? _theme.SelectedText(prefix + truncatedPrimary)
            : prefix + truncatedPrimary;
    }

    private int GetPrimaryColumnWidth()
    {
        var (min, max) = GetPrimaryColumnBounds();
        var widestPrimary = _filteredItems.Aggregate(
            0,
            (widest, item) => Math.Max(widest, TextMeasurement.VisibleWidth(GetDisplayValue(item)) + _primaryColumnGap));
        return Math.Max(min, Math.Min(widestPrimary, max));
    }

    private (int Min, int Max) GetPrimaryColumnBounds()
    {
        var rawMin = _layout.MinPrimaryColumnWidth ??
            _layout.MaxPrimaryColumnWidth ??
            _defaultPrimaryColumnWidth;
        var rawMax = _layout.MaxPrimaryColumnWidth ??
            _layout.MinPrimaryColumnWidth ??
            _defaultPrimaryColumnWidth;
        return (
            Math.Max(1, Math.Min(rawMin, rawMax)),
            Math.Max(1, Math.Max(rawMin, rawMax)));
    }

    private string TruncatePrimary(SelectItem item, bool isSelected, int maxWidth, int columnWidth)
    {
        var displayValue = GetDisplayValue(item);
        var truncatedValue = _layout.TruncatePrimary is null
            ? TextMeasurement.TruncateToWidth(displayValue, maxWidth, string.Empty)
            : _layout.TruncatePrimary(new SelectListTruncatePrimaryContext
            {
                Text = displayValue,
                MaxWidth = maxWidth,
                ColumnWidth = columnWidth,
                Item = item,
                IsSelected = isSelected,
            });
        return TextMeasurement.TruncateToWidth(truncatedValue, maxWidth, string.Empty);
    }

    private static string GetDisplayValue(SelectItem item) =>
        string.IsNullOrEmpty(item.Label) ? item.Value : item.Label;

    private void NotifySelectionChange()
    {
        var selectedItem = GetItemAt(_selectedIndex);
        if (selectedItem is not null)
        {
            OnSelectionChange?.Invoke(selectedItem);
        }
    }

    private SelectItem? GetItemAt(int index) =>
        index >= 0 && index < _filteredItems.Count ? _filteredItems[index] : null;
}
