using System;
using System.Diagnostics;
using System.Threading;

namespace GraphicSetter;

internal static class TexturePipelineDiagnostics
{
    private static long cacheHits;
    private static long cacheMisses;
    private static long decodedTextures;
    private static long cachedTextures;
    private static long resizedTextures;
    private static long miplessTextures;
    private static long sourceBytes;
    private static long blobBytes;
    private static long decodeTicks;
    private static long peakWorkingBytes;

    public static void RecordCacheHit(long bytes)
    {
        Interlocked.Increment(ref cacheHits);
        Interlocked.Add(ref blobBytes, Math.Max(0, bytes));
    }

    public static void RecordCacheMiss() => Interlocked.Increment(ref cacheMisses);

    public static void RecordDecoded(long bytes, long elapsedTicks, bool resized, bool mipless)
    {
        Interlocked.Increment(ref decodedTextures);
        Interlocked.Add(ref sourceBytes, Math.Max(0, bytes));
        Interlocked.Add(ref decodeTicks, Math.Max(0, elapsedTicks));
        if (resized) Interlocked.Increment(ref resizedTextures);
        if (mipless) Interlocked.Increment(ref miplessTextures);
        SetPeak(bytes);
    }

    public static void RecordBlobStored(long bytes)
    {
        Interlocked.Increment(ref cachedTextures);
        Interlocked.Add(ref blobBytes, Math.Max(0, bytes));
    }

    public static string Summary
    {
        get
        {
            var hits = Interlocked.Read(ref cacheHits);
            var misses = Interlocked.Read(ref cacheMisses);
            var requests = hits + misses;
            var hitRate = requests == 0 ? 0d : hits / (double)requests;
            var elapsedMs = Interlocked.Read(ref decodeTicks) * 1000d / Stopwatch.Frequency;
            return $"Blob hit rate: {hitRate:P1} ({hits}/{requests})\n" +
                   $"Decoded: {Interlocked.Read(ref decodedTextures)}, stored: {Interlocked.Read(ref cachedTextures)}\n" +
                   $"Downscaled: {Interlocked.Read(ref resizedTextures)}, mipless: {Interlocked.Read(ref miplessTextures)}\n" +
                   $"Decode time: {elapsedMs:0} ms, peak source: {FormatBytes(Interlocked.Read(ref peakWorkingBytes))}\n" +
                   $"Tracked runtime textures: {RuntimeTextureRegistry.LiveCount}";
        }
    }

    private static void SetPeak(long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref peakWorkingBytes)))
        {
            if (Interlocked.CompareExchange(ref peakWorkingBytes, value, current) == current)
                break;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024L * 1024L) return (bytes / 1024d).ToString("0.0") + " KiB";
        if (bytes < 1024L * 1024L * 1024L) return (bytes / 1024d / 1024d).ToString("0.0") + " MiB";
        return (bytes / 1024d / 1024d / 1024d).ToString("0.00") + " GiB";
    }
}
