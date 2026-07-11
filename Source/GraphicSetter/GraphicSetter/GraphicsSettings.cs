using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace GraphicSetter;

public class SettingsGroup : IExposable
{
    public const float DefaultMipMapBias = -0.7f;
    public bool enableDDSLoading = true;
    public bool enableTextureDiskCache = true;
    public bool enableMipMaps = true;
    public bool enableRuntimeCompression = true;
    public bool highQualityCompression = true;
    public bool overrideMipMapBias;
    public float mipMapBias = DefaultMipMapBias;
    public FilterMode filterMode = FilterMode.Trilinear;
    public int anisoLevel = 1;
    public VramProfile vramProfile = VramProfile.Auto;
    public int customVramBudgetMB = 2048;
    public int customMaxTextureDimension = 4096;
    public int disableMipMapsAtOrBelow = 128;
    public bool verboseLogging;
    public bool mainMenuButton = true;
    public static readonly FloatRange MipMapBiasRange = new(-1f, 1f);

    public void ExposeData()
    {
        Scribe_Values.Look(ref enableDDSLoading, "enableDDSLoading", true);
        Scribe_Values.Look(ref enableTextureDiskCache, "enableTextureDiskCache", true);
        Scribe_Values.Look(ref enableMipMaps, "enableMipMaps", true);
        Scribe_Values.Look(ref enableRuntimeCompression, "enableRuntimeCompression", true);
        Scribe_Values.Look(ref highQualityCompression, "highQualityCompression", true);
        Scribe_Values.Look(ref overrideMipMapBias, "overrideMipMapBias");
        Scribe_Values.Look(ref mipMapBias, "mipMapBias", DefaultMipMapBias);
        Scribe_Values.Look(ref filterMode, "filterMode", FilterMode.Trilinear);
        Scribe_Values.Look(ref anisoLevel, "anisoLevel", 1);
        Scribe_Values.Look(ref vramProfile, "vramProfile", VramProfile.Auto);
        Scribe_Values.Look(ref customVramBudgetMB, "customVramBudgetMB", 2048);
        Scribe_Values.Look(ref customMaxTextureDimension, "customMaxTextureDimension", 4096);
        Scribe_Values.Look(ref disableMipMapsAtOrBelow, "disableMipMapsAtOrBelow", 128);
        Scribe_Values.Look(ref verboseLogging, "verboseLogging");
        Scribe_Values.Look(ref mainMenuButton, "mainMenuButton", true);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            anisoLevel = Mathf.Clamp(anisoLevel, 0, 2);
            customVramBudgetMB = Mathf.Clamp(customVramBudgetMB, 256, 65536);
            customMaxTextureDimension = NormalizeDimension(customMaxTextureDimension);
            disableMipMapsAtOrBelow = Mathf.Clamp(disableMipMapsAtOrBelow, 16, 2048);
        }
    }

    public bool IsDefault() => enableDDSLoading && enableTextureDiskCache && enableMipMaps && enableRuntimeCompression && highQualityCompression && !overrideMipMapBias && Mathf.Approximately(mipMapBias, DefaultMipMapBias) && filterMode == FilterMode.Trilinear && anisoLevel == 1 && vramProfile == VramProfile.Auto && customVramBudgetMB == 2048 && customMaxTextureDimension == 4096 && disableMipMapsAtOrBelow == 128 && !verboseLogging && mainMenuButton;

    public void Reset()
    {
        enableDDSLoading = true;
        enableTextureDiskCache = true;
        enableMipMaps = true;
        enableRuntimeCompression = true;
        highQualityCompression = true;
        overrideMipMapBias = false;
        mipMapBias = DefaultMipMapBias;
        filterMode = FilterMode.Trilinear;
        anisoLevel = 1;
        vramProfile = VramProfile.Auto;
        customVramBudgetMB = 2048;
        customMaxTextureDimension = 4096;
        disableMipMapsAtOrBelow = 128;
        verboseLogging = false;
        mainMenuButton = true;
    }

    internal string RuntimeSignature() => string.Join("|", new[] { enableTextureDiskCache.ToString(), enableMipMaps.ToString(), enableRuntimeCompression.ToString(), highQualityCompression.ToString(), overrideMipMapBias.ToString(), mipMapBias.ToString("R"), filterMode.ToString(), anisoLevel.ToString(), vramProfile.ToString(), customVramBudgetMB.ToString(), customMaxTextureDimension.ToString(), disableMipMapsAtOrBelow.ToString() });

    private static int NormalizeDimension(int value)
    {
        var dimensions = new[] { 256, 512, 1024, 2048, 4096, 8192 };
        var closest = dimensions[0];
        foreach (var dimension in dimensions)
            if (Math.Abs(dimension - value) < Math.Abs(closest - value)) closest = dimension;
        return closest;
    }
}

public class GraphicsSettings : ModSettings
{
    public static SettingsGroup mainSettings = new();
    internal enum GraphicsTabOption { Advanced, Memory }
    private GraphicsTabOption SelTab { get; set; } = GraphicsTabOption.Advanced;
    private int cachedTextureCount = -1;
    private float nextQuickStatsRefreshTime;
    private Vector2 advancedScrollPosition;

