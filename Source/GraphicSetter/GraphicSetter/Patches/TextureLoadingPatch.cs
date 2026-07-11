using HarmonyLib;
using JetBrains.Annotations;
using RimWorld.IO;
using UnityEngine;

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

        public static bool TryCustomLoad(VirtualFile file, out Texture2D texture, bool readable = false)
        {
            return TexturePipeline.TryLoad(file, readable, out texture);
        }
    }

    public static bool CheckMipMapFix(Texture2D texture, VirtualFile file)
    {
        return texture && (((texture.width & 3) != 0) || ((texture.height & 3) != 0));
    }
}
