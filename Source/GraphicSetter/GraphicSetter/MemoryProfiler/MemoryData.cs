using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace GraphicSetter;

public class MemoryData
{
    private static readonly Dictionary<ModContentPack, CachedModData> ModData = new();
    private static readonly Dictionary<Pair<string, bool>, long> MemoryUsageByAtlas = new();

    private static readonly Color ListingBG = new ColorInt(32, 36, 40).ToColor;
    private static readonly Color NiceBlue = new ColorInt(38, 169, 224).ToColor;
    private const float CriticalPct = 0.8f;

    private bool paused;
    private int scanGeneration;
    private int processedMods;
    private List<ModContentPack> currentMods = new();

    private long TotalBytes => ModData.Sum(pair => pair.Value.MemoryUsage);
    private long TotalBytesAtlas => MemoryUsageByAtlas.Sum(pair => pair.Value);
    private long TotalUsage => TotalBytes + TotalBytesAtlas;
    private long MainMemory => Math.Max(256_000_000L, TexturePolicy.EffectiveBudgetMb * 1_000_000L);
    private float RawUsageFraction => MainMemory <= 0 ? 0f : (float)(TotalUsage / (double)MainMemory);
    private float DisplayUsageFraction => Mathf.Clamp01(RawUsageFraction);

    private long LargestModSize { get; set; }
    private long LargestAtlasSize { get; set; }
    private bool Calculating { get; set; }
    private bool Critical => RawUsageFraction > CriticalPct;

    public bool MEMOVERFLOW => RawUsageFraction > 1f;
    public Coroutine routine, routine2;

    public void Notify_SettingsChanged()
    {
        scanGeneration++;
        Calculating = false;
        paused = false;
        processedMods = 0;
        currentMods.Clear();
        ModData.Clear();
        MemoryUsageByAtlas.Clear();
        LargestModSize = 0;
        LargestAtlasSize = 0;
        SelMod = null;
    }

    private void Notify_ChangeState()
    {
        if (Calculating)
        {
            paused = !paused;
            return;
        }

        currentMods = SnapshotCurrentMods();
        if (currentMods.Count == 0)
            return;

        scanGeneration++;
        int generation = scanGeneration;
        processedMods = 0;
        paused = false;
        Calculating = true;
        ModData.Clear();
        MemoryUsageByAtlas.Clear();
        LargestModSize = 0;
        LargestAtlasSize = 0;
        routine = StaticContent.CoroutineDriver.StartCoroutine(CalculateMemory(generation));
    }

    private IEnumerator CalculateMemory(int generation)
    {
        for (int modIndex = 0; modIndex < currentMods.Count; modIndex++)
        {
            if (generation != scanGeneration)
                yield break;

            while (paused)
            {
                if (generation != scanGeneration)
                    yield break;
                yield return null;
            }

            ModContentPack mod = currentMods[modIndex];
            CachedModData data = new(mod);
            ModData[mod] = data;

            List<Texture2D> textures;
            try
            {
                textures = mod.textures.contentList.Values.Where(texture => texture).ToList();
            }
            catch (Exception exception)
            {
                Log.Warning($"[Graphics Settings] Could not enumerate textures for {mod.Name}: {exception.Message}");
                processedMods++;
                continue;
            }

            for (int textureIndex = 0; textureIndex < textures.Count; textureIndex++)
            {
                if (generation != scanGeneration)
                    yield break;

                while (paused)
                {
                    if (generation != scanGeneration)
                        yield break;
                    yield return null;
                }

                data.RegisterTexture(textures[textureIndex]);
                if ((textureIndex & 7) == 7)
                    yield return null;
            }

            LargestModSize = Math.Max(LargestModSize, data.MemoryUsage);
            processedMods++;
            yield return null;
        }

        // Estimate atlas memory from texture metadata. Never create a readable copy merely
        // to measure it; doing so can double peak RAM/VRAM for very large atlases.
        foreach (StaticTextureAtlas atlas in GlobalTextureAtlasManager.staticTextureAtlases.ToList())
        {
            if (generation != scanGeneration)
                yield break;

            while (paused)
            {
                if (generation != scanGeneration)
                    yield break;
                yield return null;
            }

            long size = TextureMemoryEstimator.Estimate(atlas.ColorTexture)
                        + TextureMemoryEstimator.Estimate(atlas.MaskTexture);
            Pair<string, bool> key = new(atlas.groupKey.group.ToString(), atlas.groupKey.hasMask);
            if (MemoryUsageByAtlas.TryGetValue(key, out long existing))
                MemoryUsageByAtlas[key] = existing + size;
            else
                MemoryUsageByAtlas[key] = size;

            LargestAtlasSize = Math.Max(LargestAtlasSize, MemoryUsageByAtlas[key]);
            yield return null;
        }

        if (generation == scanGeneration)
        {
            paused = false;
            Calculating = false;
        }
    }

