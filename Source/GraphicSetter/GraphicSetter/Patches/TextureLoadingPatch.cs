using System;
using System.IO;
using HarmonyLib;
using JetBrains.Annotations;
using RimWorld.IO;
using UnityEngine;
using Verse;
using Object = UnityEngine.Object;

namespace GraphicSetter.Patches;

internal static class TextureLoadingPatch
{
    [HarmonyPatch(typeof(ModContentLoader<Texture2D>), "LoadTexture", MethodType.Normal)]
    public static class LoadTexture_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(VirtualFile file, ref Texture2D __result)
        {
            if (TryCustomLoad(file, out Texture2D texture))
            {
                __result = texture;
                return false;
            }

            return true;
        }

        public static Texture2D CustomLoad(VirtualFile file, bool readable = false)
        {
            return TryCustomLoad(file, out Texture2D texture, readable) ? texture : null;
        }

        public static bool TryCustomLoad(VirtualFile file, out Texture2D texture2D, bool readable = false)
        {
            texture2D = null;
            SettingsGroup settings = GraphicsSettings.mainSettings;
            if (file == null)
                return false;

            try
            {
                bool rawMipDataFinalized;
                bool loadedFromCache = TextureBlobCache.TryLoad(file, out texture2D, out rawMipDataFinalized);
                if (loadedFromCache)
                    rawMipDataFinalized = true;

                bool loadedFromDds = false;
                if (!loadedFromCache)
                {
                    rawMipDataFinalized = false;
                    loadedFromDds = settings.enableDDSLoading
                                    && DDSHelper.TryLoadDDS(file, ref rawMipDataFinalized, ref texture2D);
                }

                if (!texture2D && file.Exists)
                {
                    byte[] data = file.ReadAllBytes();
                    bool generateMipMaps = true;
                    if (ImageHeaderUtility.TryReadPngDimensions(data, out int width, out int height))
                        generateMipMaps = TexturePolicy.ShouldGenerateMipMaps(file, width, height);

                    texture2D = new Texture2D(2, 2, TextureFormat.Alpha8, generateMipMaps);
                    if (!texture2D.LoadImage(data))
                    {
                        Object.DestroyImmediate(texture2D);
                        texture2D = null;
                        return false;
                    }

                    // LoadImage has populated the base level. Apply below may build the requested mip chain.
                    rawMipDataFinalized = !generateMipMaps;
                }

                if (!texture2D)
                    return false;

                texture2D.name = Path.GetFileNameWithoutExtension(file.Name);
                texture2D.anisoLevel = 1;

                if (!loadedFromCache)
                {
                    int maxDimension = TexturePolicy.ResolveMaxTextureDimension(file);
                    bool generateResizedMipMaps = TexturePolicy.ShouldGenerateMipMaps(file, texture2D.width,
                        texture2D.height);
                    if (TextureResizer.TryResize(ref texture2D, maxDimension, generateResizedMipMaps))
                        rawMipDataFinalized = true;

                    if (!loadedFromDds && Prefs.TextureCompression && CanCompressSafely(file, texture2D))
                        texture2D.Compress(true);
                }

                texture2D.filterMode = texture2D.mipmapCount > 1 ? FilterMode.Trilinear : FilterMode.Bilinear;
                if (settings.overrideMipMapBias)
                    texture2D.mipMapBias = settings.mipMapBias;

                texture2D.Apply(!rawMipDataFinalized, false);
                if (!loadedFromCache)
                    TextureBlobCache.TryStore(file, texture2D);
                if (!readable)
                    texture2D.Apply(false, true);

                return true;
            }
            catch (Exception exception)
            {
                if (texture2D)
                    Object.DestroyImmediate(texture2D);
                texture2D = null;

                if (settings.verboseLogging || Prefs.LogVerbose)
                    Log.Warning($"[Graphics Settings][{file.Name}] Custom texture load failed; falling back to vanilla. {exception}");
                return false;
            }
        }
    }

    private static bool CanCompressSafely(VirtualFile file, Texture2D texture)
    {
        if (!texture || TexturePolicy.IsDataTexture(file))
            return false;

        bool blockAligned = texture.width >= 4 && texture.height >= 4
                            && (texture.width & 3) == 0
                            && (texture.height & 3) == 0;
        if (!blockAligned && (GraphicsSettings.mainSettings.verboseLogging || Prefs.LogVerbose))
        {
            Log.Message($"[Graphics Settings] Keeping '{file.Name}' uncompressed because its "
                        + $"{texture.width}x{texture.height} dimensions are not BC block-aligned.");
        }

        return blockAligned;
    }
}
