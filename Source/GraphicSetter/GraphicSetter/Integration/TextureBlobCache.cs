using System;
using System.Collections.Generic;
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
    private const int SchemaVersion = 3;
    private const int ChecksumLength = 32;
    private const long MaxSingleEntryBytes = 1024L * 1024L * 1024L;
    private const int TrimEveryWrites = 128;

    private static readonly object Sync = new();
    private static readonly HashSet<string> AccessTimeUpdatedThisSession = new(StringComparer.OrdinalIgnoreCase);
    private static bool initialTrimCompleted;
    private static int writesSinceTrim;

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
                byte[] expectedChecksum = reader.ReadBytes(ChecksumLength);

                if (expectedChecksum.Length != ChecksumLength)
                    throw new EndOfStreamException("Cached texture checksum was truncated");
                if (width <= 0 || height <= 0 || width > 32768 || height > 32768)
                    throw new InvalidDataException($"Invalid cached texture dimensions {width}x{height}");
                if (mipCount <= 0 || dataLength <= 0 || dataLength > MaxSingleEntryBytes)
                    throw new InvalidDataException("Invalid cached texture payload metadata");
                if (stream.Length - stream.Position != dataLength)
                    throw new InvalidDataException("Cached texture payload length mismatch");
                if (!SystemInfo.SupportsTextureFormat(format))
                    throw new NotSupportedException($"Cached texture format {format} is unsupported on this device");

                byte[] rawData = reader.ReadBytes(dataLength);
                if (rawData.Length != dataLength)
                    throw new EndOfStreamException("Cached texture payload was truncated");

                using (SHA256 sha = SHA256.Create())
                {
                    byte[] actualChecksum = sha.ComputeHash(rawData);
                    if (!FixedTimeEquals(expectedChecksum, actualChecksum))
                        throw new InvalidDataException("Cached texture payload checksum mismatch");
                }

                texture = new Texture2D(width, height, format, mipCount > 1);
                texture.LoadRawTextureData(rawData);
                hasMipMaps = mipCount > 1;
                TouchAccessTimeOccasionally(cachePath);
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
            string backupPath = cachePath + ".bak";
            try
            {
                byte[] rawData = texture.GetRawTextureData();
                if (rawData == null || rawData.Length == 0 || rawData.LongLength > MaxSingleEntryBytes)
                    return;

                byte[] checksum;
                using (SHA256 sha = SHA256.Create())
                    checksum = sha.ComputeHash(rawData);

                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                TryDelete(temporaryPath);
                using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (BinaryWriter writer = new(stream, Encoding.UTF8, false))
                {
                    writer.Write(Magic);
                    writer.Write(SchemaVersion);
                    writer.Write(texture.width);
                    writer.Write(texture.height);
                    writer.Write((int)texture.format);
                    writer.Write(Math.Max(1, texture.mipmapCount));
                    writer.Write(rawData.Length);
                    writer.Write(checksum);
                    writer.Write(rawData);
                    writer.Flush();
                    stream.Flush(true);
                }

                PublishAtomically(temporaryPath, cachePath, backupPath);
                writesSinceTrim++;
                if (writesSinceTrim >= TrimEveryWrites)
                {
                    writesSinceTrim = 0;
                    TrimCache(Path.GetDirectoryName(cachePath));
                }
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

        string directory = Path.Combine(rootFolder, "GraphicsSetter", $"V{SchemaVersion}");
        Directory.CreateDirectory(directory);

        lock (Sync)
        {
            if (!initialTrimCompleted)
            {
                initialTrimCompleted = true;
                TrimCache(directory);
            }
        }

        return Path.Combine(directory, BuildSourceKey(source) + ".gstex");
    }

    private static string BuildSourceKey(VirtualFile source)
    {
        string sourceMetadata = GetFileMetadata(source.FullPath, source.Length);
        string ddsPath = GraphicsSettings.mainSettings.enableDDSLoading
            ? Path.ChangeExtension(source.FullPath, ".dds")
            : null;
        string ddsMetadata = GetFileMetadata(ddsPath, -1);

        string key = string.Join("|",
            "gstex-v3",
            source.FullPath?.Replace('\\', '/').ToLowerInvariant(),
            sourceMetadata,
            ddsMetadata,
            TexturePolicy.BuildFingerprint(),
            Prefs.TextureCompression,
            Application.unityVersion,
            Application.platform,
            SystemInfo.graphicsDeviceType,
            SystemInfo.graphicsDeviceVendorID,
            SystemInfo.graphicsDeviceVersion);

        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", string.Empty);
    }

    private static string GetFileMetadata(string path, long fallbackLength)
    {
        if (path.NullOrEmpty())
            return "none";

        try
        {
            FileInfo info = new(path);
            if (!info.Exists)
                return $"missing:{fallbackLength}";
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return $"unknown:{fallbackLength}";
        }
    }

    private static void PublishAtomically(string temporaryPath, string cachePath, string backupPath)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                TryDelete(backupPath);
                File.Replace(temporaryPath, cachePath, backupPath, true);
                TryDelete(backupPath);
            }
            else
            {
                File.Move(temporaryPath, cachePath);
            }
        }
        catch (PlatformNotSupportedException)
        {
            PublishWithFallback(temporaryPath, cachePath, backupPath);
        }
        catch (IOException)
        {
            PublishWithFallback(temporaryPath, cachePath, backupPath);
        }
    }

    private static void PublishWithFallback(string temporaryPath, string cachePath, string backupPath)
    {
        if (File.Exists(cachePath))
        {
            TryDelete(backupPath);
            File.Move(cachePath, backupPath);
        }

        try
        {
            File.Move(temporaryPath, cachePath);
            TryDelete(backupPath);
        }
        catch
        {
            if (!File.Exists(cachePath) && File.Exists(backupPath))
                File.Move(backupPath, cachePath);
            throw;
        }
    }

    private static void TrimCache(string directory)
    {
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

    private static void TouchAccessTimeOccasionally(string cachePath)
    {
        if (!AccessTimeUpdatedThisSession.Add(cachePath))
            return;

        try
        {
            DateTime lastAccess = File.GetLastAccessTimeUtc(cachePath);
            if (DateTime.UtcNow - lastAccess >= TimeSpan.FromHours(12))
                File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow);
        }
        catch
        {
            // Access-time updates may be disabled by the filesystem.
        }
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length)
            return false;

        int difference = 0;
        for (int i = 0; i < left.Length; i++)
            difference |= left[i] ^ right[i];
        return difference == 0;
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
