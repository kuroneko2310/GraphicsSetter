using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
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
    private sealed class CachedFileIdentity
    {
        public long Length;
        public long LastWriteTicks;
        public string Signature;
    }

    private const int Magic = 0x47535443; // GSTC
    private const int SchemaVersion = TexturePolicy.PipelineVersion;
    private const int ChecksumLength = 32;
    private const int IdentityChunkSize = 32 * 1024;
    private const long AbsoluteMaxSingleEntryBytes = 256L * 1024L * 1024L;
    private const int TrimEveryWrites = 128;

    private static readonly object Sync = new();
    private static readonly object IdentitySync = new();
    private static readonly Dictionary<string, CachedFileIdentity> FileIdentityCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AccessTimeUpdatedThisSession =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool initialTrimCompleted;
    private static int writesSinceTrim;

    public static unsafe bool TryLoad(VirtualFile source, out Texture2D texture, out bool hasMipMaps)
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
                int width;
                int height;
                TextureFormat format;
                int mipCount;
                int dataLength;
                bool linear;
                int payloadOffset;
                byte[] expectedChecksum;

                using (FileStream stream = new(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader reader = new(stream, Encoding.UTF8, false))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != SchemaVersion)
                        throw new InvalidDataException("Unsupported texture cache schema");

                    width = reader.ReadInt32();
                    height = reader.ReadInt32();
                    format = (TextureFormat)reader.ReadInt32();
                    mipCount = reader.ReadInt32();
                    dataLength = reader.ReadInt32();
                    linear = reader.ReadBoolean();
                    expectedChecksum = reader.ReadBytes(ChecksumLength);
                    payloadOffset = checked((int)stream.Position);

                    ValidateHeader(stream, width, height, format, mipCount, dataLength, expectedChecksum);
                    byte[] metadata = BuildChecksumMetadata(width, height, format, mipCount, dataLength, linear);
                    byte[] actualChecksum = ComputeChecksum(metadata, stream);
                    if (!FixedTimeEquals(expectedChecksum, actualChecksum))
                        throw new InvalidDataException("Cached texture header or payload checksum mismatch");
                }

                using MemoryMappedFileSpanWrapper memory = new(OpenExistingMmf(cachePath),
                    MemoryMappedFileAccess.Read);
                Span<byte> payload = memory.GetSpan(payloadOffset).Slice(0, dataLength);

                texture = new Texture2D(width, height, format, mipCount > 1, linear);
                fixed (byte* dataPointer = &payload[0])
                    texture.LoadRawTextureData((IntPtr)dataPointer, dataLength);

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

    public static void TryStore(VirtualFile source, Texture2D texture, bool linear)
    {
        if (!texture || !texture.isReadable)
            return;

        long maximumEntryBytes = GetMaximumEntryBytes();
        long estimatedBytes = TextureMemoryEstimator.Estimate(texture);
        if (estimatedBytes <= 0 || estimatedBytes > maximumEntryBytes)
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
                if (rawData == null || rawData.Length == 0 || rawData.LongLength > maximumEntryBytes)
                    return;

                int mipCount = Math.Max(1, texture.mipmapCount);
                byte[] metadata = BuildChecksumMetadata(texture.width, texture.height, texture.format, mipCount,
                    rawData.Length, linear);
                byte[] checksum;
                using (MemoryStream rawStream = new(rawData, false))
                    checksum = ComputeChecksum(metadata, rawStream);

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
                    writer.Write(mipCount);
                    writer.Write(rawData.Length);
                    writer.Write(linear);
                    writer.Write(checksum);
                    writer.Write(rawData);
                    writer.Flush();
                    stream.Flush(true);
                }

                PublishAtomically(temporaryPath, cachePath, backupPath);
                if (++writesSinceTrim >= TrimEveryWrites)
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

    private static void ValidateHeader(Stream stream, int width, int height, TextureFormat format, int mipCount,
        int dataLength, byte[] checksum)
    {
        if (checksum.Length != ChecksumLength)
            throw new EndOfStreamException("Cached texture checksum was truncated");
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768)
            throw new InvalidDataException($"Invalid cached texture dimensions {width}x{height}");
        if (mipCount <= 0 || dataLength <= 0 || dataLength > AbsoluteMaxSingleEntryBytes)
            throw new InvalidDataException("Invalid cached texture payload metadata");
        if (stream.Length - stream.Position != dataLength)
            throw new InvalidDataException("Cached texture payload length mismatch");
        if (!SystemInfo.SupportsTextureFormat(format))
            throw new NotSupportedException($"Cached texture format {format} is unsupported on this device");
    }

    private static string GetCachePath(VirtualFile source)
    {
        if (source == null || !MissileGirlIntegration.TryGetTextureCacheFolder(out string rootFolder))
            return null;

        string providerDirectory = Path.Combine(rootFolder, "GraphicsSetter");
        string directory = Path.Combine(providerDirectory, $"V{SchemaVersion}");
        Directory.CreateDirectory(directory);

        lock (Sync)
        {
            if (!initialTrimCompleted)
            {
                initialTrimCompleted = true;
                CleanupOldSchemaDirectories(providerDirectory, directory);
                TrimCache(directory);
            }
        }

        return Path.Combine(directory, BuildSourceKey(source) + ".gstex");
    }

    private static string BuildSourceKey(VirtualFile source)
    {
        string sourceMetadata = GetFileIdentity(source.FullPath, source.Length);
        string ddsPath = GraphicsSettings.mainSettings.enableDDSLoading
            ? Path.ChangeExtension(source.FullPath, ".dds")
            : null;
        string ddsMetadata = GetFileIdentity(ddsPath, -1);

        string key = string.Join("|",
            $"gstex-v{SchemaVersion}",
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

    private static string GetFileIdentity(string path, long fallbackLength)
    {
        if (path.NullOrEmpty())
            return "none";

        try
        {
            FileInfo info = new(path);
            if (!info.Exists)
                return $"missing:{fallbackLength}";

            string fullPath = info.FullName;
            long length = info.Length;
            long ticks = info.LastWriteTimeUtc.Ticks;
            lock (IdentitySync)
            {
                if (FileIdentityCache.TryGetValue(fullPath, out CachedFileIdentity cached)
                    && cached.Length == length
                    && cached.LastWriteTicks == ticks)
                    return $"{length}:{ticks}:{cached.Signature}";

                string signature = CalculateBoundarySignature(info);
                FileIdentityCache[fullPath] = new CachedFileIdentity
                {
                    Length = length,
                    LastWriteTicks = ticks,
                    Signature = signature
                };
                return $"{length}:{ticks}:{signature}";
            }
        }
        catch
        {
            return $"unknown:{fallbackLength}";
        }
    }

    private static string CalculateBoundarySignature(FileInfo info)
    {
        using SHA256 sha = SHA256.Create();
        using FileStream stream = new(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            IdentityChunkSize, FileOptions.RandomAccess);

        long[] offsets = info.Length <= IdentityChunkSize * 3L
            ? new[] { 0L }
            : new[]
            {
                0L,
                Math.Max(0L, info.Length / 2L - IdentityChunkSize / 2L),
                Math.Max(0L, info.Length - IdentityChunkSize)
            };

        byte[] buffer = new byte[IdentityChunkSize];
        foreach (long offset in offsets)
        {
            stream.Position = offset;
            int remaining = (int)Math.Min(IdentityChunkSize, stream.Length - offset);
            while (remaining > 0)
            {
                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read <= 0)
                    break;
                sha.TransformBlock(buffer, 0, read, buffer, 0);
                remaining -= read;
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(sha.Hash ?? Array.Empty<byte>()).Replace("-", string.Empty);
    }

    private static byte[] BuildChecksumMetadata(int width, int height, TextureFormat format, int mipCount,
        int dataLength, bool linear)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true))
        {
            writer.Write(width);
            writer.Write(height);
            writer.Write((int)format);
            writer.Write(mipCount);
            writer.Write(dataLength);
            writer.Write(linear);
        }
        return stream.ToArray();
    }

    private static byte[] ComputeChecksum(byte[] metadata, Stream payload)
    {
        using SHA256 sha = SHA256.Create();
        sha.TransformBlock(metadata, 0, metadata.Length, metadata, 0);

        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = payload.Read(buffer, 0, buffer.Length)) > 0)
            sha.TransformBlock(buffer, 0, read, buffer, 0);

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return sha.Hash ?? Array.Empty<byte>();
    }

    private static long GetMaximumEntryBytes()
    {
        long budgetQuarter = TexturePolicy.EffectiveBudgetMb * 1024L * 1024L / 4L;
        return Math.Max(32L * 1024L * 1024L,
            Math.Min(AbsoluteMaxSingleEntryBytes, budgetQuarter));
    }

    private static (MemoryMappedFile file, long length) OpenExistingMmf(string path)
    {
        FileInfo info = new(path);
        long length = info.Length;
        return (MemoryMappedFile.CreateFromFile(info.FullName, FileMode.Open, null, length,
            MemoryMappedFileAccess.Read), length);
    }

    private static void CleanupOldSchemaDirectories(string providerDirectory, string currentDirectory)
    {
        try
        {
            if (!Directory.Exists(providerDirectory))
                return;

            foreach (string directory in Directory.GetDirectories(providerDirectory, "V*"))
            {
                if (!string.Equals(Path.GetFullPath(directory), Path.GetFullPath(currentDirectory),
                        StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(directory, true);
            }
        }
        catch (Exception exception)
        {
            if (GraphicsSettings.mainSettings.verboseLogging)
                Log.Warning($"[Graphics Settings] Old texture cache cleanup failed: {exception}");
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
