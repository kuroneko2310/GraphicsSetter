using System;
using System.Collections.Generic;
using UnityEngine;

namespace GraphicSetter;

internal static class RuntimeTextureRegistry
{
    private sealed class Entry
    {
        public WeakReference<Texture2D> Texture;
        public string PackageId;
        public string SourcePath;
    }

    private static readonly object Sync = new();
    private static readonly List<Entry> Entries = new();

    public static void Register(Texture2D texture, string packageId, string sourcePath)
    {
        if (!texture)
            return;

        lock (Sync)
        {
            Entries.Add(new Entry
            {
                Texture = new WeakReference<Texture2D>(texture),
                PackageId = packageId ?? string.Empty,
                SourcePath = sourcePath ?? string.Empty
            });
            Apply(texture, GraphicsSettings.mainSettings);
            if (Entries.Count % 256 == 0)
                RemoveDeadEntries();
        }
    }

    public static void ApplyAll(SettingsGroup settings)
    {
        lock (Sync)
        {
            for (var index = Entries.Count - 1; index >= 0; index--)
            {
                if (!Entries[index].Texture.TryGetTarget(out var texture) || !texture)
                {
                    Entries.RemoveAt(index);
                    continue;
                }
                Apply(texture, settings);
            }
        }
    }

    public static int LiveCount
    {
        get
        {
            lock (Sync)
            {
                RemoveDeadEntries();
                return Entries.Count;
            }
        }
    }

    private static void Apply(Texture2D texture, SettingsGroup settings)
    {
        if (!texture || settings == null)
            return;

        texture.filterMode = settings.filterMode;
        texture.anisoLevel = Math.Max(0, Math.Min(2, settings.anisoLevel));
        if (settings.overrideMipMapBias)
            texture.mipMapBias = settings.mipMapBias;
    }

    private static void RemoveDeadEntries()
    {
        for (var index = Entries.Count - 1; index >= 0; index--)
        {
            if (!Entries[index].Texture.TryGetTarget(out var texture) || !texture)
                Entries.RemoveAt(index);
        }
    }
}
