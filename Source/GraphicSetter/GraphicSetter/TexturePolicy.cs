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

    private static readonly string[] DataTexturePathMarkers =
    {
        "/Masks/",
        "/Mask/",
        "/Normals/",
        "/NormalMaps/",
        "/DataTextures/"
    };

    private static readonly string[] DataTextureNameSuffixes =
    {
        "_mask",
        "_normal",
        "_norm",
        "_spec",
        "_specular",
        "_metallic",
        "_roughness",
        "_ao"
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
        if (IsDataTexture(file))
            return true;
        if (!settings.disableMipMapsForSmallTextures)
            return true;
        if (IsInterfaceTexture(file))
            return false;

        return Math.Max(width, height) > Math.Max(16, settings.smallTextureMipThreshold);
    }

    public static int ResolveMaxTextureDimension(VirtualFile file)
    {
        SettingsGroup settings = GraphicsSettings.mainSettings;
        if (!settings.enableAdaptiveTextureBudget || IsInterfaceTexture(file) || IsDataTexture(file))
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
        string path = NormalizePath(file?.FullPath);
        if (path.Length == 0)
            return false;

        foreach (string marker in FullResolutionPathMarkers)
        {
            if (path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    public static bool IsDataTexture(VirtualFile file)
    {
        string path = NormalizePath(file?.FullPath);
        if (path.Length == 0)
            return false;

        foreach (string marker in DataTexturePathMarkers)
        {
            if (path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        string fileName = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
        foreach (string suffix in DataTextureNameSuffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string BuildFingerprint()
    {
        SettingsGroup settings = GraphicsSettings.mainSettings;
        string raw = string.Join("|",
            "graphics-setter-policy-v3",
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

    private static string NormalizePath(string path) => (path ?? string.Empty).Replace('\\', '/');

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
