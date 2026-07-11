using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace GraphicSetter;

public class SettingsGroup : IExposable
{
    public const float DefaultMipMapBias = -0.7f;

    public bool enableDDSLoading = true;
    public bool overrideMipMapBias;
    public float mipMapBias = DefaultMipMapBias;
    public bool verboseLogging;
    public bool mainMenuButton = true;

    public bool enableAdaptiveTextureBudget;
    public VramBudgetMode vramBudgetMode = VramBudgetMode.Auto;
    public int customVramBudgetMb = 2048;
    public int maxTextureSize;
    public bool disableMipMapsForSmallTextures = true;
    public int smallTextureMipThreshold = 128;
    public bool enableMissileGirlIntegration = true;

    public static readonly FloatRange MipMapBiasRange = new(-1f, 1f);

    public void ExposeData()
    {
        Scribe_Values.Look(ref enableDDSLoading, "enableDDSLoading", true);
        Scribe_Values.Look(ref overrideMipMapBias, "overrideMipMapBias", false);
        Scribe_Values.Look(ref mipMapBias, "mipMapBias", DefaultMipMapBias);
        Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
        Scribe_Values.Look(ref mainMenuButton, "mainMenuButton", true);

        Scribe_Values.Look(ref enableAdaptiveTextureBudget, "enableAdaptiveTextureBudget", false);
        Scribe_Values.Look(ref vramBudgetMode, "vramBudgetMode", VramBudgetMode.Auto);
        Scribe_Values.Look(ref customVramBudgetMb, "customVramBudgetMb", 2048);
        Scribe_Values.Look(ref maxTextureSize, "maxTextureSize", 0);
        Scribe_Values.Look(ref disableMipMapsForSmallTextures, "disableMipMapsForSmallTextures", true);
        Scribe_Values.Look(ref smallTextureMipThreshold, "smallTextureMipThreshold", 128);
        Scribe_Values.Look(ref enableMissileGirlIntegration, "enableMissileGirlIntegration", true);
    }

    public bool IsDefault()
        => enableDDSLoading
           && !overrideMipMapBias
           && Mathf.Approximately(mipMapBias, DefaultMipMapBias)
           && !verboseLogging
           && mainMenuButton
           && !enableAdaptiveTextureBudget
           && vramBudgetMode == VramBudgetMode.Auto
           && customVramBudgetMb == 2048
           && maxTextureSize == 0
           && disableMipMapsForSmallTextures
           && smallTextureMipThreshold == 128
           && enableMissileGirlIntegration;

    public void Reset()
    {
        enableDDSLoading = true;
        overrideMipMapBias = false;
        mipMapBias = DefaultMipMapBias;
        verboseLogging = false;
        mainMenuButton = true;

        enableAdaptiveTextureBudget = false;
        vramBudgetMode = VramBudgetMode.Auto;
        customVramBudgetMb = 2048;
        maxTextureSize = 0;
        disableMipMapsForSmallTextures = true;
        smallTextureMipThreshold = 128;
        enableMissileGirlIntegration = true;
    }
}

public class GraphicsSettings : ModSettings
{
    public static SettingsGroup mainSettings = new();

    internal enum GraphicsTabOption
    {
        Advanced,
        Memory
    }

    private GraphicsTabOption SelTab { get; set; } = GraphicsTabOption.Advanced;
    private string customVramBudgetBuffer;
    private string mipThresholdBuffer;
    private float nextTextureCountRefresh;
    private int cachedTextureCount;

    public GraphicsSettings()
    {
        mainSettings = new SettingsGroup();
    }

    public void DoSettingsWindowContents(Rect inRect)
    {
        GUI.BeginGroup(inRect);

        Rect tabRect = new(0, TabDrawer.TabHeight, inRect.width, 0);
        Rect menuRect = new(0, TabDrawer.TabHeight, inRect.width, inRect.height - TabDrawer.TabHeight);
        Widgets.DrawMenuSection(menuRect);

        List<TabRecord> tabs = new()
        {
            new TabRecord("GS_AdvancedTab".Translate(), delegate { SelTab = GraphicsTabOption.Advanced; },
                SelTab == GraphicsTabOption.Advanced),
            new TabRecord("GS_MemoryTab".Translate(), delegate { SelTab = GraphicsTabOption.Memory; },
                SelTab == GraphicsTabOption.Memory)
        };
        TabDrawer.DrawTabs(tabRect, tabs);

        Rect contentRect = menuRect.ContractedBy(15);
        switch (SelTab)
        {
            case GraphicsTabOption.Advanced:
                DrawAdvanced(contentRect);
                break;
            case GraphicsTabOption.Memory:
                DrawMemory(contentRect);
                break;
        }

        GUI.EndGroup();
    }

