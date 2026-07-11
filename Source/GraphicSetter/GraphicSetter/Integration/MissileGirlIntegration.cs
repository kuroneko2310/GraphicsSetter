using System;
using System.Linq;
using System.Reflection;
using Verse;

namespace GraphicSetter;

internal static class MissileGirlIntegration
{
    private const string MissileGirlPackageId = "vr.missilegirl";
    private const string BridgeTypeName = "Gagarin.GraphicsIntegrationBridge, Gagarin";
    private const string ProviderId = "Telefonmast.GraphicsSettings";

    public static bool IsAvailable => LoadedModManager.RunningMods.Any(mod =>
        string.Equals(mod.PackageId, MissileGirlPackageId, StringComparison.OrdinalIgnoreCase));

    public static void NotifyPolicyChanged()
    {
        if (!GraphicsSettings.mainSettings.enableMissileGirlIntegration || !IsAvailable)
            return;

        try
        {
            Type bridgeType = Type.GetType(BridgeTypeName, false);
            MethodInfo method = bridgeType?.GetMethod("RegisterGraphicsProvider",
                BindingFlags.Public | BindingFlags.Static);
            method?.Invoke(null, new object[]
            {
                ProviderId,
                typeof(GraphicSetter).Assembly.GetName().Version?.ToString() ?? "unknown",
                TexturePolicy.BuildFingerprint()
            });
        }
        catch (Exception exception)
        {
            if (GraphicsSettings.mainSettings.verboseLogging)
                Log.Warning($"[Graphics Settings] MissileGirl integration failed: {exception}");
        }
    }

    public static bool TryGetTextureCacheFolder(out string folder)
    {
        folder = null;
        if (!GraphicsSettings.mainSettings.enableMissileGirlIntegration || !IsAvailable)
            return false;

        try
        {
            NotifyPolicyChanged();
            Type bridgeType = Type.GetType(BridgeTypeName, false);
            MethodInfo method = bridgeType?.GetMethod("GetTextureCacheFolder",
                BindingFlags.Public | BindingFlags.Static);
            folder = method?.Invoke(null, null) as string;
            return !folder.NullOrEmpty();
        }
        catch (Exception exception)
        {
            if (GraphicsSettings.mainSettings.verboseLogging)
                Log.Warning($"[Graphics Settings] Could not access MissileGirl texture cache: {exception}");
            return false;
        }
    }
}
