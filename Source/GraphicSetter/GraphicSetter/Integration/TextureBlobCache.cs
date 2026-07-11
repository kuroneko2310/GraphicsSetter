using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld.IO;
using UnityEngine;
using Verse;
using Object = UnityEngine.Object;

namespace GraphicSetter;

internal static class TextureBlobCache
{
    private const int Magic = 0x47535443; // GSTC
    private const int SchemaVersion = 2;
    private const long MaxSingleEntryBytes = 1024L * 1024L * 1024L;
    private static readonly object Sync = new();
    private static bool cacheTrimmed;

    public static bool TryLoad(VirtualFile source, out Texture2D texture, out bool hasMipMaps)
    {
        texture = null;
        hasMipMaps = false;

        string cachePath = GetCachePath(source);
        if (cachePath == null || !File.Exists(cachePath))
            return false;

        lock (Sync)
        {
            try
            {
                using FileStream stream = new(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using BinaryReader reader = new(stream, Encoding.UTF8, false);
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != SchemaVersion)
                    throw new InvalidDataException("Unsupported texture cache schema");

                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                TextureFormat format = (TextureFormat)reader.ReadInt32();
                int mipCount = reader.ReadInt32();
                int dataLength = reader.ReadInt32();

                if (width <= 0 || height <= 0 || width > 32768 || height > 32768)
                    throw new InvalidDataException($"Invalid cached texture dimensions {width}x{height}");
                if (mipCount <= 0 || dataLength <= 0 || dataLength > MaxSingleEntryBytes)
                    throw new InvalidDataException("Invalid cached texture payload metadata");
                if (stream.Length - stream.Position != dataLength)
                    throw new InvalidDataException("Cached texture payload length mismatch");

                byte[] rawData = reader.ReadBytes(dataLength);
                if (rawData.Length != dataLength)
                    throw new EndOfStreamException("Cached texture payload was truncated");

                texture = new Texture2D(width, height, format, mipCount > 1);
                texture.LoadRawTextureData(rawData);
                hasMipMaps = mipCount > 1;

                try
                {
                    File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow);
                }
                catch
                {
                    // Access-time updates may be disabled by the filesystem.
                }

                return true;
            }
            catch (Exception exception)
            {
                if (texture)
                    Object.DestroyImmediate(texture);
                texture = null;
                hasMipMaps = false;
                TryDelete(cachePath);

                if (GraphicsSettings.mainSettings.verboseLogging)
                    Log.Warning($"[Graphics Settings] Discarded invalid texture cache entry '{cachePath}': {exception}");
                return false;
            }
        }
    }

    public static void TryStore(VirtualFile source, Texture2D texture)
    {
        if (!texture || !texture.isReadable)
            return;

        string cachePath = GetCachePath(source);
        if (cachePath == null)
            return;

        lock (Sync)
        {
            string temporaryPath = cachePath + ".tmp";
            try
            {
                byte[] rawData = texture.GetRawTextureData();
                if (rawData == null || rawData.Length == 0 || rawData.LongLength > MaxSingleEntryBytes)
                    return;

                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (BinaryWriter writer = new(stream, Encoding.UTF8, false))
                {
                    writer.Write(Magic);
                    writer.Write(SchemaVersion);
                    writer.Write(texture.width);
                    writer.Write(texture.height);
                    writer.Write((int)texture.format);
                    writer.Write(Math.Max(1, texture.mipmapCount));
                    writer.Write(rawData.Length);
                    writer.Write(rawData);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(cachePath))
                    File.Delete(cachePath);
                File.Move(temporaryPath, cachePath);
            }
            catch (Exception exception)
            {
                TryDelete(temporaryPath);
                if (GraphicsSettings.mainSettings.verboseLogging)
                    Log.Warning($"[Graphics Settings] Failed writing texture cache entry: {exception}");
            }
        }
    }

    private static string GetCachePath(VirtualFile source)
    {
        if (source == null || !MissileGirlIntegration.TryGetTextureCacheFolder(out string rootFolder))
            return null;

        string directory = Path.Combine(rootFolder, "GraphicsSetter", "V2");
        Directory.CreateDirectory(directory);
        TrimCacheOnce(directory);
        return Path.Combine(directory, BuildSourceKey(source) + ".gstex");
    }

    private static string BuildSourceKey(VirtualFile source)
    {
        long lastWriteTicks = 0;
        try
        {
            if (File.Exists(source.FullPath))
                lastWriteTicks = File.GetLastWriteTimeUtc(source.FullPath).Ticks;
        }
        catch
        {
            // The VirtualFile may not map to a normal filesystem path.
        }

        string key = string.Join("|",
            "gstex-v2",
            source.FullPath?.Replace('\\', '/').ToLowerInvariant(),
            source.Length,
            lastWriteTicks,
            TexturePolicy.BuildFingerprint(),
            Prefs.TextureCompression);

        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", string.Empty);
    }

    private static void TrimCacheOnce(string directory)
    {
        if (cacheTrimmed)
            return;
        cacheTrimmed = true;

        try
        {
            long limitBytes = Math.Max(1024L * 1024L * 1024L,
                Math.Min(16L * 1024L * 1024L * 1024L, TexturePolicy.EffectiveBudgetMb * 2L * 1024L * 1024L));
            FileInfo[] files = new DirectoryInfo(directory).GetFiles("*.gstex")
                .OrderByDescending(file => file.LastAccessTimeUtc)
                .ToArray();
            long total = files.Sum(file => file.Length);
            for (int index = files.Length - 1; index >= 0 && total > limitBytes; index--)
            {
                total -= files[index].Length;
                files[index].Delete();
            }
        }
        catch (Exception exception)
        {
            if (GraphicsSettings.mainSettings.verboseLogging)
                Log.Warning($"[Graphics Settings] Texture cache trimming failed: {exception}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
