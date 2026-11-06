namespace RingView.App.ViewModels;

/// <summary>One choice in a drop-down: what it shows, its value, and whether it can be picked.</summary>
public sealed record OptionItem(string Label, int Value, bool IsEnabled = true);