    private static List<ModContentPack> SnapshotCurrentMods()
    {
        List<ModContentPack> result = new();
        foreach (ModContentPack mod in LoadedModManager.RunningMods)
        {
            try
            {
                if (mod.GetContentHolder<Texture2D>()?.contentList?.Any() == true)
                    result.Add(mod);
            }
            catch (Exception exception)
            {
                if (GraphicsSettings.mainSettings.verboseLogging)
                    Log.Warning($"[Graphics Settings] Skipping memory scan for {mod?.Name}: {exception.Message}");
            }
        }
        return result;
    }

    private float MemoryPctOf(ModContentPack mod, out long memoryUsage)
    {
        memoryUsage = 0;
        if (!ModData.TryGetValue(mod, out CachedModData data))
            return 0f;

        memoryUsage = data.MemoryUsage;
        long total = TotalBytes;
        return total <= 0 ? 0f : Mathf.Clamp01((float)(memoryUsage / (double)total));
    }

    private Color GetColorFor(ModContentPack mod)
    {
        if (!ModData.TryGetValue(mod, out CachedModData data) || LargestModSize <= 0)
            return Color.green;
        return Color.Lerp(NiceBlue, Color.magenta,
            Mathf.Clamp01((float)(data.MemoryUsage / (double)LargestModSize)));
    }

    private static string MemoryString(long memoryUsage, long maximumMemory, bool cap = false)
    {
        if (memoryUsage < 0)
            memoryUsage = 0;
        if (cap && memoryUsage > maximumMemory)
            return ">" + MemoryString(maximumMemory, maximumMemory);
        if (memoryUsage < 1_000)
            return memoryUsage + " bytes";
        if (memoryUsage < 1_000_000)
            return Math.Round(memoryUsage / 1_000d, 2) + "kB";
        if (memoryUsage < 1_000_000_000)
            return Math.Round(memoryUsage / 1_000_000d, 2) + "MB";
        return Math.Round(memoryUsage / 1_000_000_000d, 2) + "GB";
    }

    private Vector2 scrollview = Vector2.zero;
    private static ModContentPack SelMod;

    public void DrawPawnAtlasMemory(Rect rect)
    {
    }

    public void DrawMemoryData(Rect rect)
    {
        Rect top = rect.TopPart(0.75f);
        Rect bottom = rect.BottomPart(0.25f);
        DrawModList(top);
        WriteProcessingData(bottom);
    }

