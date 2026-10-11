using System.IO.Compression;
using System.Security.Cryptography;
using HistoryVulcan.Services.Modules;

namespace HistoryVulcan.Services.Nodes;

internal sealed record PackageArtifact(string Module, string Version, string Hash, long Length);

/// <summary>Content-addressed package storage. This directory is never a module discovery root.</summary>
internal sealed class PackageArtifactStore(string root)
{
    internal const long MaximumPackageBytes = 128 * 1024 * 1024;
    internal string Root { get; } = Path.GetFullPath(root);

    internal string PathFor(string hash)
    {
        ValidateHash(hash);
        return Path.Combine(Root, hash.ToUpperInvariant() + ModuleNupkg.Extension);
    }

    internal async Task<PackageArtifact> ImportAsync(string source, CancellationToken cancellation = default)
    {
        if (!ModuleNupkg.TryResolveFile(Path.GetFullPath(source), out var package, out var error))
            throw new InvalidDataException(error);
        await using var stream = File.OpenRead(package);
        return await ImportAsync(stream, null, cancellation).ConfigureAwait(false);
    }

    internal async Task<PackageArtifact> ImportAsync(
        Stream source, string? expectedHash, CancellationToken cancellation = default)
    {
        if (expectedHash != null)
            ValidateHash(expectedHash);
        Directory.CreateDirectory(Root);
        var temporary = Path.Combine(Root, ".incoming-" + Guid.NewGuid().ToString("N") + ModuleNupkg.Extension);
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    size += read;
                    if (size > MaximumPackageBytes)
                        throw new InvalidDataException("模块包超过 128 MiB 传输上限。");
                    digest.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                }
                output.Flush(flushToDisk: true);
            }

            var hash = Convert.ToHexString(digest.GetHashAndReset());
            if (expectedHash != null && !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("包 SHA-256 与交付记录不一致。");
            ValidateContents(temporary);
            if (!ModuleNupkg.TryRead(temporary, out var entry, out _, out var error))
                throw new InvalidDataException(error);
            cancellation.ThrowIfCancellationRequested();
            var destination = PathFor(hash);
            try
            {
                File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                if (!ModuleNupkg.Hash(destination).Equals(hash, StringComparison.Ordinal))
                    throw new InvalidDataException("已有缓存内容损坏，拒绝覆盖。");
            }
            return new PackageArtifact(entry.Name, entry.Version, hash, size);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal PackageArtifact Inspect(string hash)
    {
        var path = PathFor(hash);
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("缓存内容与 SHA-256 不一致。");
        if (!ModuleNupkg.TryRead(path, out var entry, out _, out var error))
            throw new InvalidDataException(error);
        return new PackageArtifact(entry.Name, entry.Version, hash.ToUpperInvariant(), stream.Length);
    }

    internal static void ValidateHash(string hash)
    {
        if (hash == null || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("制品身份必须是 SHA-256 十六进制字符串。");
    }

    private static void ValidateContents(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("包内文件过多。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is "." or "..")
                || !names.Add(name.TrimEnd('/')))
                throw new InvalidDataException("包内存在越界或重复路径。");
            if (entry.Length > MaximumPackageBytes * 4 - expanded)
                throw new InvalidDataException("模块包展开后超过 512 MiB 上限。");
            expanded += entry.Length;
            using var content = entry.Open();
            var buffer = new byte[81920];
            long actual = 0;
            int read;
            while ((read = content.Read(buffer)) > 0)
            {
                actual += read;
                if (actual > entry.Length) throw new InvalidDataException("展开长度与 ZIP 元数据不一致。");
            }
            if (actual != entry.Length) throw new InvalidDataException("包内文件未完整读取。");
        }
    }
}
