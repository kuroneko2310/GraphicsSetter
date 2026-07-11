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

    private static readonly object Sync = new();
    private static MethodInfo registerProviderMethod;
    private static MethodInfo getTextureCacheFolderMethod;
    private static string cachedTextureCacheFolder;
    private static string lastRegisteredFingerprint;
    private static bool bridgeResolved;
    private static bool? isAvailable;

    public static bool IsAvailable => isAvailable ??= LoadedModManager.RunningMods.Any(mod =>
        string.Equals(mod.PackageId, MissileGirlPackageId, StringComparison.OrdinalIgnoreCase));

    public static void NotifyPolicyChanged()
    {
        if (!GraphicsSettings.mainSettings.enableMissileGirlIntegration || !IsAvailable)
            return;

        string fingerprint = TexturePolicy.BuildFingerprint();
        lock (Sync)
        {
            if (string.Equals(lastRegisteredFingerprint, fingerprint, StringComparison.Ordinal)
                && !cachedTextureCacheFolder.NullOrEmpty())
                return;

            try
            {
                if (!TryResolveBridge())
                    return;

                registerProviderMethod.Invoke(null, new object[]
                {
                    ProviderId,
                    typeof(GraphicSetter).Assembly.GetName().Version?.ToString() ?? "unknown",
                    fingerprint
                });

                lastRegisteredFingerprint = fingerprint;
                cachedTextureCacheFolder = getTextureCacheFolderMethod.Invoke(null, null) as string;
            }
            catch (Exception exception)
            {
                bridgeResolved = false;
                registerProviderMethod = null;
                getTextureCacheFolderMethod = null;
                cachedTextureCacheFolder = null;

                if (GraphicsSettings.mainSettings.verboseLogging)
                    Log.Warning($"[Graphics Settings] MissileGirl integration failed: {exception}");
            }
        }
    }

    public static bool TryGetTextureCacheFolder(out string folder)
    {
        folder = null;
        if (!GraphicsSettings.mainSettings.enableMissileGirlIntegration || !IsAvailable)
            return false;

        lock (Sync)
        {
            if (!cachedTextureCacheFolder.NullOrEmpty())
            {
                folder = cachedTextureCacheFolder;
                return true;
            }
        }

        NotifyPolicyChanged();
        lock (Sync)
        {
            folder = cachedTextureCacheFolder;
            return !folder.NullOrEmpty();
        }
    }

    public static void ResetCachedBridge()
    {
        lock (Sync)
        {
            lastRegisteredFingerprint = null;
            cachedTextureCacheFolder = null;
            bridgeResolved = false;
            registerProviderMethod = null;
            getTextureCacheFolderMethod = null;
            isAvailable = null;
        }
    }

    private static bool TryResolveBridge()
    {
        if (bridgeResolved)
            return registerProviderMethod != null && getTextureCacheFolderMethod != null;

        Type bridgeType = Type.GetType(BridgeTypeName, false);
        registerProviderMethod = bridgeType?.GetMethod("RegisterGraphicsProvider",
            BindingFlags.Public | BindingFlags.Static);
        getTextureCacheFolderMethod = bridgeType?.GetMethod("GetTextureCacheFolder",
            BindingFlags.Public | BindingFlags.Static);
        bridgeResolved = registerProviderMethod != null && getTextureCacheFolderMethod != null;
        return bridgeResolved;
    }
}
