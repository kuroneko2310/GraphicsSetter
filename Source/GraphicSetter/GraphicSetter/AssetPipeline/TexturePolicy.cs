using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace GraphicSetter;

public enum VramProfile
{
    Auto,
    Low,
    Balanced,
    Quality,
    Custom
}

internal enum TextureCategory
{
    Ui,
    Portrait,
    Pawn,
    Building,
    Terrain,
    Background,
    Mask,
    Normal,
    Icon,
    Other
}

internal sealed class TexturePipelinePolicy
{
    public TextureCategory Category;
    public int MaxDimension;
    public bool UseMipMaps;
    public bool AllowCompression;
    public bool Linear;
    public bool HighQualityCompression;

    public string Signature => string.Join("|", new[]
    {
        Category.ToString(),
        MaxDimension.ToString(CultureInfo.InvariantCulture),
        UseMipMaps.ToString(),
        AllowCompression.ToString(),
        Linear.ToString(),
        HighQualityCompression.ToString()
    });

    public static TexturePipelinePolicy Create(string sourcePath, SettingsGroup settings, string externalPolicy)
    {
        var category = Classify(sourcePath);
        var policy = new TexturePipelinePolicy
        {
            Category = category,
            MaxDimension = GetDefaultMaxDimension(category, settings),
            UseMipMaps = ShouldUseMipMaps(category, settings),
            AllowCompression = settings.enableRuntimeCompression && category != TextureCategory.Ui && category != TextureCategory.Portrait && category != TextureCategory.Icon,
            Linear = category == TextureCategory.Normal || category == TextureCategory.Mask,
            HighQualityCompression = settings.highQualityCompression
        };

        ApplyExternalPolicy(policy, externalPolicy);
        policy.MaxDimension = Math.Max(64, policy.MaxDimension);
        return policy;
    }

    public bool DisableMipMapsForSize(int width, int height, SettingsGroup settings)
    {
        return !UseMipMaps || Math.Max(width, height) <= Math.Max(16, settings.disableMipMapsAtOrBelow);
    }

    private static TextureCategory Classify(string sourcePath)
    {
        var normalized = (sourcePath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        var file = Path.GetFileNameWithoutExtension(normalized) ?? string.Empty;
        if (normalized.Contains("/ui/") || normalized.Contains("/interface/")) return TextureCategory.Ui;
        if (normalized.Contains("portrait")) return TextureCategory.Portrait;
        if (normalized.Contains("/icons/") || normalized.Contains("/icon/")) return TextureCategory.Icon;
        if (normalized.Contains("normal") || file.EndsWith("_n") || file.EndsWith("_normal")) return TextureCategory.Normal;
        if (normalized.Contains("mask") || file.EndsWith("_m") || file.EndsWith("_mask")) return TextureCategory.Mask;
        if (normalized.Contains("terrain") || normalized.Contains("floors")) return TextureCategory.Terrain;
        if (normalized.Contains("background") || normalized.Contains("world/")) return TextureCategory.Background;
        if (normalized.Contains("pawn") || normalized.Contains("things/pawn") || normalized.Contains("apparel")) return TextureCategory.Pawn;
        if (normalized.Contains("building") || normalized.Contains("things/building")) return TextureCategory.Building;
        return TextureCategory.Other;
    }

    private static int GetDefaultMaxDimension(TextureCategory category, SettingsGroup settings)
    {
        if (settings.vramProfile == VramProfile.Custom)
            return Math.Max(64, settings.customMaxTextureDimension);

        return settings.vramProfile switch
        {
            VramProfile.Low => category switch
            {
                TextureCategory.Ui or TextureCategory.Portrait => 2048,
                TextureCategory.Icon => 512,
                TextureCategory.Mask or TextureCategory.Normal => 512,
                _ => 1024
            },
            VramProfile.Balanced => category switch
            {
                TextureCategory.Ui or TextureCategory.Portrait => 4096,
                TextureCategory.Icon => 1024,
                TextureCategory.Mask or TextureCategory.Normal => 1024,
                TextureCategory.Terrain or TextureCategory.Background => 2048,
                _ => 2048
            },
            VramProfile.Quality => category switch
            {
                TextureCategory.Icon => 2048,
                TextureCategory.Mask or TextureCategory.Normal => 2048,
                _ => 8192
            },
            _ => GetAutoMaxDimension(category)
        };
    }

    private static int GetAutoMaxDimension(TextureCategory category)
    {
        var vram = UnityEngine.SystemInfo.graphicsMemorySize;
        if (vram > 0 && vram <= 3072)
            return category is TextureCategory.Ui or TextureCategory.Portrait ? 2048 : 1024;
        if (vram > 0 && vram <= 6144)
            return category is TextureCategory.Ui or TextureCategory.Portrait ? 4096 : 2048;
        return category is TextureCategory.Mask or TextureCategory.Normal ? 2048 : 4096;
    }

    private static bool ShouldUseMipMaps(TextureCategory category, SettingsGroup settings)
    {
        if (!settings.enableMipMaps)
            return false;
        return category != TextureCategory.Ui && category != TextureCategory.Portrait && category != TextureCategory.Icon;
    }

    private static void ApplyExternalPolicy(TexturePipelinePolicy policy, string serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
            return;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in serialized.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
                continue;
            values[part.Substring(0, equals)] = part.Substring(equals + 1);
        }

        if (values.TryGetValue("category", out var category) && Enum.TryParse(category, true, out TextureCategory parsedCategory))
            policy.Category = parsedCategory;
        if (values.TryGetValue("maxDimension", out var max) && int.TryParse(max, out var parsedMax) && parsedMax > 0)
            policy.MaxDimension = parsedMax;
        if (values.TryGetValue("mipPolicy", out var mipPolicy) && !string.IsNullOrEmpty(mipPolicy))
            policy.UseMipMaps = !mipPolicy.Equals("none", StringComparison.OrdinalIgnoreCase) && !mipPolicy.Equals("off", StringComparison.OrdinalIgnoreCase);
        if (values.TryGetValue("format", out var format) && !string.IsNullOrEmpty(format))
            policy.AllowCompression = !format.Equals("rgba32", StringComparison.OrdinalIgnoreCase) && !format.Equals("uncompressed", StringComparison.OrdinalIgnoreCase);
        if (values.TryGetValue("linear", out var linear) && bool.TryParse(linear, out var parsedLinear))
            policy.Linear = parsedLinear;
    }
}
