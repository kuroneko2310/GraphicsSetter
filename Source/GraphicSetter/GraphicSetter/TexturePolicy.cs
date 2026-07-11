using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RimWorld.IO;
using UnityEngine;

namespace GraphicSetter;

public enum VramBudgetMode
{
    Auto,
    Conservative,
    Balanced,
    Quality,
    Custom
}

internal static class TexturePolicy
{
    private static readonly string[] FullResolutionPathMarkers =
    {
        "/UI/",
        "/Fonts/",
        "/Portraits/",
        "/Icons/"
    };

    public static int DetectedVramMb => Math.Max(0, SystemInfo.graphicsMemorySize);

    public static int EffectiveBudgetMb
    {
        get
        {
            SettingsGroup settings = GraphicsSettings.mainSettings;
            int detected = DetectedVramMb > 0 ? DetectedVramMb : 2048;

            return settings.vramBudgetMode switch
            {
                VramBudgetMode.Conservative => Math.Max(512, (int)(detected * 0.45f)),
                VramBudgetMode.Balanced => Math.Max(768, (int)(detected * 0.60f)),
                VramBudgetMode.Quality => Math.Max(1024, (int)(detected * 0.75f)),
                VramBudgetMode.Custom => Math.Max(256, settings.customVramBudgetMb),
                _ => Math.Max(512, (int)(detected * AutoBudgetFraction(detected)))
            };
        }
    }

    public static bool ShouldGenerateMipMaps(VirtualFile file, int width, int height)
    {
        SettingsGroup settings = GraphicsSettings.mainSettings;
        if (!settings.disableMipMapsForSmallTextures)
            return true;

        if (IsInterfaceTexture(file))
            return false;

        return Math.Max(width, height) > Math.Max(16, settings.smallTextureMipThreshold);
    }

    public static int ResolveMaxTextureDimension(VirtualFile file)
    {
        SettingsGroup settings = GraphicsSettings.mainSettings;
        if (!settings.enableAdaptiveTextureBudget || IsInterfaceTexture(file))
            return 0;

        if (settings.maxTextureSize > 0)
            return settings.maxTextureSize;

        int budget = EffectiveBudgetMb;
        if (budget <= 1024)
            return 1024;
        if (budget <= 3072)
            return 2048;
        return 4096;
    }

    public static bool IsInterfaceTexture(VirtualFile file)
    {
        string path = file?.FullPath;
        if (string.IsNullOrEmpty(path))
            return false;

        path = path.Replace('\\', '/');
        foreach (string marker in FullResolutionPathMarkers)
        {
            if (path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    public static string BuildFingerprint()
    {
        SettingsGroup settings = GraphicsSettings.mainSettings;
        string raw = string.Join("|",
            "graphics-setter-policy-v2",
            settings.enableDDSLoading,
            settings.enableAdaptiveTextureBudget,
            settings.vramBudgetMode,
            settings.customVramBudgetMb,
            settings.maxTextureSize,
            settings.disableMipMapsForSmallTextures,
            settings.smallTextureMipThreshold);

        using SHA256 sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return BitConverter.ToString(bytes).Replace("-", string.Empty);
    }

    private static float AutoBudgetFraction(int detectedVramMb)
    {
        if (detectedVramMb <= 2048)
            return 0.45f;
        if (detectedVramMb <= 4096)
            return 0.55f;
        if (detectedVramMb <= 8192)
            return 0.65f;
        return 0.72f;
    }
}
