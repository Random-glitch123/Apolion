using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Apolion
{
    // ═══════════════════════════════════════════════════════════════════════════════
    // MODELS
    // ═══════════════════════════════════════════════════════════════════════════════

    public enum UpdatePhase
    {
        Preparing,
        DownloadingManifest,
        ComparingFiles,
        DownloadingDelta,
        DownloadingArchive,
        Verifying,
        Extracting,
        Installing,
        Completed
    }

    public sealed class UpdateProgress
    {
        public UpdatePhase Phase { get; init; }
        public double Fraction { get; init; } = -1;
        public long BytesReceived { get; init; }
        public long? TotalBytes { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// Manifest produced by tools/Package-Release.ps1 and uploaded next to the
    /// release zip. Every file entry points at the exact compressed byte range
    /// inside the zip, which is what allows the updater to download only the
    /// files that changed.
    /// </summary>
    internal sealed class UpdateManifest
    {
        public int Schema { get; set; } = 1;
        public string Version { get; set; } = string.Empty;
        public string Platform { get; set; } = "win-x64";
        public string ArchiveName { get; set; } = string.Empty;
        public long ArchiveSize { get; set; }
        public string ArchiveSha256 { get; set; } = string.Empty;
        public List<UpdateManifestFile> Files { get; set; } = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static UpdateManifest? TryParse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
                if (manifest == null || !manifest.NormalizeAndValidate())
                    return null;
                return manifest;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, JsonOptions);
        }

        private bool NormalizeAndValidate()
        {
            if (Schema != 1)
                return false;

            Version = (Version ?? string.Empty).Trim().TrimStart('v', 'V');
            Platform = (Platform ?? string.Empty).Trim();
            ArchiveName = (ArchiveName ?? string.Empty).Trim();
            ArchiveSha256 = (ArchiveSha256 ?? string.Empty).Trim().ToLowerInvariant();

            if (Version.Length == 0 || Files == null || Files.Count == 0)
                return false;

            if (ArchiveSha256.Length > 0 && !IsSha256(ArchiveSha256))
                return false;

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files)
            {
                if (file == null)
                    return false;

                if (!PathSafety.TryNormalizeRelativePath(file.Path, out string normalizedPath))
                    return false;

                file.Path = normalizedPath;
                if (!seenPaths.Add(normalizedPath))
                    return false;

                file.Sha256 = (file.Sha256 ?? string.Empty).Trim().ToLowerInvariant();
                if (!IsSha256(file.Sha256))
                    return false;

                if (file.Size < 0 || file.CompressedSize < 0 || file.Offset < 0)
                    return false;

                // The updater can only stream these two standard zip methods.
                if (file.Method != 0 && file.Method != 8)
                    return false;

                if (ArchiveSize > 0 && file.Offset + file.CompressedSize > ArchiveSize)
                    return false;
            }

            return true;
        }

        private static bool IsSha256(string value)
        {
            return value.Length == 64 && value.All(static c =>
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F'));
        }
    }

    internal sealed class UpdateManifestFile
    {
        /// <summary>Path relative to the application folder, using forward slashes.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Uncompressed size in bytes.</summary>
        public long Size { get; set; }

        /// <summary>Lower-case SHA-256 of the uncompressed file.</summary>
        public string Sha256 { get; set; } = string.Empty;

        /// <summary>CRC-32 from the zip central directory.</summary>
        public uint Crc32 { get; set; }

        /// <summary>Offset of the first compressed data byte in the archive.</summary>
        public long Offset { get; set; }

        /// <summary>Number of compressed bytes in the archive.</summary>
        public long CompressedSize { get; set; }

        /// <summary>Zip compression method (0 = stored, 8 = deflate).</summary>
        public int Method { get; set; }
    }

    internal static class PathSafety
    {
        /// <summary>
        /// Validates and normalizes a manifest path. Rejects rooted paths,
        /// drive letters, alternate data streams and directory traversal.
        /// </summary>
        public static bool TryNormalizeRelativePath(string? path, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string candidate = path.Trim().Replace('\\', '/');
            if (candidate.StartsWith('/') || candidate.StartsWith("//", StringComparison.Ordinal) || candidate.Contains(':'))
                return false;

            var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return false;

            foreach (string segment in segments)
            {
                if (segment == "." || segment == "..")
                    return false;
            }

            normalized = string.Join('/', segments);
            return true;
        }

        public static string GetSafeLocalPath(string rootDirectory, string relativePath)
        {
            if (!TryNormalizeRelativePath(relativePath, out string normalized))
                throw new InvalidDataException($"Unsafe update path: {relativePath}");

            string root = Path.GetFullPath(rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(
                Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));

            string rootPrefix = root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Update path escapes the install folder: {relativePath}");

            return fullPath;
        }
    }

    internal sealed class UpdatePlan
    {
        public UpdatePlan(
            UpdateManifest manifest,
            IReadOnlyList<UpdateManifestFile> changedFiles,
            long changedCompressedBytes,
            long changedUncompressedBytes)
        {
            Manifest = manifest;
            ChangedFiles = changedFiles;
            ChangedCompressedBytes = changedCompressedBytes;
            ChangedUncompressedBytes = changedUncompressedBytes;
        }

        public UpdateManifest Manifest { get; }
        public IReadOnlyList<UpdateManifestFile> ChangedFiles { get; }
        public long ChangedCompressedBytes { get; }
        public long ChangedUncompressedBytes { get; }
        public bool IsUpToDate => ChangedFiles.Count == 0;
    }

    internal sealed class ReleaseAssetInfo
    {
        public ReleaseAssetInfo(string name, string url, long size, string? sha256)
        {
            Name = name;
            Url = url;
            Size = size;
            Sha256 = sha256;
        }

        public string Name { get; }
        public string Url { get; }
        public long Size { get; }
        public string? Sha256 { get; }
    }

    internal sealed class ReleaseAssets
    {
        public ReleaseAssetInfo? PrimaryZip { get; set; }
        public ReleaseAssetInfo? Manifest { get; set; }
        public List<ReleaseAssetInfo> AllZips { get; } = new();

        public ReleaseAssetInfo? FindZip(string name)
        {
            return AllZips.FirstOrDefault(z =>
                string.Equals(z.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal sealed class ReleaseSelection
    {
        public string Version { get; init; } = string.Empty;
        public string ReleaseNotes { get; init; } = string.Empty;
        public ReleaseAssets Assets { get; init; } = new();
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // UTILITIES: VersionComparer & Hashing
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compares version strings such as "1.0.1", "v1.0.0-beta.2" or "1.0.0+build.7".
    /// Standard semantic versions are compared with semver precedence rules, so
    /// "1.0.0" is newer than "1.0.0-beta" and "1.0.0-beta.2" is newer than
    /// "1.0.0-beta.1". Unusual version strings fall back to a token comparison.
    /// </summary>
    internal static class VersionComparer
    {
        public static bool IsNewer(string versionA, string versionB)
        {
            return Compare(versionA, versionB) > 0;
        }

        /// <summary>
        /// Returns a positive number when <paramref name="versionA"/> is newer,
        /// zero when both versions are equivalent, and a negative number when
        /// <paramref name="versionB"/> is newer.
        /// </summary>
        public static int Compare(string versionA, string versionB)
        {
            string normalizedA = Normalize(versionA);
            string normalizedB = Normalize(versionB);

            if (TryParseSemanticVersion(normalizedA, out var semanticA) &&
                TryParseSemanticVersion(normalizedB, out var semanticB))
            {
                return CompareSemanticVersions(semanticA, semanticB);
            }

            return CompareTokens(normalizedA, normalizedB);
        }

        private static int CompareSemanticVersions(ParsedVersion versionA, ParsedVersion versionB)
        {
            int sharedReleaseParts = Math.Min(
                versionA.ReleaseParts.Count, versionB.ReleaseParts.Count);

            for (int index = 0; index < sharedReleaseParts; index++)
            {
                int comparison = CompareNumeric(
                    versionA.ReleaseParts[index], versionB.ReleaseParts[index]);
                if (comparison != 0)
                    return Math.Sign(comparison);
            }

            // Preserve the existing update semantics: 1.0.0 is newer than 1.0.
            if (versionA.ReleaseParts.Count != versionB.ReleaseParts.Count)
                return Math.Sign(versionA.ReleaseParts.Count.CompareTo(versionB.ReleaseParts.Count));

            bool hasPrereleaseA = versionA.PrereleaseParts != null;
            bool hasPrereleaseB = versionB.PrereleaseParts != null;

            if (!hasPrereleaseA && !hasPrereleaseB)
                return 0;
            if (!hasPrereleaseA)
                return 1;
            if (!hasPrereleaseB)
                return -1;

            var prereleaseA = versionA.PrereleaseParts!;
            var prereleaseB = versionB.PrereleaseParts!;
            int sharedPrereleaseParts = Math.Min(prereleaseA.Count, prereleaseB.Count);

            for (int index = 0; index < sharedPrereleaseParts; index++)
            {
                string partA = prereleaseA[index];
                string partB = prereleaseB[index];
                bool numericA = IsNumeric(partA);
                bool numericB = IsNumeric(partB);

                int comparison;
                if (numericA && numericB)
                {
                    comparison = CompareNumeric(partA, partB);
                }
                else if (numericA != numericB)
                {
                    // Semver: numeric identifiers have lower precedence than
                    // alphanumeric identifiers.
                    comparison = numericA ? -1 : 1;
                }
                else
                {
                    comparison = string.Compare(partA, partB, StringComparison.OrdinalIgnoreCase);
                }

                if (comparison != 0)
                    return Math.Sign(comparison);
            }

            // A longer prerelease list is newer: beta.2 > beta.
            return Math.Sign(prereleaseA.Count.CompareTo(prereleaseB.Count));
        }

        private static int CompareTokens(string versionA, string versionB)
        {
            var partsA = GetVersionParts(versionA);
            var partsB = GetVersionParts(versionB);
            int sharedPartCount = Math.Min(partsA.Count, partsB.Count);

            for (int index = 0; index < sharedPartCount; index++)
            {
                var partA = partsA[index];
                var partB = partsB[index];

                int comparison;
                if (partA.IsNumeric && partB.IsNumeric)
                {
                    comparison = CompareNumeric(partA.Value, partB.Value);
                }
                else if (partA.IsNumeric != partB.IsNumeric)
                {
                    comparison = partA.IsNumeric ? 1 : -1;
                }
                else
                {
                    comparison = string.Compare(
                        partA.Value, partB.Value, StringComparison.OrdinalIgnoreCase);
                }

                if (comparison != 0)
                    return Math.Sign(comparison);
            }

            if (partsA.Count == partsB.Count)
                return 0;

            // Extra numeric parts make a version newer; an extra alphabetic
            // suffix makes it a prerelease-style version (older).
            if (partsA.Count > partsB.Count)
                return partsA[sharedPartCount].IsNumeric ? 1 : -1;

            return partsB[sharedPartCount].IsNumeric ? -1 : 1;
        }

        private static string Normalize(string version)
        {
            string normalized = (version ?? string.Empty).Trim().TrimStart('v', 'V');

            // Build metadata is irrelevant for update ordering.
            int metadataIndex = normalized.IndexOf('+');
            if (metadataIndex >= 0)
                normalized = normalized[..metadataIndex];

            return normalized;
        }

        private static bool TryParseSemanticVersion(string version, out ParsedVersion parsed)
        {
            parsed = null!;

            if (version.Length == 0)
                return false;

            int prereleaseIndex = version.IndexOf('-');
            string releasePart = prereleaseIndex >= 0 ? version[..prereleaseIndex] : version;
            string? prereleasePart = prereleaseIndex >= 0 ? version[(prereleaseIndex + 1)..] : null;

            string[] releaseParts = releasePart.Split('.');
            if (releaseParts.Length == 0 || releaseParts.Any(static part => !IsNumeric(part)))
                return false;

            string[]? prereleaseParts = null;
            if (prereleasePart != null)
            {
                prereleaseParts = prereleasePart.Split('.');
                if (prereleaseParts.Length == 0 ||
                    prereleaseParts.Any(static part => part.Length == 0))
                {
                    return false;
                }
            }

            parsed = new ParsedVersion(releaseParts, prereleaseParts);
            return true;
        }

        private static int CompareNumeric(string numberA, string numberB)
        {
            string trimmedA = numberA.TrimStart('0');
            string trimmedB = numberB.TrimStart('0');

            int comparison = trimmedA.Length.CompareTo(trimmedB.Length);
            if (comparison == 0)
                comparison = string.Compare(trimmedA, trimmedB, StringComparison.Ordinal);

            return comparison;
        }

        private static bool IsNumeric(string value)
        {
            return value.Length > 0 && value.All(char.IsAsciiDigit);
        }

        private static List<(string Value, bool IsNumeric)> GetVersionParts(string version)
        {
            var parts = new List<(string Value, bool IsNumeric)>();
            int partStart = -1;
            bool partIsNumeric = false;

            for (int index = 0; index <= version.Length; index++)
            {
                bool isNumeric = index < version.Length && char.IsAsciiDigit(version[index]);
                bool isLetter = index < version.Length && char.IsLetter(version[index]);

                if (!isNumeric && !isLetter)
                {
                    if (partStart >= 0)
                    {
                        parts.Add((version[partStart..index], partIsNumeric));
                        partStart = -1;
                    }
                    continue;
                }

                if (partStart >= 0 && isNumeric != partIsNumeric)
                {
                    parts.Add((version[partStart..index], partIsNumeric));
                    partStart = index;
                    partIsNumeric = isNumeric;
                }
                else if (partStart < 0)
                {
                    partStart = index;
                    partIsNumeric = isNumeric;
                }
            }

            return parts;
        }

        private sealed record ParsedVersion(
            IReadOnlyList<string> ReleaseParts,
            IReadOnlyList<string>? PrereleaseParts);
    }

    /// <summary>
    /// Small hashing helpers used by the update planner and the range-download
    /// verifier. SHA-256 is used for file identity; CRC-32 is used because the
    /// value is already available in the zip central directory.
    /// </summary>
    internal static class Hashing
    {
        public const int BufferSize = 128 * 1024;

        public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string ComputeSha256(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.SequentialScan);

            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        /// <summary>
        /// IEEE CRC-32 (the variant used by zip files).
        /// </summary>
        public static uint ComputeCrc32(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.SequentialScan);

            var crc = new Crc32();
            byte[] buffer = new byte[BufferSize];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                crc.Append(buffer.AsSpan(0, read));

            return crc.Value;
        }

        public static string ComputeSha256(ReadOnlySpan<byte> data)
        {
            return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        }
    }

    internal sealed class Crc32
    {
        private static readonly uint[] Table = BuildTable();
        private uint _value = 0xFFFFFFFFu;

        public uint Value => ~_value;

        public void Append(ReadOnlySpan<byte> data)
        {
            uint value = _value;
            foreach (byte b in data)
                value = Table[(value ^ b) & 0xFF] ^ (value >> 8);
            _value = value;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[i] = value;
            }
            return table;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // PLANNER
    // ═══════════════════════════════════════════════════════════════════════════════

    internal static class UpdatePlanner
    {
        /// <summary>
        /// Compares each manifest entry with the installed file. Size mismatch is
        /// enough to mark a file as changed; when the size matches, the SHA-256 is
        /// checked. Files that cannot be read are also marked as changed so the
        /// updater replaces them after the browser exits.
        /// </summary>
        public static async Task<UpdatePlan> CreatePlanAsync(
            UpdateManifest manifest,
            string installDirectory,
            CancellationToken cancellationToken = default)
        {
            var changed = new List<UpdateManifestFile>();
            long compressedBytes = 0;
            long uncompressedBytes = 0;

            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string localPath = PathSafety.GetSafeLocalPath(installDirectory, file.Path);
                if (await NeedsDownloadAsync(localPath, file, cancellationToken).ConfigureAwait(false))
                {
                    changed.Add(file);
                    compressedBytes += file.CompressedSize;
                    uncompressedBytes += file.Size;
                }
            }

            return new UpdatePlan(manifest, changed, compressedBytes, uncompressedBytes);
        }

        /// <summary>
        /// Downloads are only worth doing entry-by-entry when the changed data is
        /// meaningfully smaller than the whole archive. Otherwise the full zip is
        /// faster and more reliable.
        /// </summary>
        public static bool ShouldUseDelta(
            UpdatePlan plan,
            long archiveSize,
            int maxChangedFiles = 200,
            double maxRatioOfArchive = 0.75,
            long minimumSavingsBytes = 2 * 1024 * 1024)
        {
            if (plan.ChangedFiles.Count == 0)
                return false;

            if (plan.ChangedFiles.Count > maxChangedFiles)
                return false;

            if (archiveSize <= 0)
                return plan.ChangedCompressedBytes < minimumSavingsBytes;

            long threshold = Math.Max(minimumSavingsBytes, (long)(archiveSize * maxRatioOfArchive));
            if (plan.ChangedCompressedBytes >= threshold)
                return false;

            // Every changed entry must be inside the archive and use a method we support.
            foreach (var file in plan.ChangedFiles)
            {
                if (file.Method != 0 && file.Method != 8)
                    return false;

                if (file.Offset < 0 || file.CompressedSize < 0)
                    return false;

                if (file.Offset + file.CompressedSize > archiveSize)
                    return false;
            }

            return true;
        }

        private static async Task<bool> NeedsDownloadAsync(
            string localPath,
            UpdateManifestFile file,
            CancellationToken cancellationToken)
        {
            try
            {
                var info = new FileInfo(localPath);
                if (!info.Exists || info.Length != file.Size)
                    return true;

                string actualHash = await Hashing.ComputeSha256Async(localPath, cancellationToken)
                    .ConfigureAwait(false);
                return !string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Locked or unreadable now, but replaceable once the app exits.
                return true;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // REMOTE ZIP READER
    // ═══════════════════════════════════════════════════════════════════════════════

    internal sealed class RemoteZipReader
    {
        private readonly HttpClient _httpClient;
        private readonly Uri _archiveUri;

        public RemoteZipReader(HttpClient httpClient, Uri archiveUri)
        {
            _httpClient = httpClient;
            _archiveUri = archiveUri;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort.
            }
        }

        public async Task ExtractEntryAsync(
            UpdateManifestFile file,
            string destination,
            IProgress<UpdateProgress>? progress,
            UpdatePhase phase,
            long completedBytes,
            long totalBytes,
            string status,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _archiveUri);
            request.Headers.Range = new RangeHeaderValue(file.Offset, file.Offset + file.CompressedSize - 1);

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using var fileStream = new FileStream(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                Hashing.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (file.Method == 0) // Stored
            {
                await stream.CopyToAsync(fileStream, Hashing.BufferSize, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (file.Method == 8) // Deflate
            {
                using var deflateStream = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: false);
                await deflateStream.CopyToAsync(fileStream, Hashing.BufferSize, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                throw new NotSupportedException($"Unsupported zip compression method: {file.Method}");
            }

            // Verify hash
            string actualHash = await Hashing.ComputeSha256Async(destination, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(destination);
                throw new UpdateIntegrityException($"Hash mismatch for {file.Path}");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // RELEASE SELECTOR
    // ═══════════════════════════════════════════════════════════════════════════════

    internal static class ReleaseSelector
    {
        private const string PlatformTag = "win-x64";

        public static ReleaseSelection? Select(string releasesJson, string currentVersion, bool allowPrerelease)
        {
            using var doc = JsonDocument.Parse(releasesJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                return null;

            ReleaseSelection? best = null;

            foreach (var release in root.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean())
                    continue;

                bool isPrerelease = release.TryGetProperty("prerelease", out var preProp) && preProp.GetBoolean();
                if (isPrerelease && !allowPrerelease)
                    continue;

                string? version = ExtractVersion(release);
                if (string.IsNullOrEmpty(version))
                    continue;

                if (!VersionComparer.IsNewer(version, currentVersion))
                    continue;

                var assets = ParseAssets(release);
                if (assets.PrimaryZip == null)
                    continue;

                if (best == null || VersionComparer.IsNewer(version, best.Version))
                {
                    best = new ReleaseSelection
                    {
                        Version = version,
                        ReleaseNotes = release.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
                        Assets = assets
                    };
                }
            }

            return best;
        }

        private static string? ExtractVersion(JsonElement release)
        {
            // Prefer the "name" field (e.g., "Apolion v1.0.1")
            if (release.TryGetProperty("name", out var nameProp))
            {
                string? name = nameProp.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var extracted = ExtractVersionFromString(name);
                    if (!string.IsNullOrEmpty(extracted))
                        return extracted;
                }
            }

            // Fallback to tag_name
            if (release.TryGetProperty("tag_name", out var tagProp))
            {
                string? tag = tagProp.GetString();
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    var extracted = ExtractVersionFromString(tag);
                    if (!string.IsNullOrEmpty(extracted))
                        return extracted;
                }
            }

            return null;
        }

        private static string? ExtractVersionFromString(string input)
        {
            // Try to find a version pattern like "1.0.1" or "v1.0.1"
            var match = System.Text.RegularExpressions.Regex.Match(input, @"v?(\d+\.\d+\.\d+(?:-[\w\.]+)?(?:\+[\w\.]+)?)");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static ReleaseAssets ParseAssets(JsonElement release)
        {
            var assets = new ReleaseAssets();

            if (!release.TryGetProperty("assets", out var assetsArray) ||
                assetsArray.ValueKind != JsonValueKind.Array)
            {
                return assets;
            }

            foreach (var asset in assetsArray.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameProp) ||
                    !asset.TryGetProperty("browser_download_url", out var urlProp) ||
                    !asset.TryGetProperty("size", out var sizeProp))
                {
                    continue;
                }

                string? name = nameProp.GetString();
                string? url = urlProp.GetString();
                long size = sizeProp.GetInt64();

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    continue;

                string? sha256 = null;
                if (asset.TryGetProperty("digest", out var digestProp))
                {
                    sha256 = digestProp.GetString()?.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase);
                }

                var assetInfo = new ReleaseAssetInfo(name, url, size, sha256);

                // Check for manifest
                if (string.Equals(name, "update-manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    assets.Manifest = assetInfo;
                    continue;
                }

                // Check for win-x64 zip (not pdb, not debug, not arm64, not x86)
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains(PlatformTag, StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("pdb", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("debug", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("symbol", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("-x86", StringComparison.OrdinalIgnoreCase))
                {
                    assets.AllZips.Add(assetInfo);

                    // Prefer the one without "full" or similar in name, or just the first
                    if (assets.PrimaryZip == null ||
                        name.Length < assets.PrimaryZip.Name.Length)
                    {
                        assets.PrimaryZip = assetInfo;
                    }
                }
            }

            return assets;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // MAIN UPDATER LOGIC
    // ═══════════════════════════════════════════════════════════════════════════════

    [Serializable]
    internal class UpdateIntegrityException : Exception
    {
        public UpdateIntegrityException() { }
        public UpdateIntegrityException(string message) : base(message) { }
        public UpdateIntegrityException(string message, Exception inner) : base(message, inner) { }
        protected UpdateIntegrityException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    /// <summary>
    /// Manages checking for updates, downloading, and installing new versions from GitHub releases.
    /// </summary>
    public static class Updater
    {
        // ═══════════════════════════════════════════════════════════════════════════════
        // CONFIGURATION
        // ══════════════════════════════════════════════════════════════════════════════

        private const string GitHubRepoOwner = "Random-glitch123";
        private const string GitHubRepoName = "Apolion";
        private const string GitHubApiBaseUrl = "https://api.github.com";
        private const string PlatformTag = "win-x64";
        private const string ManifestFileName = "update-manifest.json";
        private const int MaxDeltaFiles = 200;
        private const double MaxDeltaRatio = 0.75;
        private const long MinDeltaSavingsBytes = 2 * 1024 * 1024;
        private const long MaxManifestBytes = 8 * 1024 * 1024;

        private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan ManifestTimeout = TimeSpan.FromMinutes(2);

        private static readonly string[] CefCacheRelativeDirectories =
        {
            "Sessions",
            Path.Combine("Default", "Code Cache"),
            Path.Combine("Default", "GPUCache"),
            "GrShaderCache",
            Path.Combine("Default", "DawnGraphiteCache"),
            Path.Combine("Default", "DawnWebGPUCache"),
            Path.Combine("Default", "Service Worker"),
            Path.Combine("Default", "blob_storage"),
            Path.Combine("Default", "Cache"),
            Path.Combine("Default", "Media Cache"),
            Path.Combine("Default", "IndexedDB"),
            Path.Combine("Default", "Local Storage"),
            Path.Combine("Default", "Session Storage"),
            Path.Combine("Default", "File System"),
            Path.Combine("Default", "databases"),
            "ShaderCache",
            "Visited Links"
        };

        private static readonly HttpClient _httpClient = CreateHttpClient();
        private static string _currentVersion = "0.9.3";

        static Updater()
        {
            try
            {
                _currentVersion = AppLogger.GetAppVersion();
            }
            catch
            {
                // Keep default
            }
        }

        /// <summary>Current installed version.</summary>
        public static string CurrentVersion => _currentVersion;

        /// <summary>Latest version available on GitHub (null if no newer version was found).</summary>
        public static string? LatestVersion { get; private set; }

        /// <summary>Download URL for the full release zip.</summary>
        public static string? LatestDownloadUrl { get; private set; }

        /// <summary>File name of the full release zip.</summary>
        public static string? LatestDownloadName { get; private set; }

        /// <summary>Size of the full release zip in bytes (0 when unknown).</summary>
        public static long LatestDownloadSize { get; private set; }

        /// <summary>SHA-256 of the full release zip, when GitHub or the manifest provides one.</summary>
        public static string? LatestDownloadSha256 { get; private set; }

        /// <summary>URL of the small update-manifest.json asset, when present.</summary>
        public static string? LatestManifestUrl { get; private set; }

        /// <summary>Release notes for the latest version.</summary>
        public static string? LatestReleaseNotes { get; private set; }

        /// <summary>Human-readable reason for the last failed update attempt.</summary>
        public static string? LastError { get; private set; }

        internal static ReleaseAssets? LatestAssets { get; private set; }

        // ═══════════════════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Checks GitHub for a newer version. Stable releases are preferred; a
        /// prerelease is considered when the installed version is itself a
        /// prerelease or APOLION_ALLOW_PRERELEASE=1 is set.
        /// </summary>
        public static async Task<bool> CheckForUpdatesAsync(
            bool? includePrerelease = null,
            CancellationToken cancellationToken = default)
        {
            ResetLatestState();

            try
            {
                string apiUrl =
                    $"{GitHubApiBaseUrl}/repos/{GitHubRepoOwner}/{GitHubRepoName}/releases?per_page=30";

                string json = await GetStringWithRetryAsync(
                    apiUrl, maxBytes: 4 * 1024 * 1024, ApiTimeout, cancellationToken,
                    acceptGitHubJson: true).ConfigureAwait(false);

                bool allowPrerelease = includePrerelease ?? ShouldAllowPrerelease();
                ReleaseSelection? best = ReleaseSelector.Select(json, _currentVersion, allowPrerelease);

                if (best == null)
                {
                    AppLogger.Info($"No update needed. Current: {_currentVersion}");
                    return false;
                }

                LatestVersion = best.Version;
                LatestReleaseNotes = best.ReleaseNotes;
                LatestAssets = best.Assets;
                LatestDownloadUrl = best.Assets.PrimaryZip.Url;
                LatestDownloadName = best.Assets.PrimaryZip.Name;
                LatestDownloadSize = best.Assets.PrimaryZip.Size;
                LatestDownloadSha256 = best.Assets.PrimaryZip.Sha256;
                LatestManifestUrl = best.Assets.Manifest?.Url;

                AppLogger.Info(
                    $"Update available: {_currentVersion} -> {best.Version} " +
                    $"(manifest: {(LatestManifestUrl != null ? "yes" : "no")}, " +
                    $"zip: {LatestDownloadName})");
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                AppLogger.Warn("Update check timed out");
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Update check failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Downloads the update, verifies it, and launches the external updater.
        /// Returns true when the updater was started (the app should then exit).
        /// </summary>
        public static async Task<bool> DownloadAndInstallAsync(
            IProgress<UpdateProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastError = null;

            if (string.IsNullOrWhiteSpace(LatestDownloadUrl) ||
                !Uri.TryCreate(LatestDownloadUrl, UriKind.Absolute, out _))
            {
                LastError = "No download URL is available for this release.";
                AppLogger.Error(LastError);
                return false;
            }

            string tempRoot = Path.Combine(Path.GetTempPath(), "Apolion_Update");
            string stagingDir = Path.Combine(tempRoot, "staging");

            try
            {
                Directory.CreateDirectory(tempRoot);
                DeleteDirectory(stagingDir);
                Directory.CreateDirectory(stagingDir);

                progress?.Report(new UpdateProgress
                {
                    Phase = UpdatePhase.Preparing,
                    Fraction = -1,
                    Message = "Preparing update..."
                });

                // ── 1. Try the manifest-driven delta path ──────────────────
                UpdateManifest? manifest = await TryGetManifestAsync(progress, cancellationToken)
                    .ConfigureAwait(false);

                ReleaseAssetInfo? archiveAsset = null;
                if (manifest != null)
                {
                    archiveAsset = ResolveArchiveAsset(manifest);
                    if (archiveAsset == null)
                    {
                        AppLogger.Warn(
                            "Update manifest refers to an archive that is not in this release; " +
                            "falling back to the full zip.");
                        manifest = null;
                    }
                }

                if (manifest != null && archiveAsset != null)
                {
                    var plan = await UpdatePlanner.CreatePlanAsync(
                        manifest, GetInstallDirectory(), cancellationToken).ConfigureAwait(false);

                    AppLogger.Info(
                        $"Delta plan: {plan.ChangedFiles.Count} changed file(s), " +
                        $"{FormatBytes(plan.ChangedCompressedBytes)} compressed, " +
                        $"{FormatBytes(plan.ChangedUncompressedBytes)} installed");

                    if (plan.IsUpToDate)
                    {
                        LastError = "The installed files already match the latest release.";
                        AppLogger.Info(LastError);
                        return false;
                    }

                    if (UpdatePlanner.ShouldUseDelta(
                            plan, manifest.ArchiveSize, MaxDeltaFiles, MaxDeltaRatio, MinDeltaSavingsBytes))
                    {
                        bool deltaSucceeded = await TryApplyDeltaAsync(
                            manifest, archiveAsset, plan, stagingDir, progress, cancellationToken)
                            .ConfigureAwait(false);

                        if (deltaSucceeded)
                            return true;

                        // The delta attempt may have written part of the staging
                        // folder before failing. Start the full path cleanly.
                        DeleteDirectory(stagingDir);
                        Directory.CreateDirectory(stagingDir);
                    }
                    else
                    {
                        AppLogger.Info("Delta would not save enough bandwidth; using the full zip.");
                    }
                }

                // ── 2. Full zip path ───────────────────────────────────────
                return await DownloadFullArchiveAndInstallAsync(
                    manifest, stagingDir, tempRoot, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LastError = "Update cancelled.";
                AppLogger.Info("Update cancelled by user");
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AppLogger.Exception("Download and install failed", ex);
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════
        // INTERNAL IMPLEMENTATION
        // ═════════════════════════════════════════════════════════════════════════════

        private static async Task<bool> TryApplyDeltaAsync(
            UpdateManifest manifest,
            ReleaseAssetInfo archiveAsset,
            UpdatePlan plan,
            string stagingDir,
            IProgress<UpdateProgress>? progress,
            CancellationToken cancellationToken)
        {
            try
            {
                AppLogger.Info($"Starting delta download from {archiveAsset.Name}");

                var reader = new RemoteZipReader(_httpClient, new Uri(archiveAsset.Url));
                long totalBytes = plan.ChangedCompressedBytes;
                long completedBytes = 0;

                for (int index = 0; index < plan.ChangedFiles.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var file = plan.ChangedFiles[index];
                    string destination = PathSafety.GetSafeLocalPath(stagingDir, file.Path);
                    string status = plan.ChangedFiles.Count == 1
                        ? $"Downloading {file.Path}..."
                        : $"Downloading {file.Path} ({index + 1}/{plan.ChangedFiles.Count})...";

                    progress?.Report(new UpdateProgress
                    {
                        Phase = UpdatePhase.DownloadingDelta,
                        Fraction = totalBytes > 0 ? (double)completedBytes / totalBytes : -1,
                        BytesReceived = completedBytes,
                        TotalBytes = totalBytes > 0 ? totalBytes : null,
                        Message = status
                    });

                    await reader.ExtractEntryAsync(
                        file, destination, progress, UpdatePhase.DownloadingDelta,
                        completedBytes, totalBytes, status, cancellationToken).ConfigureAwait(false);

                    completedBytes += file.CompressedSize;
                }

                WriteManifestToFolder(stagingDir, manifest);
                PrepareAppDataForUpdate();

                string? removeList = WriteRemoveList(manifest, Path.Combine(
                    Path.GetTempPath(), "Apolion_Update", "remove-list.txt"));

                progress?.Report(new UpdateProgress
                {
                    Phase = UpdatePhase.Installing,
                    Fraction = 1,
                    BytesReceived = completedBytes,
                    TotalBytes = totalBytes > 0 ? totalBytes : null,
                    Message = "Starting the updater..."
                });

                return LaunchUpdater(stagingDir, removeList);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AppLogger.Warn($"Delta update failed, falling back to full zip: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> DownloadFullArchiveAndInstallAsync(
            UpdateManifest? manifest,
            string stagingDir,
            string tempRoot,
            IProgress<UpdateProgress>? progress,
            CancellationToken cancellationToken)
        {
            string archiveUrl = LatestDownloadUrl!;
            string? expectedSha = LatestDownloadSha256;
            long expectedSize = LatestDownloadSize;

            if (manifest != null && !string.IsNullOrWhiteSpace(manifest.ArchiveSha256))
                expectedSha = manifest.ArchiveSha256;
            if (manifest != null && manifest.ArchiveSize > 0)
                expectedSize = manifest.ArchiveSize;

            string zipPath = Path.Combine(tempRoot, "update.zip");

            progress?.Report(new UpdateProgress
            {
                Phase = UpdatePhase.DownloadingArchive,
                Fraction = -1,
                Message = "Downloading the full update..."
            });

            await DownloadFileAsync(
                archiveUrl, zipPath, expectedSize, expectedSha, progress, cancellationToken)
                .ConfigureAwait(false);

            string extractDir = Path.Combine(tempRoot, "extracted");
            DeleteDirectory(extractDir);
            Directory.CreateDirectory(extractDir);

            progress?.Report(new UpdateProgress
            {
                Phase = UpdatePhase.Extracting,
                Fraction = -1,
                Message = "Extracting update..."
            });

            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true),
                cancellationToken).ConfigureAwait(false);

            string? appFolder = FindAppFolder(extractDir);
            if (string.IsNullOrEmpty(appFolder))
            {
                LastError = "The update archive does not contain Apolion.exe.";
                AppLogger.Error(LastError);
                return false;
            }

            if (manifest != null)
                WriteManifestToFolder(appFolder, manifest);

            PrepareAppDataForUpdate();

            string? removeList = manifest != null
                ? WriteRemoveList(
                    manifest,
                    Path.Combine(Path.GetTempPath(), "Apolion_Update", "remove-list.txt"))
                : null;

            progress?.Report(new UpdateProgress
            {
                Phase = UpdatePhase.Installing,
                Fraction = 1,
                Message = "Starting the updater..."
            });

            return LaunchUpdater(appFolder, removeList);
        }

        private static async Task<UpdateManifest?> TryGetManifestAsync(
            IProgress<UpdateProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(LatestManifestUrl))
                return null;

            progress?.Report(new UpdateProgress
            {
                Phase = UpdatePhase.DownloadingManifest,
                Fraction = -1,
                Message = "Looking for a smaller update..."
            });

            try
            {
                string json = await GetStringWithRetryAsync(
                    LatestManifestUrl, MaxManifestBytes, ManifestTimeout, cancellationToken,
                    acceptGitHubJson: false).ConfigureAwait(false);

                UpdateManifest? manifest = UpdateManifest.TryParse(json);
                if (manifest == null)
                {
                    AppLogger.Warn("Update manifest is invalid; using the full zip.");
                    return null;
                }

                if (!string.Equals(manifest.Version, LatestVersion, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn(
                        $"Update manifest version {manifest.Version} does not match release {LatestVersion}; " +
                        "using the full zip.");
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(manifest.Platform) &&
                    !string.Equals(manifest.Platform, PlatformTag, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn(
                        $"Update manifest is for {manifest.Platform}, not {PlatformTag}; using the full zip.");
                    return null;
                }

                return manifest;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Could not download update manifest: {ex.Message}");
                return null;
            }
        }

        private static ReleaseAssetInfo? ResolveArchiveAsset(UpdateManifest manifest)
        {
            if (LatestAssets == null)
                return null;

            if (!string.IsNullOrWhiteSpace(manifest.ArchiveName))
            {
                ReleaseAssetInfo? match = LatestAssets.FindZip(manifest.ArchiveName);
                if (match == null)
                    return null;

                // The manifest describes this specific archive; prefer the
                // manifest's size/hash when it supplied them.
                return new ReleaseAssetInfo(
                    match.Name,
                    match.Url,
                    manifest.ArchiveSize > 0 ? manifest.ArchiveSize : match.Size,
                    !string.IsNullOrWhiteSpace(manifest.ArchiveSha256)
                        ? manifest.ArchiveSha256
                        : match.Sha256);
            }

            return LatestAssets.PrimaryZip;
        }

        /// <summary>
        /// Downloads a file with HTTP Range resume support. A complete .part file
        /// is reused when its hash matches, so an interrupted 100 MB download does
        /// not begin from zero again.
        /// </summary>
        private static async Task DownloadFileAsync(
            string url,
            string destinationPath,
            long expectedSize,
            string? expectedSha256,
            IProgress<UpdateProgress>? progress,
            CancellationToken cancellationToken)
        {
            string partPath = destinationPath + ".part";

            if (File.Exists(destinationPath))
            {
                if (!string.IsNullOrWhiteSpace(expectedSha256) &&
                    (expectedSize <= 0 || new FileInfo(destinationPath).Length == expectedSize))
                {
                    string existingHash = await Hashing.ComputeSha256Async(destinationPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (string.Equals(existingHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        progress?.Report(new UpdateProgress
                        {
                            Phase = UpdatePhase.Verifying,
                            Fraction = 1,
                            Message = "Using the already downloaded update..."
                        });
                        return;
                    }
                }

                TryDeleteFile(destinationPath);
            }

            if (string.IsNullOrWhiteSpace(expectedSha256))
                TryDeleteFile(partPath);

            long existingBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (expectedSize > 0 && existingBytes >= expectedSize)
            {
                TryDeleteFile(partPath);
                existingBytes = 0;
            }

            int attempt = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempt++;

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (existingBytes > 0)
                        request.Headers.Range = new RangeHeaderValue(existingBytes, null);

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(DownloadTimeout);

                    using var response = await _httpClient
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                        .ConfigureAwait(false);

                    if (existingBytes > 0)
                    {
                        if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            var contentRange = response.Content.Headers.ContentRange;
                            if (contentRange?.From != existingBytes)
                            {
                                // The server resumed at a different offset. Restart cleanly.
                                TryDeleteFile(partPath);
                                existingBytes = 0;
                                continue;
                            }
                        }
                        else if (response.StatusCode == HttpStatusCode.OK)
                        {
                            // Server ignored the Range header; overwrite from the start.
                            existingBytes = 0;
                        }
                        else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                        {
                            TryDeleteFile(partPath);
                            existingBytes = 0;
                            continue;
                        }
                        else
                        {
                            response.EnsureSuccessStatusCode();
                        }
                    }
                    else
                    {
                        response.EnsureSuccessStatusCode();
                    }

                    long? contentLength = response.Content.Headers.ContentLength;
                    long totalBytes = expectedSize > 0
                        ? expectedSize
                        : contentLength.HasValue ? existingBytes + contentLength.Value : 0;

                    await using var network = await response.Content
                        .ReadAsStreamAsync(timeoutCts.Token)
                        .ConfigureAwait(false);

                    await using var file = new FileStream(
                        partPath,
                        existingBytes > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        Hashing.BufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    byte[] buffer = new byte[Hashing.BufferSize];
                    long received = existingBytes;
                    int read;

                    while ((read = await network
                               .ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token)
                               .ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token)
                            .ConfigureAwait(false);
                        received += read;

                        if (totalBytes > 0)
                        {
                            progress?.Report(new UpdateProgress
                            {
                                Phase = UpdatePhase.DownloadingArchive,
                                Fraction = Math.Clamp((double)received / totalBytes, 0, 1),
                                BytesReceived = received,
                                TotalBytes = totalBytes,
                                Message = $"Downloading update ({FormatBytes(received)} of {FormatBytes(totalBytes)})"
                            });
                        }
                        else
                        {
                            progress?.Report(new UpdateProgress
                            {
                                Phase = UpdatePhase.DownloadingArchive,
                                Fraction = -1,
                                BytesReceived = received,
                                Message = $"Downloading update ({FormatBytes(received)})"
                            });
                        }
                    }

                    await file.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

                    if (expectedSize > 0 && received != expectedSize)
                    {
                        throw new IOException(
                            $"Downloaded {received} bytes but expected {expectedSize}.");
                    }

                    if (!string.IsNullOrWhiteSpace(expectedSha256))
                    {
                        progress?.Report(new UpdateProgress
                        {
                            Phase = UpdatePhase.Verifying,
                            Fraction = -1,
                            Message = "Verifying the downloaded update..."
                        });

                        string actualHash = await Hashing.ComputeSha256Async(partPath, cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            TryDeleteFile(partPath);
                            throw new UpdateIntegrityException(
                                "The downloaded update failed SHA-256 verification.");
                        }
                    }

                    File.Move(partPath, destinationPath, overwrite: true);
                    progress?.Report(new UpdateProgress
                    {
                        Phase = UpdatePhase.DownloadingArchive,
                        Fraction = 1,
                        BytesReceived = expectedSize > 0 ? expectedSize : 0,
                        TotalBytes = expectedSize > 0 ? expectedSize : null,
                        Message = "Download complete."
                    });
                    return;
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw;

                    if (attempt >= 3)
                        throw new TimeoutException("Timed out while downloading the update.");
                }
                catch (Exception ex) when (attempt < 3 && IsTransient(ex))
                {
                    AppLogger.Warn($"Download attempt {attempt} failed, retrying: {ex.Message}");
                    existingBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };

            var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("Apolion-UpdateChecker/2.0");
            return client;
        }

        private static async Task<string> GetStringWithRetryAsync(
            string url,
            long maxBytes,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            bool acceptGitHubJson)
        {
            Exception? lastException = null;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (acceptGitHubJson)
                    {
                        request.Headers.Accept.ParseAdd("application/vnd.github+json");
                        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                    }

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(timeout);

                    using var response = await _httpClient
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                        .ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();

                    await using var stream = await response.Content
                        .ReadAsStreamAsync(timeoutCts.Token)
                        .ConfigureAwait(false);

                    using var memory = new MemoryStream();
                    byte[] buffer = new byte[16 * 1024];
                    long total = 0;
                    int read;

                    while ((read = await stream
                               .ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token)
                               .ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > maxBytes)
                            throw new InvalidDataException("Update server response is too large.");

                        memory.Write(buffer, 0, read);
                    }

                    return Encoding.UTF8.GetString(memory.ToArray());
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    lastException = new TimeoutException("Update request timed out.");
                }
                catch (Exception ex) when (attempt < 3 && IsTransient(ex))
                {
                    lastException = ex;
                }

                if (attempt < 3)
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }

            throw lastException ?? new HttpRequestException("Update request failed.");
        }

        /// <summary>
        /// Deletes the transient CEF cache directories and the EULA acceptance
        /// marker. Language, settings and saved tabs are intentionally preserved.
        /// </summary>
        public static void PrepareAppDataForUpdate()
        {
            try
            {
                string appDataDir = SettingsManager.GetAppDataFolder();
                AppLogger.Info($"Preparing AppData for update: {appDataDir}");

                CleanupCefCache();
            }
            catch (Exception ex)
            {
                AppLogger.Exception("PrepareAppDataForUpdate failed", ex);
            }
        }

        private static void CleanupCefCache()
        {
            string cefCachePath = GetCefCachePath();
            if (!Directory.Exists(cefCachePath))
                return;

            AppLogger.Info($"Cleaning CefCache at {cefCachePath}");

            foreach (string relativeDirectory in CefCacheRelativeDirectories)
            {
                try
                {
                    string directory = Path.Combine(cefCachePath, relativeDirectory);
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                        AppLogger.Debug($"Deleted cache dir: {directory}");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Failed to delete cache dir {relativeDirectory}: {ex.Message}");
                }
            }

            try
            {
                foreach (string pattern in new[] { "*.log", "*.tmp" })
                {
                    foreach (string file in Directory.GetFiles(cefCachePath, pattern))
                        TryDeleteFile(file);
                }
            }
            catch
            {
                // Cache cleanup is best effort; the dedicated updater retries after exit.
            }
        }

        private static string GetCefCachePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Apolion",
                "CefCache");
        }

        private static string? FindAppFolder(string rootDirectory)
        {
            var exeFiles = Directory.GetFiles(rootDirectory, "Apolion.exe", SearchOption.AllDirectories);
            if (exeFiles.Length == 0)
                return null;

            return exeFiles
                .OrderBy(static path => path.Length)
                .Select(Path.GetDirectoryName)
                .FirstOrDefault(static path => !string.IsNullOrEmpty(path));
        }

        private static string GetInstallDirectory()
        {
            string? currentExe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExe))
                throw new InvalidOperationException("Could not determine the current executable path.");

            string? currentDir = Path.GetDirectoryName(currentExe);
            if (string.IsNullOrEmpty(currentDir))
                throw new InvalidOperationException("Could not determine the install directory.");

            return currentDir;
        }

        private static void WriteManifestToFolder(string folder, UpdateManifest manifest)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(folder, ManifestFileName),
                    manifest.ToJson(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Could not write {ManifestFileName} to staging: {ex.Message}");
            }
        }

        /// <summary>
        /// Produces a list of files that belonged to the previously installed
        /// release but are absent from the new manifest. The dedicated updater only
        /// deletes these known release files; arbitrary user files are never
        /// touched.
        /// </summary>
        private static string? WriteRemoveList(
            UpdateManifest newManifest,
            string removeListPath)
        {
            try
            {
                string installDirectory = GetInstallDirectory();
                string oldManifestPath = Path.Combine(installDirectory, ManifestFileName);
                if (!File.Exists(oldManifestPath))
                    return null;

                UpdateManifest? oldManifest = UpdateManifest.TryParse(
                    File.ReadAllText(oldManifestPath));
                if (oldManifest == null)
                    return null;

                var newPaths = new HashSet<string>(
                    newManifest.Files.Select(static file => file.Path),
                    StringComparer.OrdinalIgnoreCase);

                var obsolete = oldManifest.Files
                    .Where(file => !newPaths.Contains(file.Path))
                    .Select(static file => file.Path)
                    .Where(static path => PathSafety.TryNormalizeRelativePath(path, out _))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (obsolete.Count == 0)
                    return null;

                Directory.CreateDirectory(Path.GetDirectoryName(removeListPath)!);
                File.WriteAllLines(
                    removeListPath,
                    obsolete,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                AppLogger.Info($"Prepared remove list with {obsolete.Count} obsolete file(s)");
                return removeListPath;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Could not prepare obsolete-file list: {ex.Message}");
                return null;
            }
        }

        private static bool LaunchUpdater(string sourceAppFolder, string? removeListPath)
        {
            try
            {
                string installDirectory = GetInstallDirectory();
                string currentExe = Path.Combine(installDirectory, "Apolion.exe");

                // Check for dedicated updater in staging folder first (full zip path)
                string updaterExe = Path.Combine(sourceAppFolder, "Apolion Updater.exe");
                if (!File.Exists(updaterExe))
                {
                    // Then check install directory (delta path)
                    updaterExe = Path.Combine(installDirectory, "Apolion Updater.exe");
                }

                if (!File.Exists(updaterExe))
                {
                    LastError = "Dedicated updater (Apolion Updater.exe) not found. Please reinstall Apolion.";
                    AppLogger.Error(LastError);
                    return false;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = updaterExe,
                    Arguments = $"\"{installDirectory}\" \"{sourceAppFolder}\" \"{removeListPath ?? string.Empty}\"",
                    UseShellExecute = true
                });

                AppLogger.Info("Dedicated updater launched, shutting down current instance");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AppLogger.Exception("LaunchUpdater failed", ex);
                return false;
            }
        }

        private static bool ShouldAllowPrerelease()
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("APOLION_ALLOW_PRERELEASE"),
                    "1",
                    StringComparison.Ordinal))
            {
                return true;
            }

            // A prerelease build should continue to see prereleases.
            return _currentVersion.Contains('-') || _currentVersion.Contains('+');
        }

        private static bool IsTransient(Exception ex)
        {
            return ex is HttpRequestException or IOException or TimeoutException;
        }

        private static void ResetLatestState()
        {
            LatestVersion = null;
            LatestDownloadUrl = null;
            LatestDownloadName = null;
            LatestDownloadSize = 0;
            LatestDownloadSha256 = null;
            LatestManifestUrl = null;
            LatestReleaseNotes = null;
            LatestAssets = null;
            LastError = null;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0)
                return "?";
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:0.0} KB";
            if (bytes < 1024L * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024):0.0} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Could not delete '{path}': {ex.Message}");
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort.
            }
        }
    }
}