    public void DrawModList(Rect rect)
    {
        rect = new Rect(rect.x, rect.y + 20, rect.width, rect.height - 20);
        Rect leftSide = rect.ContractedBy(5);

        Widgets.DrawBoxSolid(rect, ListingBG);
        GUI.color = Color.gray;
        Widgets.DrawBox(rect);
        GUI.color = Color.white;
        Widgets.Label(new Rect(leftSide.x, rect.y - 20, leftSide.width, 20),
            "GS_AllTextureMemory".Translate());

        GUI.BeginGroup(leftSide);
        try
        {
            float y = 0;
            float extraY = SelMod != null ? 80 : 0;
            Rect viewRect = new(0, 0, leftSide.width, Math.Max(leftSide.height,
                ModData.Count * 20 + extraY));
            Widgets.BeginScrollView(new Rect(0, 0, leftSide.width, leftSide.height), ref scrollview, viewRect, false);

            List<KeyValuePair<ModContentPack, CachedModData>> entries = ModData.ToList();
            entries.Sort((left, right) => right.Value.MemoryUsage.CompareTo(left.Value.MemoryUsage));
            foreach (KeyValuePair<ModContentPack, CachedModData> entry in entries)
            {
                float fraction = MemoryPctOf(entry.Key, out long memoryUsage);
                string label = entry.Key.Name + " (" + MemoryString(memoryUsage, MainMemory) + ") "
                               + fraction.ToStringPercent();
                Rect row = new(0, y, leftSide.width, 20);
                RenderUtils.FillableBarLabeled(new Rect(0, y, leftSide.width, 18), fraction, label,
                    GetColorFor(entry.Key), Color.clear, false);
                Widgets.DrawHighlightIfMouseover(row);
                TooltipHandler.TipRegion(row, label);
                if (Widgets.ButtonInvisible(row))
                    SelMod = SelMod == entry.Key ? null : entry.Key;

                if (SelMod == entry.Key)
                {
                    DrawSelModReadout(entry.Key, new Rect(0, row.yMax, row.width, 80));
                    y += 80;
                }
                y += 20;
            }

            if (entries.Count == 0)
            {
                int availableCount = currentMods.Count > 0 ? currentMods.Count : SnapshotCurrentMods().Count;
                string label = availableCount > 0
                    ? "GS_ModsToProcessLabel".Translate(availableCount)
                    : "GS_NoModsToProcess".Translate();
                Widgets.Label(new Rect(0, y, leftSide.width, Text.CalcHeight(label, leftSide.width)), label);
            }

            Widgets.EndScrollView();
        }
        finally
        {
            GUI.EndGroup();
        }
    }

    private static void DrawSelModReadout(ModContentPack selectedMod, Rect rect)
    {
        if (!ModData.TryGetValue(selectedMod, out CachedModData data))
            return;

        Listing_Standard listing = new();
        listing.Begin(rect);
        listing.Label($"Total textures: {data.TotalTextureCount}");
        listing.Label($"Atlas-sized textures: {data.TexturesInAtlasCount}");
        listing.Label($"Large textures: {data.TexturesWithoutAtlasCount}");
        listing.Label($"Textures with mipmaps: {data.TexturesWithMipMaps}");
        listing.End();
    }

    public void WriteProcessingData(Rect rect)
    {
        GUI.BeginGroup(rect);
        try
        {
            Rect button = new(0, 5, rect.width * 0.20f, 22);
            Rect bar = new(button.xMax + 5, 5, rect.width - button.width - 5, 22);
            float currentY = button.height;

            string buttonText = Calculating
                ? paused ? "GS_CacheContinue".Translate() : "GS_CacheStop".Translate()
                : "GS_CacheRecalc".Translate();
            bool hasTextures = currentMods.Count > 0 || SnapshotCurrentMods().Count > 0;
            if (Widgets.ButtonText(button, buttonText, true, false, hasTextures))
                Notify_ChangeState();

            Widgets.FillableBar(bar, DisplayUsageFraction, StaticContent.blue, Texture2D.blackTexture, true);
            TextAnchor previousAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            string label = MEMOVERFLOW
                ? "GS_CacheWarnRAM".Translate()
                : MemoryString(TotalUsage, MainMemory) + "/" + MemoryString(MainMemory, MainMemory);
            Widgets.Label(bar, label);
            Text.Anchor = previousAnchor;

            if (Calculating)
            {
                string progressLabel = $"{"GS_RecalcProcess".Translate()} ({processedMods}/{currentMods.Count})";
                float height = Text.CalcHeight(progressLabel, rect.width);
                Rect progressRect = new(0, currentY + 5, rect.width, height);
                Widgets.Label(progressRect, progressLabel);
                currentY = progressRect.yMax;
            }

            if (Critical)
            {
                GameFont previousFont = Text.Font;
                Text.Font = GameFont.Small;
                string warning = "GS_CacheWarning".Translate(MEMOVERFLOW
                    ? "GS_CacheWarnOverflow".Translate()
                    : "GS_CacheWarnStruggle".Translate());
                float height = Text.CalcHeight(warning, rect.width);
                Widgets.Label(new Rect(0, currentY + 5, rect.width, height), warning);
                Text.Font = previousFont;
            }
        }
        finally
        {
            GUI.EndGroup();
        }
    }
}