    public GraphicsSettings() { mainSettings = new SettingsGroup(); }

    public void DoSettingsWindowContents(Rect inRect)
    {
        GUI.BeginGroup(inRect);
        Rect tabRect = new(0, TabDrawer.TabHeight, inRect.width, 0);
        Rect menuRect = new(0, TabDrawer.TabHeight, inRect.width, inRect.height - TabDrawer.TabHeight);
        Widgets.DrawMenuSection(menuRect);
        var tabs = new List<TabRecord>
        {
            new("GS_AdvancedTab".Translate(), delegate { SelTab = GraphicsTabOption.Advanced; }, SelTab == GraphicsTabOption.Advanced),
            new("GS_MemoryTab".Translate(), delegate { SelTab = GraphicsTabOption.Memory; }, SelTab == GraphicsTabOption.Memory)
        };
        TabDrawer.DrawTabs(tabRect, tabs);
        var contentRect = menuRect.ContractedBy(15);
        if (SelTab == GraphicsTabOption.Advanced) DrawAdvanced(contentRect); else DrawMemory(contentRect);
        GUI.EndGroup();
    }

    private void DrawAdvanced(Rect rect)
    {
        var before = mainSettings.RuntimeSignature();
        var viewRect = new Rect(0, 0, rect.width - 18f, 710f);
        Widgets.BeginScrollView(rect, ref advancedScrollPosition, viewRect);
        var listing = new Listing_Standard();
        listing.Begin(viewRect);
        DrawSectionHeader(listing, "Texture loading");
        Checkbox(listing, "Enable DDS sidecar loading", ref mainSettings.enableDDSLoading, "Loads a matching .dds file before the original PNG when supported.");
        Checkbox(listing, "Enable Gagarin GPU-ready texture cache", ref mainSettings.enableTextureDiskCache, "Stores processed Texture2D raw data in MissileGirl/Gagarin. The mod still works without MissileGirl.");
        Checkbox(listing, "Generate and load mipmaps", ref mainSettings.enableMipMaps, "UI, portraits and icons remain mipless. Small textures can skip mipmaps below the selected threshold.");
        Checkbox(listing, "Compress decoded images at runtime", ref mainSettings.enableRuntimeCompression, "Reduces VRAM for suitable non-UI textures after image decoding.");
        if (mainSettings.enableRuntimeCompression) Checkbox(listing, "Use high-quality runtime compression", ref mainSettings.highQualityCompression, "Slower on the first uncached load, but improves the compressed texture.");
        listing.GapLine();
        DrawSectionHeader(listing, "VRAM budget and size policy");
        DrawCycleButton(listing, "Profile", mainSettings.vramProfile.ToString(), () => mainSettings.vramProfile = Next(mainSettings.vramProfile), "Auto reserves VRAM for RimWorld, UI and render targets.");
        if (mainSettings.vramProfile == VramProfile.Custom)
        {
            var budget = listing.GetRect(30f);
            Widgets.Label(budget.LeftPart(0.55f), $"Custom MOD texture budget: {mainSettings.customVramBudgetMB} MB");
            mainSettings.customVramBudgetMB = Mathf.RoundToInt(Widgets.HorizontalSlider(budget.RightPart(0.45f), mainSettings.customVramBudgetMB, 256f, 16384f, true, mainSettings.customVramBudgetMB + " MB", "256", "16384", 256f));
            DrawCycleButton(listing, "Maximum texture dimension", mainSettings.customMaxTextureDimension.ToString(), () => mainSettings.customMaxTextureDimension = NextDimension(mainSettings.customMaxTextureDimension), "Textures are reduced by dropping full resolution levels.");
        }
        DrawCycleButton(listing, "Disable mipmaps at or below", mainSettings.disableMipMapsAtOrBelow + " px", () => mainSettings.disableMipMapsAtOrBelow = NextMipThreshold(mainSettings.disableMipMapsAtOrBelow), "Small textures often gain little from a full mip chain.");
        listing.GapLine();
        DrawSectionHeader(listing, "Live sampling");
        DrawCycleButton(listing, "Filter mode", mainSettings.filterMode.ToString(), () => mainSettings.filterMode = Next(mainSettings.filterMode), "Applied live to textures already loaded through GraphicsSetter.");
        var anisoRect = listing.GetRect(30f);
        Widgets.Label(anisoRect.LeftPart(0.55f), $"Anisotropic level: {mainSettings.anisoLevel}");
        mainSettings.anisoLevel = Mathf.RoundToInt(Widgets.HorizontalSlider(anisoRect.RightPart(0.45f), mainSettings.anisoLevel, 0f, 2f, true, mainSettings.anisoLevel.ToString(), "0", "2", 1f));
        var biasRect = listing.GetRect(24f);
        Widgets.CheckboxLabeled(biasRect, "Override Mipmap Bias", ref mainSettings.overrideMipMapBias);
        if (mainSettings.overrideMipMapBias)
        {
            var uiBias = -mainSettings.mipMapBias;
            uiBias = Widgets.HorizontalSlider(listing.GetRect(32f).ContractedBy(3f), uiBias, SettingsGroup.MipMapBiasRange.min, SettingsGroup.MipMapBiasRange.max, true, $"Bias: {mainSettings.mipMapBias:F2}", "Blurry", "Sharp", 0.01f);
            mainSettings.mipMapBias = -uiBias;
            GUI.color = GetMipmapColor(mainSettings.mipMapBias);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(listing.GetRect(18f), GetMipmapDescription(mainSettings.mipMapBias));
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }
        listing.GapLine();
        DrawSectionHeader(listing, "Interface and diagnostics");
        Checkbox(listing, "Display main menu button", ref mainSettings.mainMenuButton, "Requires a restart.");
        Checkbox(listing, "Verbose GraphicsSetter logging", ref mainSettings.verboseLogging, "Adds detailed pipeline messages to the log.");
        listing.Gap(16f);
        DrawQuickStats(listing);
        if (!mainSettings.IsDefault())
        {
            listing.Gap(12f);
            var buttonRect = listing.GetRect(35f);
            if (Widgets.ButtonText(new Rect(buttonRect.center.x - 100f, buttonRect.y, 200f, 35f), "Reset to Defaults")) mainSettings.Reset();
        }
        listing.End();
        Widgets.EndScrollView();
        if (before != mainSettings.RuntimeSignature())
        {
            RuntimeTextureRegistry.ApplyAll(mainSettings);
            VramBudgetManager.Reset(mainSettings);
            StaticContent.MemoryData.Notify_SettingsChanged();
        }
    }

