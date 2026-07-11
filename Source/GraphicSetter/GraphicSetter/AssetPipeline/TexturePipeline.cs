using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace GraphicSetter;

internal static class TexturePipeline
{
    public const string AlgorithmVersion = "graphicssetter-texture-pipeline-v2.3";

    public static bool TryLoad(VirtualFile file, bool readable, out Texture2D texture)
    {
        texture = null;
        if (file == null) return false;
        var settings = GraphicsSettings.mainSettings ?? new SettingsGroup();
        var originalPath = file.FullPath;
        var packageId = GagarinTextureCacheClient.ResolvePackageId(originalPath);
        var policy = TexturePipelinePolicy.Create(originalPath, settings, GagarinTextureCacheClient.GetTexturePolicy(packageId, originalPath));
        var ddsPath = Path.ChangeExtension(originalPath, ".dds");
        if (settings.enableDDSLoading && File.Exists(ddsPath) && TryLoadCandidate(file, ddsPath, true, packageId, policy, settings, readable, out texture)) return true;
        return TryLoadCandidate(file, originalPath, false, packageId, policy, settings, readable, out texture);
    }

    private static bool TryLoadCandidate(VirtualFile originalFile, string sourcePath, bool isDds, string packageId, TexturePipelinePolicy policy, SettingsGroup settings, bool readable, out Texture2D texture)
    {
        texture = null;
        try
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return false;
            var sourceHash = GagarinTextureCacheClient.GetSourceContentHash(sourcePath);
            if (string.IsNullOrEmpty(sourceHash)) return false;
            var profile = BuildProfile(policy, settings, isDds);
            var key = HashText(sourceHash + "|" + AlgorithmVersion + "|" + profile);
            var diskCacheAvailable = settings.enableTextureDiskCache && GagarinTextureCacheClient.Available;
            if (diskCacheAvailable)
            {
                if (GagarinTextureCacheClient.TryGetBlob(key, out var blobPath))
                {
                    if (TextureBlobCodec.TryLoad(blobPath, readable, out texture, out _, out var blobBytes))
                    {
                        FinalizeTexture(texture, originalFile, packageId, sourcePath);
                        TexturePipelineDiagnostics.RecordCacheHit(blobBytes);
                        return true;
                    }

                    GagarinTextureCacheClient.InvalidateSource(sourcePath);
                }

                TexturePipelineDiagnostics.RecordCacheMiss();
            }

            var stopwatch = Stopwatch.StartNew();
            var resized = false;
            var mipless = false;
            var sourceBytes = new FileInfo(sourcePath).Length;
            if (isDds)
            {
                var maxDimension = policy.MaxDimension;
                TryReadDdsDimensions(sourcePath, out var sourceWidth, out var sourceHeight);
                if (sourceWidth > 0 && sourceHeight > 0) maxDimension = VramBudgetManager.FitMaxDimension(sourceWidth, sourceHeight, policy.MaxDimension, policy.UseMipMaps, settings);
                texture = DdsPipelineLoader.Load(sourcePath, maxDimension, !policy.UseMipMaps, settings.disableMipMapsAtOrBelow, out sourceWidth, out sourceHeight, out var ddsError);
                if (!texture)
                {
                    if (!ddsError.NullOrEmpty()) Log.Warning($"[Graphics Settings] DDS load failed for '{sourcePath}', trying the original image. {ddsError}");
                    return false;
                }
                resized = texture.width != sourceWidth || texture.height != sourceHeight;
                mipless = texture.mipmapCount <= 1;
            }
            else
            {
                var data = originalFile.ReadAllBytes();
                sourceBytes = data.LongLength;
                texture = TextureImageProcessor.Decode(originalFile, data, policy, settings, out resized, out mipless);
            }
            if (!texture) return false;

            if (diskCacheAvailable)
            {
                try
                {
                    var blob = TextureBlobCodec.Encode(texture, policy.Linear);
                    if (GagarinTextureCacheClient.StoreBlob(key, blob, sourcePath, packageId, profile))
                        TexturePipelineDiagnostics.RecordBlobStored(blob.LongLength);
                }
                catch (Exception cacheException)
                {
                    if (settings.verboseLogging)
                        Log.Warning($"[Graphics Settings] Could not publish the processed texture blob for '{sourcePath}'. The decoded texture will still be used.\n{cacheException}");
                }
            }

            texture.Apply(false, !readable);
            FinalizeTexture(texture, originalFile, packageId, sourcePath);
            stopwatch.Stop();
            TexturePipelineDiagnostics.RecordDecoded(sourceBytes, stopwatch.ElapsedTicks, resized, mipless);
            return true;
        }
        catch (Exception exception)
        {
            if (texture) UnityEngine.Object.DestroyImmediate(texture);
            texture = null;
            if (settings.verboseLogging || !isDds) Log.Warning($"[Graphics Settings][{originalFile.Name}] {(isDds ? "DDS" : "image")} pipeline failed.\n{exception}");
            return false;
        }
    }

    private static void FinalizeTexture(Texture2D texture, VirtualFile file, string packageId, string sourcePath)
    {
        texture.name = Path.GetFileNameWithoutExtension(file.Name);
        VramBudgetManager.CommitTexture(texture);
        RuntimeTextureRegistry.Register(texture, packageId, sourcePath);
    }

    private static string BuildProfile(TexturePipelinePolicy policy, SettingsGroup settings, bool isDds)
    {
        return string.Join("|", new[] { policy.Signature, isDds ? "dds" : "image", settings.vramProfile.ToString(), settings.customVramBudgetMB.ToString(), settings.customMaxTextureDimension.ToString(), settings.disableMipMapsAtOrBelow.ToString(), SystemInfo.graphicsDeviceType.ToString(), SystemInfo.graphicsShaderLevel.ToString(), SystemInfo.graphicsMemorySize.ToString() });
    }

    private static bool TryReadDdsDimensions(string path, out int width, out int height)
    {
        width = 0; height = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 20 || reader.ReadUInt32() != 0x20534444u || reader.ReadUInt32() != 124u) return false;
            _ = reader.ReadUInt32();
            height = checked((int)reader.ReadUInt32());
            width = checked((int)reader.ReadUInt32());
            return width > 0 && height > 0;
        }
        catch { width = 0; height = 0; return false; }
    }

    private static string HashText(string value)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var item in bytes) builder.Append(item.ToString("x2"));
        return builder.ToString();
    }
}
