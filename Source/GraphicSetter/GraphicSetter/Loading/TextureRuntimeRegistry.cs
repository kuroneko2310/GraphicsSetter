using System;
using System.Collections.Generic;
using UnityEngine;

namespace GraphicSetter;

internal static class TextureRuntimeRegistry
{
    private sealed class Entry
    {
        public WeakReference Texture;
        public float OriginalMipMapBias;
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<int, Entry> Entries = new();
    private static int registrationsSinceCleanup;

    public static void RegisterAndApply(Texture2D texture)
    {
        if (!texture)
            return;

        lock (Sync)
        {
            int instanceId = texture.GetInstanceID();
            if (!Entries.TryGetValue(instanceId, out Entry entry)
                || entry.Texture.Target is not Texture2D existing
                || !existing
                || existing != texture)
            {
                entry = new Entry
                {
                    Texture = new WeakReference(texture),
                    OriginalMipMapBias = texture.mipMapBias
                };
                Entries[instanceId] = entry;
            }

            Apply(entry, texture);
            registrationsSinceCleanup++;
            if (registrationsSinceCleanup >= 128)
            {
                registrationsSinceCleanup = 0;
                RemoveDeadEntries();
            }
        }
    }

    public static void ApplyCurrentSettings()
    {
        lock (Sync)
        {
            List<int> dead = null;
            foreach (KeyValuePair<int, Entry> pair in Entries)
            {
                if (pair.Value.Texture.Target is not Texture2D texture || !texture)
                {
                    dead ??= new List<int>();
                    dead.Add(pair.Key);
                    continue;
                }

                Apply(pair.Value, texture);
            }

            if (dead != null)
            {
                foreach (int instanceId in dead)
                    Entries.Remove(instanceId);
            }
        }
    }

    private static void Apply(Entry entry, Texture2D texture)
    {
        texture.filterMode = texture.mipmapCount > 1 ? FilterMode.Trilinear : FilterMode.Bilinear;
        texture.mipMapBias = GraphicsSettings.mainSettings.overrideMipMapBias
            ? GraphicsSettings.mainSettings.mipMapBias
            : entry.OriginalMipMapBias;
    }

    private static void RemoveDeadEntries()
    {
        List<int> dead = null;
        foreach (KeyValuePair<int, Entry> pair in Entries)
        {
            if (pair.Value.Texture.Target is Texture2D texture && texture)
                continue;

            dead ??= new List<int>();
            dead.Add(pair.Key);
        }

        if (dead == null)
            return;

        foreach (int instanceId in dead)
            Entries.Remove(instanceId);
    }
}
