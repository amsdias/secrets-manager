using Microsoft.UI.Reactor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SecretsManager.Core.App;
using Windows.UI;

namespace SecretsManager.App;

/// <summary>
/// Color themes. "System" is plain Fluent (light or dark, following Windows). The Catppuccin flavors recolor WinUI's
/// shared theme brushes in place (TextFillColorPrimaryBrush, ControlFillColorDefaultBrush, …). Control templates alias
/// those same brush instances, so every control, dialog and drop-down follows without restyling each one.
/// The original colors are remembered and restored when switching back to System.
/// </summary>
static class ThemeManager
{
    sealed record Flavor(bool IsLight, uint Base, uint Mantle, uint Crust, uint Surface0, uint Surface1, uint Surface2,
        uint Overlay0, uint Overlay1, uint Overlay2, uint Subtext0, uint Subtext1, uint Text, uint Accent, uint Red, uint Green, uint Yellow);

    // Official palettes: https://catppuccin.com/palette (accent: mauve).
    static readonly Dictionary<string, Flavor> Flavors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Latte"] = new(true, 0xeff1f5, 0xe6e9ef, 0xdce0e8, 0xccd0da, 0xbcc0cc, 0xacb0be, 0x9ca0b0, 0x8c8fa1, 0x7c7f93, 0x6c6f85, 0x5c5f77, 0x4c4f69, 0x8839ef, 0xd20f39, 0x40a02b, 0xdf8e1d),
        ["Frappe"] = new(false, 0x303446, 0x292c3c, 0x232634, 0x414559, 0x51576d, 0x626880, 0x737994, 0x838ba7, 0x949cbb, 0xa5adce, 0xb5bfe2, 0xc6d0f5, 0xca9ee6, 0xe78284, 0xa6d189, 0xe5c890),
        ["Macchiato"] = new(false, 0x24273a, 0x1e2030, 0x181926, 0x363a4f, 0x494d64, 0x5b6078, 0x6e738d, 0x8087a2, 0x939ab7, 0xa5adcb, 0xb8c0e0, 0xcad3f5, 0xc6a0f6, 0xed8796, 0xa6da95, 0xeed49f),
        ["Mocha"] = new(false, 0x1e1e2e, 0x181825, 0x11111b, 0x313244, 0x45475a, 0x585b70, 0x6c7086, 0x7f849c, 0x9399b2, 0xa6adc8, 0xbac2de, 0xcdd6f4, 0xcba6f7, 0xf38ba8, 0xa6e3a1, 0xf9e2af),
    };

    static Color C(uint rgb, double alpha = 1) =>
        Color.FromArgb((byte)Math.Round(alpha * 255), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    static Dictionary<string, Color> BrushColors(Flavor f) => new()
    {
        // Surfaces: window = mantle, cards = base, status bar = crust.
        ["SolidBackgroundFillColorBaseBrush"] = C(f.Mantle),
        ["SolidBackgroundFillColorBaseAltBrush"] = C(f.Crust),
        ["SolidBackgroundFillColorSecondaryBrush"] = C(f.Base),
        ["SolidBackgroundFillColorTertiaryBrush"] = C(f.Surface0),
        ["SolidBackgroundFillColorQuarternaryBrush"] = C(f.Surface1),
        ["LayerFillColorDefaultBrush"] = C(f.Crust),
        ["LayerFillColorAltBrush"] = C(f.Base),
        ["LayerOnMicaBaseAltFillColorDefaultBrush"] = C(f.Base),
        ["CardBackgroundFillColorDefaultBrush"] = C(f.Base),
        ["CardBackgroundFillColorSecondaryBrush"] = C(f.Mantle),
        ["SmokeFillColorDefaultBrush"] = C(f.Crust, 0.6),
        ["AcrylicBackgroundFillColorDefaultBrush"] = C(f.Base),
        ["AcrylicBackgroundFillColorBaseBrush"] = C(f.Mantle),
        ["AcrylicInAppFillColorDefaultBrush"] = C(f.Base),
        ["AcrylicInAppFillColorBaseBrush"] = C(f.Mantle),
        // Strokes
        ["CardStrokeColorDefaultBrush"] = C(f.Surface0),
        ["ControlStrokeColorDefaultBrush"] = C(f.Surface1),
        ["ControlStrokeColorSecondaryBrush"] = C(f.Surface2),
        ["ControlStrongStrokeColorDefaultBrush"] = C(f.Overlay0),
        ["ControlStrongStrokeColorDisabledBrush"] = C(f.Surface2),
        ["DividerStrokeColorDefaultBrush"] = C(f.Surface0),
        ["SurfaceStrokeColorDefaultBrush"] = C(f.Surface1),
        ["ControlElevationBorderBrush"] = C(f.Surface1),
        ["CircleElevationBorderBrush"] = C(f.Surface1),
        ["TextControlElevationBorderBrush"] = C(f.Overlay0),
        ["TextControlElevationBorderFocusedBrush"] = C(f.Accent),
        ["AccentControlElevationBorderBrush"] = C(f.Accent, 0.8),
        ["FocusStrokeColorOuterBrush"] = C(f.Text),
        // Control fills
        ["ControlFillColorDefaultBrush"] = C(f.Surface0),
        ["ControlFillColorSecondaryBrush"] = C(f.Surface1),
        ["ControlFillColorTertiaryBrush"] = C(f.Surface0, 0.7),
        ["ControlFillColorDisabledBrush"] = C(f.Surface0, 0.5),
        ["ControlFillColorInputActiveBrush"] = C(f.Base),
        ["ControlAltFillColorSecondaryBrush"] = C(f.Mantle),
        ["ControlAltFillColorTertiaryBrush"] = C(f.Surface0),
        ["ControlStrongFillColorDefaultBrush"] = C(f.Overlay1),
        ["SubtleFillColorSecondaryBrush"] = C(f.Surface0, 0.8),
        ["SubtleFillColorTertiaryBrush"] = C(f.Surface1, 0.8),
        // Text
        ["TextFillColorPrimaryBrush"] = C(f.Text),
        ["TextFillColorSecondaryBrush"] = C(f.Subtext1),
        ["TextFillColorTertiaryBrush"] = C(f.Overlay2),
        ["TextFillColorDisabledBrush"] = C(f.Overlay0),
        ["TextOnAccentFillColorPrimaryBrush"] = C(f.IsLight ? f.Base : f.Crust),
        ["TextOnAccentFillColorSecondaryBrush"] = C(f.IsLight ? f.Base : f.Crust, 0.8),
        ["TextOnAccentFillColorDisabledBrush"] = C(f.Overlay0),
        // Accent and status
        ["AccentFillColorDefaultBrush"] = C(f.Accent),
        ["AccentFillColorSecondaryBrush"] = C(f.Accent, 0.9),
        ["AccentFillColorTertiaryBrush"] = C(f.Accent, 0.8),
        ["AccentFillColorDisabledBrush"] = C(f.Surface1),
        ["AccentTextFillColorPrimaryBrush"] = C(f.Accent),
        ["AccentTextFillColorSecondaryBrush"] = C(f.Accent),
        ["AccentTextFillColorTertiaryBrush"] = C(f.Accent, 0.8),
        ["SystemFillColorCriticalBrush"] = C(f.Red),
        ["SystemFillColorCriticalBackgroundBrush"] = C(f.Red, 0.15),
        ["SystemFillColorSuccessBrush"] = C(f.Green),
        ["SystemFillColorCautionBrush"] = C(f.Yellow),
        ["SystemFillColorAttentionBrush"] = C(f.Accent),
    };

    static readonly Dictionary<Brush, Action> Restore = new(ReferenceEqualityComparer.Instance);

    public static ElementTheme ElementThemeFor(string theme) =>
        Flavors.TryGetValue(theme, out var f) ? (f.IsLight ? ElementTheme.Light : ElementTheme.Dark) : ElementTheme.Default;

    /// <summary>Brushes that refused a new color (some shared brushes are read-only); kept for diagnostics.</summary>
    public static int SkippedBrushes { get; private set; }

    public static void Apply(string theme, ReactorWindow? window)
    {
        foreach (var undo in Restore.Values) Try(undo);
        Restore.Clear();

        Flavors.TryGetValue(theme, out var flavor);
        if (flavor is not null)
        {
            string[] themeKeys = flavor.IsLight ? ["Light"] : ["Default", "Dark"];
            foreach (var (key, color) in BrushColors(flavor))
                foreach (var brush in FindBrushes(Application.Current.Resources, themeKeys, key).Distinct(ReferenceEqualityComparer.Instance).Cast<Brush>())
                    Try(() => Recolor(brush, color));
        }

        // Popups (dialogs, drop-downs) take their theme from the window's root content, not from our root element.
        if (window?.NativeWindow.Content is FrameworkElement root) root.RequestedTheme = ElementThemeFor(theme);
        ApplyTitleBar(window, flavor);
    }

    static void Try(Action action)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { SkippedBrushes++; }
    }

    static void Recolor(Brush brush, Color color)
    {
        switch (brush)
        {
            case SolidColorBrush s:
                var old = s.Color;
                Restore.TryAdd(s, () => s.Color = old);
                s.Color = color;
                break;
            case AcrylicBrush a:
                var (tint, fallback, opacity) = (a.TintColor, a.FallbackColor, a.TintOpacity);
                Restore.TryAdd(a, () => { a.TintColor = tint; a.FallbackColor = fallback; a.TintOpacity = opacity; });
                a.TintColor = color; a.FallbackColor = color; a.TintOpacity = 0.95;
                break;
            case GradientBrush g:
                var stops = g.GradientStops.Select(x => x.Color).ToArray();
                Restore.TryAdd(g, () => { for (var i = 0; i < stops.Length && i < g.GradientStops.Count; i++) g.GradientStops[i].Color = stops[i]; });
                foreach (var stop in g.GradientStops) stop.Color = color;
                break;
        }
    }

    static IEnumerable<object> FindBrushes(ResourceDictionary rd, string[] themeKeys, string key)
    {
        foreach (var themeKey in themeKeys)
            if (rd.ThemeDictionaries.TryGetValue(themeKey, out var t) && t is ResourceDictionary themeDict)
                foreach (var b in InDictionary(themeDict, key)) yield return b;
        foreach (var merged in rd.MergedDictionaries)
            foreach (var b in FindBrushes(merged, themeKeys, key)) yield return b;
    }

    static IEnumerable<object> InDictionary(ResourceDictionary rd, string key)
    {
        if (rd.TryGetValue(key, out var v) && v is Brush) yield return v;
        foreach (var merged in rd.MergedDictionaries)
            foreach (var b in InDictionary(merged, key)) yield return b;
    }

    static void ApplyTitleBar(ReactorWindow? window, Flavor? f)
    {
        if (window?.AppWindow.TitleBar is not { } bar) return;
        Color? bg = f is null ? null : C(f.Mantle), fg = f is null ? null : C(f.Text), dim = f is null ? null : C(f.Overlay1),
            hover = f is null ? null : C(f.Surface0), pressed = f is null ? null : C(f.Surface1);
        bar.BackgroundColor = bg; bar.InactiveBackgroundColor = bg;
        bar.ForegroundColor = fg; bar.InactiveForegroundColor = dim;
        bar.ButtonBackgroundColor = bg; bar.ButtonInactiveBackgroundColor = bg;
        bar.ButtonForegroundColor = fg; bar.ButtonInactiveForegroundColor = dim;
        bar.ButtonHoverBackgroundColor = hover; bar.ButtonHoverForegroundColor = fg;
        bar.ButtonPressedBackgroundColor = pressed; bar.ButtonPressedForegroundColor = fg;
    }
}
