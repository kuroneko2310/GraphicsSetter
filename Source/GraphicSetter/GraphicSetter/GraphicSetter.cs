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

        Harmony graphics = new("com.telefonmast.graphicssettings.rimworld.mod");
        graphics.PatchAll();

        LongEventHandler.ExecuteWhenFinished(MissileGirlIntegration.NotifyPolicyChanged);
    }

    public override void WriteSettings()
    {
        base.WriteSettings();
        TextureRuntimeRegistry.ApplyCurrentSettings();
        MissileGirlIntegration.NotifyPolicyChanged();
        StaticContent.MemoryData.Notify_SettingsChanged();
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
