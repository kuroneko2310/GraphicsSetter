using System;
using System.IO;
using HarmonyLib;
using JetBrains.Annotations;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace GraphicSetter.Patches;

internal static class TextureLoadingPatch
{
    [HarmonyPatch(typeof(ModContentLoader<Texture2D>), "LoadTexture", MethodType.Normal)]
    public static class LoadTexture_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(VirtualFile file, ref Texture2D __result)
        {
            if (!TryCustomLoad(file, out var texture))
                return true;

            __result = texture;
            return false;
        }

        public static Texture2D CustomLoad(VirtualFile file, bool readable = false)
        {
            TryCustomLoad(file, out var texture, readable);
            return texture;
        }

        public static bool TryCustomLoad(VirtualFile file, out Texture2D texture2D, bool readable = false)
        {
            texture2D = null;
            if (file == null)
                return false;

            var settings = GraphicsSettings.mainSettings ?? new SettingsGroup();

            try
            {
                var hasMipMapsSet = false;
                var loadedFromDds = settings.enableDDSLoading
                    && DDSHelper.TryLoadDDS(file, ref hasMipMapsSet, ref texture2D);

                if (!texture2D && file.Exists)
                {
                    var data = file.ReadAllBytes();
                    texture2D = new Texture2D(2, 2, TextureFormat.Alpha8, true);
                    if (!texture2D.LoadImage(data))
                        throw new InvalidDataException($"Unity could not decode texture at '{file.FullPath}'.");

                    hasMipMapsSet = FixMipMapsIfNeeded(ref texture2D, data, file);
                }

                if (!texture2D)
                    throw new InvalidDataException($"Could not load texture at '{file.FullPath}'.");

                if (!loadedFromDds && Prefs.TextureCompression)
                    texture2D.Compress(true);

                texture2D.name = Path.GetFileNameWithoutExtension(file.Name);
                texture2D.filterMode = FilterMode.Trilinear;
                texture2D.anisoLevel = 1;

                if (settings.overrideMipMapBias)
                    texture2D.mipMapBias = settings.mipMapBias;

                texture2D.Apply(!hasMipMapsSet, !readable);
                return true;
            }
            catch (Exception exception)
            {
                if (texture2D)
                    UnityEngine.Object.DestroyImmediate(texture2D);

                texture2D = null;
                Log.Warning($"[Graphics Settings][{file.Name}] Custom texture loading failed; falling back to RimWorld's loader.\n{exception}");
                return false;
            }
        }
    }

    private static bool FixMipMapsIfNeeded(ref Texture2D texture2D, byte[] data, VirtualFile file)
    {
        if (!CheckMipMapFix(texture2D, file))
            return false;

        UnityEngine.Object.DestroyImmediate(texture2D);

        texture2D = new Texture2D(2, 2, TextureFormat.Alpha8, false);
        if (!texture2D.LoadImage(data))
            throw new InvalidDataException($"Unity could not decode non-mipmapped texture at '{file.FullPath}'.");

        return true;
    }

    public static bool CheckMipMapFix(Texture2D texture2D, VirtualFile file)
    {
        var needsFix = NeedsMipMapFix(texture2D);
        if (needsFix)
            LogMipMapWarning(texture2D, file);

        return needsFix;
    }

    private static void LogMipMapWarning(Texture2D texture2D, VirtualFile file)
    {
        if (!Prefs.LogVerbose)
            return;

        Log.Warning($"Texture does not support mipmapping, needs to be divisible by 4 ({
            texture2D.width}x{texture2D.height}) for '{file.Name}'");
    }

    private static bool NeedsMipMapFix(Texture2D texture2D)
        => ((texture2D.width & 3) != 0) | ((texture2D.height & 3) != 0);
}
