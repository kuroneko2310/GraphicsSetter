using System.Collections.Generic;
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

    public static readonly FloatRange MipMapBiasRange = new(-1f, 1f);

    public void ExposeData()
    {
        Scribe_Values.Look(ref enableDDSLoading, "enableDDSLoading", true);
        Scribe_Values.Look(ref overrideMipMapBias, "overrideMipMapBias");
        Scribe_Values.Look(ref mipMapBias, "mipMapBias", DefaultMipMapBias);
        Scribe_Values.Look(ref verboseLogging, "verboseLogging");
        Scribe_Values.Look(ref mainMenuButton, "mainMenuButton", true);
    }

    public bool IsDefault()
        => enableDDSLoading
            && !overrideMipMapBias
            && Mathf.Approximately(mipMapBias, DefaultMipMapBias)
            && !verboseLogging
            && mainMenuButton;

    public void Reset()
    {
        enableDDSLoading = true;
        overrideMipMapBias = false;
        mipMapBias = DefaultMipMapBias;
        verboseLogging = false;
        mainMenuButton = true;
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
    private int cachedTextureCount = -1;
    private float nextQuickStatsRefreshTime;

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

        var tabs = new List<TabRecord>
        {
            new("GS_AdvancedTab".Translate(), delegate { SelTab = GraphicsTabOption.Advanced; }, SelTab == GraphicsTabOption.Advanced),
            new("GS_MemoryTab".Translate(), delegate { SelTab = GraphicsTabOption.Memory; }, SelTab == GraphicsTabOption.Memory)
        };
        TabDrawer.DrawTabs(tabRect, tabs);

        var contentRect = menuRect.ContractedBy(15);

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
        var listing = new Listing_Standard();
        listing.Begin(rect);

        var enableRect = listing.GetRect(26);
        Widgets.CheckboxLabeled(enableRect, "Enable DDS texture loading", ref mainSettings.enableDDSLoading);

        GUI.color = new Color(0.7f, 0.7f, 0.7f);
        Text.Font = GameFont.Tiny;
        const string ddsLoadingDesc = "Loads compressed textures when available\n • Reduces memory usage\n • Improves loading times";
        var size = Text.CalcSize(ddsLoadingDesc);
        var descRect = listing.GetRect(size.y);
        Widgets.Label(descRect.ContractedBy(25, 0), ddsLoadingDesc);
        Text.Font = GameFont.Small;
        GUI.color = Color.white;

        listing.Gap(25);
        enableRect = listing.GetRect(26f);
        Widgets.CheckboxLabeled(enableRect, "Display main menu button", ref mainSettings.mainMenuButton);
        TooltipHandler.TipRegion(enableRect, "Changes to this require a restart to apply");

        listing.Gap(8);

        var biasRect = listing.GetRect(24);
        Widgets.CheckboxLabeled(biasRect, "Override Mipmap Bias", ref mainSettings.overrideMipMapBias);

        if (mainSettings.overrideMipMapBias)
        {
            listing.Gap(10);

            var sliderBg = listing.GetRect(30);

            GUI.color = new Color(0.2f, 0.2f, 0.2f);
            Widgets.DrawBox(sliderBg);
            GUI.color = Color.white;

            var sliderInner = sliderBg.ContractedBy(3);
            var uiBias = InvertBias(mainSettings.mipMapBias);
            uiBias = Widgets.HorizontalSlider(
                sliderInner,
                uiBias,
                SettingsGroup.MipMapBiasRange.min,
                SettingsGroup.MipMapBiasRange.max,
                true,
                $"Bias: {mainSettings.mipMapBias:F2}",
                "Blurry",
                "Sharp",
                0.01f);
            mainSettings.mipMapBias = InvertBias(uiBias);

            listing.Gap(4);
            GUI.color = GetMipmapColor(mainSettings.mipMapBias);
            Text.Anchor = TextAnchor.MiddleCenter;
            var indicatorRect = listing.GetRect(18);
            Widgets.Label(indicatorRect, GetMipmapDescription(mainSettings.mipMapBias));
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        listing.Gap(30);
        DrawQuickStats(listing);

        if (!mainSettings.IsDefault())
        {
            listing.Gap(20);
            var buttonRect = listing.GetRect(35);
            var centeredButton = new Rect(buttonRect.center.x - 100, buttonRect.y, 200, 35);

            if (Widgets.ButtonText(centeredButton, "Reset to Defaults", true, true, true))
                mainSettings.Reset();
        }

        listing.End();
    }

    private static float InvertBias(float bias) => -bias;

    private void DrawSectionHeader(Listing_Standard listing, string text, string icon = null)
    {
        var headerRect = listing.GetRect(30);

        var lineRect = new Rect(headerRect.x, headerRect.y + 5, headerRect.width, 1);
        GUI.color = new Color(0.3f, 0.3f, 0.3f);
        Widgets.DrawLineHorizontal(lineRect.x, lineRect.y, lineRect.width);
        GUI.color = Color.white;

        Text.Font = GameFont.Medium;
        var headerTextRect = headerRect;
        headerTextRect.y += 10;

        if (!string.IsNullOrEmpty(icon))
        {
            GUI.color = new Color(0.8f, 0.8f, 0.3f);
            var iconRect = new Rect(headerTextRect.x, headerTextRect.y, 30, 30);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(iconRect, icon);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            headerTextRect.x += 35;
        }

        Widgets.Label(headerTextRect, text);
        Text.Font = GameFont.Small;

        listing.Gap(5);
    }

    private void DrawQuickStats(Listing_Standard listing)
    {
        var statsRect = listing.GetRect(60);

        GUI.color = new Color(0.15f, 0.15f, 0.15f);
        Widgets.DrawBox(statsRect);
        GUI.color = Color.white;

        var innerStats = statsRect.ContractedBy(10);
        var now = Time.realtimeSinceStartup;
        if (cachedTextureCount < 0 || now >= nextQuickStatsRefreshTime)
        {
            cachedTextureCount = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
            nextQuickStatsRefreshTime = now + 1f;
        }

        var ddsActive = mainSettings.enableDDSLoading;

        Text.Anchor = TextAnchor.MiddleLeft;
        GUI.color = new Color(0.8f, 0.8f, 0.8f);

        var line1 = new Rect(innerStats.x, innerStats.y + 5, innerStats.width, 20);
        var line2 = new Rect(innerStats.x, innerStats.y + 25, innerStats.width, 20);

        Widgets.Label(line1, $"Status: {(ddsActive ? "DDS Loading Active" : "Standard Loading")}");
        Widgets.Label(line2, $"Textures in memory: {cachedTextureCount}");

        if (ddsActive)
        {
            GUI.color = new Color(0.4f, 0.8f, 0.4f);
            var statusDot = new Rect(line1.xMax - 20, line1.y + 5, 10, 10);
            Widgets.DrawBoxSolid(statusDot, GUI.color);
        }

        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
    }

    private static Color GetMipmapColor(float bias)
    {
        if (bias >= 0.5f) return new Color(0.4f, 0.6f, 1f); // Blue for performance
        if (bias > 0f) return new Color(0.4f, 0.8f, 0.8f);
        if (Mathf.Approximately(bias, 0f)) return new Color(0.7f, 0.7f, 0.7f);
        if (bias >= -0.5f) return new Color(0.8f, 0.8f, 0.4f);
        return new Color(1f, 0.6f, 0.4f); // Orange for quality
    }

    private static string GetMipmapDescription(float bias)
    {
        if (bias >= 0.5f) return "Performance mode";
        if (bias > 0f) return "Balanced-Performance";
        if (Mathf.Approximately(bias, 0f)) return "Balanced (Default)";
        if (bias >= -0.5f) return "Balanced-Quality";
        return "Quality mode";
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
