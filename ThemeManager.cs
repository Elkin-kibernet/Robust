using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace RobloxWorker.Launcher.Services;

/// <summary>
/// Переключение тёмной/светлой темы через глобальные DynamicResource-кисти.
/// </summary>
public static class ThemeManager
{
    private static readonly IReadOnlyDictionary<string, string> DarkColors = new Dictionary<string, string>
    {
        ["BgMain"] = "#0D1117",
        ["BgCard"] = "#161B22",
        ["BgCardAlt"] = "#1E2530",
        ["BgInput"] = "#0B1017",
        ["BorderSoft"] = "#2A3242",
        ["BorderStrong"] = "#3A4658",
        ["TextPrimary"] = "#F0F6FC",
        ["TextSecondary"] = "#9AA7B8",
        ["TextMuted"] = "#6B7A8D",
        ["Accent"] = "#10B981",
        ["AccentHover"] = "#34D399",
        ["AccentSoft"] = "#0F2E25",
        ["Danger"] = "#EF4444",
        ["DangerHover"] = "#F87171",
        ["Warning"] = "#F59E0B",
        ["Info"] = "#3B82F6",
        ["ChromeHover"] = "#22FFFFFF",
        ["ChromeCloseHover"] = "#E81123"
    };

    private static readonly IReadOnlyDictionary<string, string> LightColors = new Dictionary<string, string>
    {
        ["BgMain"] = "#EEF1F5",
        ["BgCard"] = "#FFFFFF",
        ["BgCardAlt"] = "#F4F7FB",
        ["BgInput"] = "#E9EEF5",
        ["BorderSoft"] = "#D6DEE8",
        ["BorderStrong"] = "#BFCAD8",
        ["TextPrimary"] = "#18212D",
        ["TextSecondary"] = "#4A5768",
        ["TextMuted"] = "#7A8797",
        ["Accent"] = "#059669",
        ["AccentHover"] = "#047857",
        ["AccentSoft"] = "#E3F5EE",
        ["Danger"] = "#DC2626",
        ["DangerHover"] = "#B91C1C",
        ["Warning"] = "#B45309",
        ["Info"] = "#2563EB",
        ["ChromeHover"] = "#14000000",
        ["ChromeCloseHover"] = "#E81123"
    };

    public static void Apply(string theme)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var palette = string.Equals(theme, "light", System.StringComparison.OrdinalIgnoreCase)
            ? LightColors
            : DarkColors;

        foreach (var pair in palette)
        {
            resources[pair.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
        }
    }
}
