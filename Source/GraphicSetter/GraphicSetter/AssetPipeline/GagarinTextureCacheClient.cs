using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Verse;

namespace GraphicSetter;

internal static class GagarinTextureCacheClient
{
    private sealed class SourceHashEntry
    {
        public long Size;
        public long WriteTicks;
        public string Hash;
    }

    private static readonly Dictionary<string, SourceHashEntry> SourceHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();
    private static Type bridgeType;
    private static float nextResolveAttempt;
    private static bool loggedFailure;
    private static bool versionRegistered;

    public static bool Available => ResolveBridge();

    public static bool TryGetBlob(string key, out string path)
    {
        path = null;
        if (!ResolveBridge()) return false;
        try
        {
            var arguments = new object[] { key, null };
            var result = Invoke("TryGetTextureBlob", arguments);
            path = arguments[1] as string;
            return result is bool success && success && !string.IsNullOrEmpty(path) && File.Exists(path);
        }
        catch (Exception exception)
        {
            LogBridgeFailure(exception);
            return false;
        }
    }

    public static bool StoreBlob(string key, byte[] payload, string sourcePath, string packageId, string profile)
    {
        if (!ResolveBridge() || payload == null || payload.Length == 0) return false;
        try
        {
            var result = Invoke("StoreTextureBlob", new object[] { key, payload, sourcePath, packageId ?? string.Empty, profile ?? string.Empty });
            return result is bool success && success;
        }
        catch (Exception exception)
        {
            LogBridgeFailure(exception);
            return false;
        }
    }

    public static string ResolvePackageId(string sourcePath)
    {
        if (!ResolveBridge()) return ResolvePackageIdLocally(sourcePath);
        try
        {
            return Invoke("ResolvePackageIdForPath", new object[] { sourcePath }) as string ?? ResolvePackageIdLocally(sourcePath);
        }
        catch
        {
            return ResolvePackageIdLocally(sourcePath);
        }
    }

    public static string GetTexturePolicy(string packageId, string sourcePath)
    {
        if (!ResolveBridge()) return null;
        try
        {
            return Invoke("GetTexturePolicy", new object[] { packageId ?? string.Empty, sourcePath ?? string.Empty }) as string;
        }
        catch (Exception exception)
        {
            LogBridgeFailure(exception);
            return null;
        }
    }

    public static string GetSourceContentHash(string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return string.Empty;
        if (ResolveBridge())
        {
            try
            {
                var fromManifest = Invoke("GetSourceContentHash", new object[] { sourcePath }) as string;
                if (!string.IsNullOrEmpty(fromManifest)) return fromManifest;
            }
            catch (Exception exception)
            {
                LogBridgeFailure(exception);
            }
        }

        lock (Sync)
        {
            var info = new FileInfo(sourcePath);
            if (SourceHashes.TryGetValue(sourcePath, out var cached) && cached.Size == info.Length && cached.WriteTicks == info.LastWriteTimeUtc.Ticks)
                return cached.Hash;
            using var sha = SHA256.Create();
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = ToHex(sha.ComputeHash(stream));
            SourceHashes[sourcePath] = new SourceHashEntry { Size = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks, Hash = hash };
            return hash;
        }
    }

    public static string GetStatistics()
    {
        if (!ResolveBridge()) return "Gagarin cache service not loaded";
        try { return Invoke("GetStatistics", Array.Empty<object>()) as string ?? "No statistics"; }
        catch { return "Statistics unavailable"; }
    }

    private static bool ResolveBridge()
    {
        if (bridgeType != null)
        {
            RegisterVersion();
            return true;
        }
        if (Time.realtimeSinceStartup < nextResolveAttempt) return false;
        nextResolveAttempt = Time.realtimeSinceStartup + 5f;
        bridgeType = Type.GetType("Gagarin.TextureCacheBridge, Gagarin", false);
        if (bridgeType == null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                bridgeType = assembly.GetType("Gagarin.TextureCacheBridge", false);
                if (bridgeType != null) break;
            }
        }
        RegisterVersion();
        return bridgeType != null;
    }

    private static void RegisterVersion()
    {
        if (bridgeType == null || versionRegistered) return;
        versionRegistered = true;
        try { Invoke("SetGraphicsAlgorithmVersion", new object[] { TexturePipeline.AlgorithmVersion }); }
        catch (Exception exception) { LogBridgeFailure(exception); }
    }

    private static object Invoke(string methodName, object[] arguments)
    {
        var method = bridgeType?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        if (method == null) throw new MissingMethodException(bridgeType?.FullName ?? "Gagarin.TextureCacheBridge", methodName);
        return method.Invoke(null, arguments);
    }

    private static string ResolvePackageIdLocally(string sourcePath)
    {
        var normalized = NormalizePath(sourcePath);
        var bestLength = -1;
        var result = string.Empty;
        foreach (var mod in LoadedModManager.RunningModsListForReading)
        {
            var root = NormalizePath(mod.RootDir);
            if (string.IsNullOrEmpty(root) || !normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) continue;
            if (root.Length <= bestLength) continue;
            bestLength = root.Length;
            result = mod.PackageId;
        }
        return result;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        try { path = Path.GetFullPath(path); }
        catch { }
        return path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }

    private static string ToHex(byte[] data)
    {
        var builder = new StringBuilder(data.Length * 2);
        foreach (var value in data) builder.Append(value.ToString("x2"));
        return builder.ToString();
    }

    private static void LogBridgeFailure(Exception exception)
    {
        if (loggedFailure) return;
        loggedFailure = true;
        Log.Warning($"[Graphics Settings] Gagarin texture-cache bridge failed; texture loading will continue without disk blobs.\n{exception}");
    }
}