    private static void Checkbox(Listing_Standard listing, string label, ref bool value, string tooltip)
    {
        var rect = listing.GetRect(26f);
        Widgets.CheckboxLabeled(rect, label, ref value);
        if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(rect, tooltip);
    }

    private static void DrawCycleButton(Listing_Standard listing, string label, string value, Action action, string tooltip)
    {
        var rect = listing.GetRect(30f);
        Text.Anchor = TextAnchor.MiddleLeft;
        Widgets.Label(rect.LeftPart(0.58f), label);
        Text.Anchor = TextAnchor.UpperLeft;
        if (Widgets.ButtonText(rect.RightPart(0.40f), value)) action();
        if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(rect, tooltip);
    }

    private static T Next<T>(T value) where T : struct
    {
        var values = (T[])Enum.GetValues(typeof(T));
        var index = Array.IndexOf(values, value);
        return values[(index + 1) % values.Length];
    }

    private static int NextDimension(int current)
    {
        var values = new[] { 256, 512, 1024, 2048, 4096, 8192 };
        var index = Array.IndexOf(values, current);
        return values[(Math.Max(index, -1) + 1) % values.Length];
    }

    private static int NextMipThreshold(int current)
    {
        var values = new[] { 16, 32, 64, 128, 256, 512, 1024 };
        var index = Array.IndexOf(values, current);
        return values[(Math.Max(index, -1) + 1) % values.Length];
    }

    private static Color GetMipmapColor(float bias)
    {
        if (bias >= 0.5f) return new Color(0.4f, 0.6f, 1f);
        if (bias > 0f) return new Color(0.4f, 0.8f, 0.8f);
        if (Mathf.Approximately(bias, 0f)) return new Color(0.7f, 0.7f, 0.7f);
        if (bias >= -0.5f) return new Color(0.8f, 0.8f, 0.4f);
        return new Color(1f, 0.6f, 0.4f);
    }

    private static string GetMipmapDescription(float bias)
    {
        if (bias >= 0.5f) return "Performance mode";
        if (bias > 0f) return "Balanced-Performance";
        if (Mathf.Approximately(bias, 0f)) return "Balanced (Default)";
        if (bias >= -0.5f) return "Balanced-Quality";
        return "Quality mode";
    }

    private static void DrawSectionHeader(Listing_Standard listing, string text)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(listing.GetRect(30f), text);
        Text.Font = GameFont.Small;
    }

    private void DrawQuickStats(Listing_Standard listing)
    {
        var statsRect = listing.GetRect(115f);
        Widgets.DrawBox(statsRect);
        var inner = statsRect.ContractedBy(8f);
        var now = Time.realtimeSinceStartup;
        if (cachedTextureCount < 0 || now >= nextQuickStatsRefreshTime)
        {
            cachedTextureCount = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
            nextQuickStatsRefreshTime = now + 1f;
        }
        var budgetMb = VramBudgetManager.BudgetBytes / 1024d / 1024d;
        var plannedMb = VramBudgetManager.PlannedBytes / 1024d / 1024d;
        Widgets.Label(inner, $"Textures in memory: {cachedTextureCount}\nGraphicsSetter tracked: {RuntimeTextureRegistry.LiveCount}\nPlanned MOD texture budget: {plannedMb:0} / {budgetMb:0} MB\n" + GagarinTextureCacheClient.GetStatistics());
    }

    public void DrawMemory(Rect rect) => StaticContent.MemoryData.DrawMemoryData(rect);

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref mainSettings, "settings");
        if (Scribe.mode == LoadSaveMode.PostLoadInit) mainSettings ??= new SettingsGroup();
    }
}
