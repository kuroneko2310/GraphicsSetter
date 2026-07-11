using System;
using UnityEngine;

namespace GraphicSetter;

internal static class VramBudgetManager
{
    private static readonly object Sync = new();
    private static long plannedBytes;
    private static long budgetBytes;

    public static long PlannedBytes
    {
        get { lock (Sync) return plannedBytes; }
    }

    public static long BudgetBytes
    {
        get { lock (Sync) return budgetBytes; }
    }

    public static void Reset(SettingsGroup settings)
    {
        lock (Sync)
        {
            plannedBytes = 0;
            budgetBytes = CalculateBudget(settings);
        }
    }

    public static int FitMaxDimension(int width, int height, int requestedMaximum, bool useMipMaps, SettingsGroup settings)
    {
        lock (Sync)
        {
            if (budgetBytes <= 0)
                budgetBytes = CalculateBudget(settings);

            var targetMaximum = Math.Max(64, requestedMaximum);
            var targetWidth = width;
            var targetHeight = height;
            while (Math.Max(targetWidth, targetHeight) > targetMaximum)
            {
                targetWidth = Math.Max(1, targetWidth / 2);
                targetHeight = Math.Max(1, targetHeight / 2);
            }

            var estimated = EstimateRgbaBytes(targetWidth, targetHeight, useMipMaps);
            while (plannedBytes + estimated > budgetBytes && Math.Max(targetWidth, targetHeight) > 128)
            {
                targetWidth = Math.Max(1, targetWidth / 2);
                targetHeight = Math.Max(1, targetHeight / 2);
                estimated = EstimateRgbaBytes(targetWidth, targetHeight, useMipMaps);
            }

            plannedBytes += estimated;
            return Math.Max(targetWidth, targetHeight);
        }
    }

    private static long CalculateBudget(SettingsGroup settings)
    {
        var detectedMb = Math.Max(0, SystemInfo.graphicsMemorySize);
        long budgetMb = settings.vramProfile switch
        {
            VramProfile.Low => detectedMb > 0 ? Math.Min(1536, Math.Max(512, detectedMb / 3)) : 1024,
            VramProfile.Balanced => detectedMb > 0 ? Math.Min(4096, Math.Max(1024, detectedMb / 2)) : 2048,
            VramProfile.Quality => detectedMb > 0 ? Math.Max(2048, detectedMb * 3L / 4L) : 4096,
            VramProfile.Custom => Math.Max(256, settings.customVramBudgetMB),
            _ => detectedMb > 0 ? Math.Max(512, detectedMb - Math.Max(768, detectedMb / 4)) : 2048
        };
        return budgetMb * 1024L * 1024L;
    }

    private static long EstimateRgbaBytes(int width, int height, bool mipMaps)
    {
        long size = (long)Math.Max(1, width) * Math.Max(1, height) * 4;
        return mipMaps ? size + size / 3 : size;
    }
}