    private void DrawAdvanced(Rect rect)
    {
        Listing_Standard listing = new();
        listing.Begin(rect);

        Rect enableRect = listing.GetRect(26);
        Widgets.CheckboxLabeled(enableRect, "Enable DDS texture loading", ref mainSettings.enableDDSLoading);

        GUI.color = new Color(0.7f, 0.7f, 0.7f);
        Text.Font = GameFont.Tiny;
        const string ddsLoadingDesc = "Loads compressed textures when available\n • Reduces memory usage\n • Improves loading times";
        Vector2 size = Text.CalcSize(ddsLoadingDesc);
        Rect descRect = listing.GetRect(size.y);
        Widgets.Label(descRect.ContractedBy(25, 0), ddsLoadingDesc);
        Text.Font = GameFont.Small;
        GUI.color = Color.white;

        listing.Gap(18);
        DrawVramBudgetSettings(listing);

        listing.Gap(18);
        Rect integrationRect = listing.GetRect(26f);
        Widgets.CheckboxLabeled(integrationRect, "Integrate cache invalidation with MissileGirl",
            ref mainSettings.enableMissileGirlIntegration);
        TooltipHandler.TipRegion(integrationRect,
            "When MissileGirl is active, texture policy changes invalidate only its texture cache domain, not the XML cache.");

        listing.Gap(18);
        enableRect = listing.GetRect(26f);
        Widgets.CheckboxLabeled(enableRect, "Display main menu button", ref mainSettings.mainMenuButton);
        TooltipHandler.TipRegion(enableRect, "Changes to this require a restart to apply");

        listing.Gap(12);
        Rect biasRect = listing.GetRect(24);
        Widgets.CheckboxLabeled(biasRect, "Override Mipmap Bias", ref mainSettings.overrideMipMapBias);

        if (mainSettings.overrideMipMapBias)
        {
            listing.Gap(10);
            Rect sliderBg = listing.GetRect(30);
            GUI.color = new Color(0.2f, 0.2f, 0.2f);
            Widgets.DrawBox(sliderBg);
            GUI.color = Color.white;

            Rect sliderInner = sliderBg.ContractedBy(3);
            float uiBias = InvertBias(mainSettings.mipMapBias);
            uiBias = Widgets.HorizontalSlider(sliderInner, uiBias, SettingsGroup.MipMapBiasRange.min,
                SettingsGroup.MipMapBiasRange.max, true, $"Bias: {mainSettings.mipMapBias:F2}", "Blurry", "Sharp",
                0.01f);
            mainSettings.mipMapBias = InvertBias(uiBias);

            listing.Gap(4);
            GUI.color = GetMipmapColor(mainSettings.mipMapBias);
            Text.Anchor = TextAnchor.MiddleCenter;
            Rect indicatorRect = listing.GetRect(18);
            Widgets.Label(indicatorRect, GetMipmapDescription(mainSettings.mipMapBias));
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        listing.Gap(24);
        DrawQuickStats(listing);

        if (!mainSettings.IsDefault())
        {
            listing.Gap(16);
            Rect buttonRect = listing.GetRect(35);
            Rect centeredButton = new(buttonRect.center.x - 100, buttonRect.y, 200, 35);
            if (Widgets.ButtonText(centeredButton, "Reset to Defaults", true, true, true))
            {
                mainSettings.Reset();
                customVramBudgetBuffer = null;
                mipThresholdBuffer = null;
            }
        }

        listing.End();
    }

    private void DrawVramBudgetSettings(Listing_Standard listing)
    {
        Rect adaptiveRect = listing.GetRect(26f);
        Widgets.CheckboxLabeled(adaptiveRect, "Enable adaptive VRAM texture budget",
            ref mainSettings.enableAdaptiveTextureBudget);
        TooltipHandler.TipRegion(adaptiveRect,
            "Optionally downsizes oversized non-UI mod textures before final GPU upload. Disabled by default for compatibility.");

        if (!mainSettings.enableAdaptiveTextureBudget)
            return;

        listing.Gap(6);
        Rect modeRect = listing.GetRect(28f);
        if (Widgets.ButtonText(modeRect, $"VRAM profile: {mainSettings.vramBudgetMode}"))
        {
            List<FloatMenuOption> options = Enum.GetValues(typeof(VramBudgetMode)).Cast<VramBudgetMode>()
                .Select(mode => new FloatMenuOption(mode.ToString(), () => mainSettings.vramBudgetMode = mode))
                .ToList();
            Find.WindowStack.Add(new FloatMenu(options));
        }

        if (mainSettings.vramBudgetMode == VramBudgetMode.Custom)
        {
            listing.Gap(4);
            Rect customRect = listing.GetRect(28f);
            Rect labelRect = customRect.LeftPartPixels(customRect.width - 110f);
            Rect fieldRect = customRect.RightPartPixels(100f);
            Widgets.Label(labelRect, "Custom texture budget (MB)");
            customVramBudgetBuffer ??= mainSettings.customVramBudgetMb.ToString();
            Widgets.TextFieldNumeric(fieldRect, ref mainSettings.customVramBudgetMb, ref customVramBudgetBuffer, 256,
                65536);
        }

        listing.Gap(4);
        Rect maxSizeRect = listing.GetRect(28f);
        string maxSizeLabel = mainSettings.maxTextureSize <= 0 ? "Automatic" : $"{mainSettings.maxTextureSize}px";
        if (Widgets.ButtonText(maxSizeRect, $"Maximum non-UI texture size: {maxSizeLabel}"))
        {
            int[] sizes = { 0, 512, 1024, 2048, 4096, 8192 };
            List<FloatMenuOption> options = sizes.Select(value => new FloatMenuOption(
                value == 0 ? "Automatic" : $"{value}px", () => mainSettings.maxTextureSize = value)).ToList();
            Find.WindowStack.Add(new FloatMenu(options));
        }

        listing.Gap(4);
        Rect smallMipRect = listing.GetRect(26f);
        Widgets.CheckboxLabeled(smallMipRect, "Disable mipmaps for small and UI textures",
            ref mainSettings.disableMipMapsForSmallTextures);

        if (mainSettings.disableMipMapsForSmallTextures)
        {
            Rect thresholdRect = listing.GetRect(28f);
            Rect labelRect = thresholdRect.LeftPartPixels(thresholdRect.width - 110f);
            Rect fieldRect = thresholdRect.RightPartPixels(100f);
            Widgets.Label(labelRect, "Small texture threshold (px)");
            mipThresholdBuffer ??= mainSettings.smallTextureMipThreshold.ToString();
            Widgets.TextFieldNumeric(fieldRect, ref mainSettings.smallTextureMipThreshold, ref mipThresholdBuffer, 16,
                1024);
        }
    }

    private static float InvertBias(float bias) => -bias;

    private void DrawQuickStats(Listing_Standard listing)
    {
        Rect statsRect = listing.GetRect(80);
        GUI.color = new Color(0.15f, 0.15f, 0.15f);
        Widgets.DrawBox(statsRect);
        GUI.color = Color.white;

        if (Time.realtimeSinceStartup >= nextTextureCountRefresh)
        {
            cachedTextureCount = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
            nextTextureCountRefresh = Time.realtimeSinceStartup + 1f;
        }

        Rect innerStats = statsRect.ContractedBy(10);
        Text.Anchor = TextAnchor.MiddleLeft;
        GUI.color = new Color(0.8f, 0.8f, 0.8f);
        Widgets.Label(new Rect(innerStats.x, innerStats.y, innerStats.width, 20),
            $"Status: {(mainSettings.enableDDSLoading ? "DDS Loading Active" : "Standard Loading")}");
        Widgets.Label(new Rect(innerStats.x, innerStats.y + 20, innerStats.width, 20),
            $"Textures in memory: {cachedTextureCount}");
        Widgets.Label(new Rect(innerStats.x, innerStats.y + 40, innerStats.width, 20),
            $"Detected VRAM: {TexturePolicy.DetectedVramMb} MB | Texture budget: {TexturePolicy.EffectiveBudgetMb} MB");
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
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
        return bias switch
        {
            >= 0.5f => "Performance mode",
            > 0f => "Balanced-Performance",
            0f => "Balanced (Default)",
            >= -0.5f => "Balanced-Quality",
            _ => "Quality mode"
        };
    }

    public void DrawMemory(Rect rect)
    {
        StaticContent.MemoryData.DrawMemoryData(rect);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref mainSettings, "settings");

        if (Scribe.mode == LoadSaveMode.PostLoadInit)
            mainSettings ??= new SettingsGroup();
    }
}
