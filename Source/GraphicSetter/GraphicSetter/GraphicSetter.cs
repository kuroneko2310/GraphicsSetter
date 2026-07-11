using HarmonyLib;
using UnityEngine;
using Verse;

namespace GraphicSetter;

public class GraphicSetter : Mod
{
    public static GraphicSetter ModRef { get; private set; }

    public static GraphicsSettings Settings { get; private set; }

    public GraphicSetter(ModContentPack content) : base(content)
    {
        ModRef = this;
        Log.Message("[1.6]Graphics Setter - Loaded");
        Settings = GetSettings<GraphicsSettings>();
        VramBudgetManager.Reset(GraphicsSettings.mainSettings);
        var graphics = new Harmony("com.telefonmast.graphicssettings.rimworld.mod");
        graphics.PatchAll();
    }

    public override void WriteSettings()
    {
        RuntimeTextureRegistry.ApplyAll(GraphicsSettings.mainSettings);
        VramBudgetManager.Reset(GraphicsSettings.mainSettings);
        Settings.Write();
        base.WriteSettings();
    }

    public override string SettingsCategory()
    {
        return "GS_MenuTitle".Translate();
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        Settings.DoSettingsWindowContents(inRect);
    }
}