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
    private bool shouldStop;
    private int pendingCalculations;
    private long LargestModSize { get; set; }
    private long LargestAtlasSize { get; set; }
    private bool Calculating { get; set; }
    private Vector2 scrollview = Vector2.zero;
    private static ModContentPack SelMod;

    private static IEnumerable<ModContentPack> CurrentMods => LoadedModManager.RunningMods.Where(mod => mod.GetContentHolder<Texture2D>().contentList.Any());
    private long TotalBytes => ModData.Sum(item => item.Value.MemoryUsage);
    private long TotalBytesAtlas => MemoryUsageByAtlas.Sum(item => item.Value);
    private long TotalUsage => TotalBytes + TotalBytesAtlas;
    private long MainMemory => Math.Max(512L * 1024L * 1024L, SystemInfo.graphicsMemorySize * 1000000L);
    private float TotalPctUsage => MainMemory <= 0 ? 0f : (float)(TotalUsage / (double)MainMemory);
    private bool Critical => TotalPctUsage > CriticalPct;
    public bool MEMOVERFLOW => TotalPctUsage > 1f;
    public Coroutine routine, routine2;

    public void Notify_SettingsChanged()
    {
        Calculating = false;
        shouldStop = false;
        pendingCalculations = 0;
        ModData.Clear();
        MemoryUsageByAtlas.Clear();
    }

    private void Notify_ChangeState()
    {
        if (Calculating)
        {
            shouldStop = !shouldStop;
            return;
        }
        shouldStop = false;
        Calculating = true;
        pendingCalculations = 2;
        LargestModSize = 0;
        LargestAtlasSize = 0;
        routine = StaticContent.CoroutineDriver.StartCoroutine(CalculateTextureMemory());
        routine2 = StaticContent.CoroutineDriver.StartCoroutine(CalculateAtlasMemory());
    }

    private IEnumerator CalculateAtlasMemory()
    {
        MemoryUsageByAtlas.Clear();
        foreach (var atlas in GlobalTextureAtlasManager.staticTextureAtlases)
        {
            while (shouldStop) yield return null;
            var colorSize = CachedModData.EstimateTextureMemorySize(atlas.ColorTexture);
            var size = atlas.groupKey.hasMask ? colorSize * 2 : colorSize;
            var pair = new Pair<string, bool>(atlas.groupKey.group.ToString(), atlas.groupKey.hasMask);
            MemoryUsageByAtlas[pair] = MemoryUsageByAtlas.TryGetValue(pair, out var existing) ? existing + size : size;
            LargestAtlasSize = Math.Max(LargestAtlasSize, MemoryUsageByAtlas[pair]);
            yield return null;
        }
        CalculationFinished();
    }

    private IEnumerator CalculateTextureMemory()
    {
        ModData.Clear();
        foreach (var mod in CurrentMods.ToList())
        {
            while (shouldStop) yield return null;
            var data = new CachedModData(mod);
            ModData[mod] = data;
            IReadOnlyCollection<Texture2D> textures = mod.textures.contentList.Values;
            var index = 0;
            foreach (var texture in textures)
            {
                while (shouldStop) yield return null;
                data.RegisterTexture(texture);
                if (++index % 3 == 0) yield return null;
            }
            LargestModSize = Math.Max(LargestModSize, data.MemoryUsage);
        }
        CalculationFinished();
    }

    private void CalculationFinished()
    {
        pendingCalculations = Math.Max(0, pendingCalculations - 1);
        if (pendingCalculations == 0) Calculating = false;
    }

    private float MemoryPctOf(ModContentPack mod, out long memory)
    {
        memory = ModData.TryGetValue(mod, out var data) ? data.MemoryUsage : 0;
        return TotalBytes <= 0 ? 0f : (float)(memory / (double)TotalBytes);
    }

    private Color GetColorFor(ModContentPack mod)
    {
        if (!ModData.TryGetValue(mod, out var data) || LargestModSize <= 0) return Color.green;
        return Color.Lerp(NiceBlue, Color.magenta, (float)(data.MemoryUsage / (double)LargestModSize));
    }

    internal static string MemoryString(long memory, long maxMemory = long.MaxValue, bool cap = false)
    {
        if (cap && memory > maxMemory) return ">" + MemoryString(maxMemory);
        if (memory < 1000) return memory + " bytes";
        if (memory < 1000000) return Math.Round(memory / 1000d, 2) + "kB";
        if (memory < 1000000000) return Math.Round(memory / 1000000d, 2) + "MB";
        return Math.Round(memory / 1000000000d, 2) + "GB";
    }

    public void DrawPawnAtlasMemory(Rect rect) { }

    public void DrawMemoryData(Rect rect)
    {
        DrawModList(rect.TopPart(0.68f));
        WriteProcessingData(rect.BottomPart(0.32f));
    }

    public void DrawModList(Rect rect)
    {
        rect = new Rect(rect.x, rect.y + 20f, rect.width, rect.height - 20f);
        var content = rect.ContractedBy(5f);
        Widgets.DrawBoxSolid(rect, ListingBG);
        GUI.color = Color.gray;
        Widgets.DrawBox(rect);
        GUI.color = Color.white;
        Widgets.Label(new Rect(content.x, rect.y - 20f, content.width, 20f), "GS_AllTextureMemory".Translate());
        GUI.BeginGroup(content);
        var y = 0f;
        var extraHeight = SelMod != null ? 155f : 0f;
        var viewRect = new Rect(0f, 0f, content.width - 16f, Math.Max(content.height, ModData.Count * 20f + extraHeight));
        Widgets.BeginScrollView(new Rect(0f, 0f, content.width, content.height), ref scrollview, viewRect, false);
        foreach (var entry in ModData.OrderByDescending(item => item.Value.MemoryUsage))
        {
            var pct = MemoryPctOf(entry.Key, out var memory);
            var text = $"{entry.Key.Name} ({MemoryString(memory)}) {pct.ToStringPercent()}";
            var row = new Rect(0f, y, viewRect.width, 20f);
            RenderUtils.FillableBarLabeled(new Rect(0f, y, viewRect.width, 18f), pct, text, GetColorFor(entry.Key), Color.clear, false);
            Widgets.DrawHighlightIfMouseover(row);
            TooltipHandler.TipRegion(row, text);
            if (Widgets.ButtonInvisible(row)) SelMod = SelMod == entry.Key ? null : entry.Key;
            y += 20f;
            if (SelMod == entry.Key)
            {
                DrawSelModReadout(entry.Value, new Rect(8f, y, viewRect.width - 8f, 150f));
                y += 155f;
            }
        }
        if (!ModData.Any())
        {
            var modCount = CurrentMods.Count();
            var text = modCount > 0 ? "GS_ModsToProcessLabel".Translate(modCount) : "GS_NoModsToProcess".Translate();
            Widgets.Label(new Rect(0f, 0f, viewRect.width, Text.CalcHeight(text, viewRect.width)), text);
        }
        Widgets.EndScrollView();
        GUI.EndGroup();
    }

    private static void DrawSelModReadout(CachedModData data, Rect rect)
    {
        var listing = new Listing_Standard();
        listing.Begin(rect);
        listing.Label($"Textures: {data.TotalTextureCount} | atlas candidates: {data.TexturesInAtlasCount} | large: {data.TexturesWithoutAtlasCount}");
        listing.Label($"Mipmaps: {data.TexturesWithMipMaps} | no mipmaps: {data.TexturesWithoutMipMaps} | readable: {data.ReadableTextures}");
        listing.Label($"Potential savings without mipmaps: {MemoryString(data.PotentialMipSavings)}");
        listing.Label($"Potential savings one resolution level down: {MemoryString(data.PotentialOneLevelDownSavings)}");
        foreach (var format in data.FormatSummary().Take(3)) listing.Label(format);
        listing.End();
    }

    public void WriteProcessingData(Rect rect)
    {
        GUI.BeginGroup(rect);
        var buttons = new Rect(0f, 5f, rect.width * 0.20f, 24f);
        var barRect = new Rect(buttons.xMax + 5f, 5f, rect.width - buttons.width - 5f, 24f);
        var text = Calculating ? shouldStop ? "GS_CacheContinue".Translate() : "GS_CacheStop".Translate() : "GS_CacheRecalc".Translate();
        if (Widgets.ButtonText(buttons, text, true, false, true)) Notify_ChangeState();
        Widgets.FillableBar(barRect, Mathf.Clamp01(TotalPctUsage), StaticContent.blue, Texture2D.blackTexture, true);
        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(barRect, MEMOVERFLOW ? "GS_CacheWarnRAM".Translate() : MemoryString(TotalUsage) + "/" + MemoryString(MainMemory));
        Text.Anchor = TextAnchor.UpperLeft;
        var y = barRect.yMax + 5f;
        if (Calculating)
        {
            Widgets.Label(new Rect(0f, y, rect.width, 22f), $"{"GS_RecalcProcess".Translate()} ({ModData.Count}/{CurrentMods.Count()})");
            y += 22f;
        }
        if (Critical)
        {
            var warning = "GS_CacheWarning".Translate(MEMOVERFLOW ? "GS_CacheWarnOverflow".Translate() : "GS_CacheWarnStruggle".Translate());
            var height = Text.CalcHeight(warning, rect.width);
            Widgets.Label(new Rect(0f, y, rect.width, height), warning);
            y += height;
        }
        var diagnostics = TexturePipelineDiagnostics.Summary + "\n" + GagarinTextureCacheClient.GetStatistics() + $"\nStatic atlases (estimated without copies): {MemoryString(TotalBytesAtlas)}";
        Widgets.Label(new Rect(0f, y + 3f, rect.width, Math.Max(0f, rect.height - y - 3f)), diagnostics);
        GUI.EndGroup();
    }
}
