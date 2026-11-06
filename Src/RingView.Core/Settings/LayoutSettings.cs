namespace RingView.Core.Settings;

/// <summary>Grid layout preset. <see cref="Auto"/> picks columns from the camera count.</summary>
public enum LayoutPreset
{
    Auto,
    OneUp,
    TwoByTwo,
    ThreeByThree,
    FourByFour,
}

/// <summary>The wall's layout: a preset plus the user's tile order (camera ids).</summary>
public sealed record LayoutSettings(LayoutPreset Preset, IReadOnlyList<long> Order)
{
    public static LayoutSettings Default { get; } = new(LayoutPreset.Auto, []);
}

/// <summary>Pinned by Tests/Golden/layout-columns.json.</summary>
public static class LayoutRules
{
    /// <summary>Column count for a preset, given how many tiles are visible.</summary>
    public static int Columns(LayoutPreset preset, int visibleCount) => preset switch
    {
        LayoutPreset.OneUp => 1,
        LayoutPreset.TwoByTwo => 2,
        LayoutPreset.ThreeByThree => 3,
        LayoutPreset.FourByFour => 4,
        _ => visibleCount > 0 ? (int)Math.Ceiling(Math.Sqrt(visibleCount)) : 1,
    };
}
