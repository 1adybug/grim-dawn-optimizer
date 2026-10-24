using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using GrimDawnOptimizer.Core.Domain;
using K4os.Compression.LZ4;

namespace GrimDawnOptimizer.Core.Services;

/// <summary>
/// Reads animation durations without loading game DLLs. Archive precedence is
/// base game and expansions gdx1, gdx2, then gdx3, matching database layering.
/// </summary>
public sealed class AnimationSpeedResolver
{
    private static readonly Regex AttackField = new(@"^(.+?)AttackAnim(\d*)$", RegexOptions.CultureInvariant);
    private readonly Dictionary<string, ArcIndex> archives = new(StringComparer.OrdinalIgnoreCase);

    public AnimationSpeedProfile Resolve(GameInstallation installation, GameRecord animationTable,
        CancellationToken cancellationToken = default)
    {
        var groups = new Dictionary<string, List<AnimationTimingEntry>>(StringComparer.OrdinalIgnoreCase);
        var incompleteGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();
        foreach (var field in animationTable.TextValues.OrderBy(field => field.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = AttackField.Match(field.Key);
            var resourcePath = field.Value.FirstOrDefault();
            if (!match.Success || string.IsNullOrWhiteSpace(resourcePath)) continue;
            var prefix = match.Groups[1].Value;
            var speedField = prefix + "AttackAnimSpeed" + match.Groups[2].Value;
            var speed = animationTable.NumericSeries.TryGetValue(speedField, out var series) && series.Count > 0
                ? series[0]
                : animationTable.NumericValues.Values.TryGetValue(speedField, out var value) ? value : 1;
            try
            {
                if (!(speed > 0) || !double.IsFinite(speed)) throw new InvalidDataException("Invalid animation speed.");
                var (archive, header) = ReadAnimationHeader(installation.RootPath, resourcePath);
                var timing = ParseHeader(header);
                // The game uses single-precision division before averaging entries.
                var seconds = (float)timing.Frames / timing.FramesPerSecond / (float)speed;
                if (!groups.TryGetValue(prefix, out var entries)) groups[prefix] = entries = [];
                entries.Add(new(resourcePath, archive, timing.Version, timing.Frames, timing.FramesPerSecond, speed, seconds));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                incompleteGroups.Add(prefix);
                unresolved.Add($"{field.Key}: {resourcePath} ({exception.Message})");
            }
        }

        var resolved = new Dictionary<string, AnimationTimingGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var (prefix, entries) in groups)
        {
            if (incompleteGroups.Contains(prefix) || entries.Count == 0) continue;
            // AnimationSet's Attack getter ignores selection weights: it averages
            // every successfully loaded, nonempty animation, even a zero-weight one.
            float total = 0;
            foreach (var entry in entries) total += (float)entry.AdjustedSeconds;
            resolved[prefix] = new(prefix, total / entries.Count, entries.ToArray());
        }
        return new() { TableRecordName = animationTable.RecordName, AttackGroups = resolved, UnresolvedResources = unresolved };
    }

    public IReadOnlyDictionary<string, AnimationSpeedProfile> ResolveAll(GameInstallation installation,
        IEnumerable<GameRecord> records, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, AnimationSpeedProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (!record.TextValues.Keys.Any(key => AttackField.IsMatch(key))) continue;
            result[record.RecordName] = Resolve(installation, record, cancellationToken);
        }
        return result;
    }

    public static (int Version, uint Frames, int FramesPerSecond) ParseHeader(ReadOnlySpan<byte> header)
    {
        var offset = 0;
        var version = 1;
        if (header.Length >= 3 && header[..3].SequenceEqual("ANM"u8))
        {
            if (header.Length < 4 || header[3] != 2) throw new InvalidDataException("Unsupported ANM version.");
            offset = 4;
            version = 2;
        }
        if (header.Length < offset + 12) throw new InvalidDataException("Truncated ANM header.");
        var frames = BinaryPrimitives.ReadUInt32LittleEndian(header[(offset + 4)..]);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(header[(offset + 8)..]);
        if (frames == 0 || rate <= 0) throw new InvalidDataException("ANM has no usable animation duration.");
        return (version, frames, rate);
    }

    private (string Archive, byte[] Header) ReadAnimationHeader(string root, string resourcePath)
    {
        var normalized = resourcePath.Replace('\\', '/').TrimStart('/');
        var separator = normalized.IndexOf('/');
        if (separator <= 0 || normalized.Split('/').Any(part => part is ".." or "."))
            throw new InvalidDataException("Invalid animation resource path.");
        var archiveName = normalized[..separator] + ".arc";
        var entryName = normalized[(separator + 1)..];
        foreach (var folder in new[] { Path.Combine(root, "gdx3", "resources"), Path.Combine(root, "gdx2", "resources"), Path.Combine(root, "gdx1", "resources"), Path.Combine(root, "resources") })
        {
            var path = Path.Combine(folder, archiveName);
            if (!File.Exists(path)) continue;
            if (!archives.TryGetValue(path, out var archive)) archives[path] = archive = new ArcIndex(path);
            if (archive.TryReadHeader(entryName, out var header)) return (path, header);
        }
        throw new FileNotFoundException("Animation resource was not found in installation archives.");
    }

    private sealed class ArcIndex
    {
        private readonly string path;
        private readonly ArcPart[] parts;
        private readonly Dictionary<string, ArcEntry> entries = new(StringComparer.OrdinalIgnoreCase);

        public ArcIndex(string path)
        {
            this.path = path;
            using var stream = Open();
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            var magic = reader.ReadInt32();
            var version = reader.ReadInt32();
            var fileCount = reader.ReadInt32();
            var partCount = reader.ReadInt32();
            var partTableSize = reader.ReadInt32();
            var stringTableSize = reader.ReadInt32();
            var partTableOffset = reader.ReadInt32();
            if (magic != 0x435241 || version != 3 || fileCount < 0 || partCount < 0 ||
                partTableOffset < 28 || partTableSize < 0 || stringTableSize < 0 ||
                (long)partCount * 12 > partTableSize ||
                (long)partTableOffset + partTableSize + stringTableSize + (long)fileCount * 44 > stream.Length)
                throw new InvalidDataException("Invalid ARC tables.");
            stream.Position = partTableOffset;
            parts = new ArcPart[partCount];
            for (var index = 0; index < parts.Length; index++)
                parts[index] = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            stream.Position = (long)partTableOffset + partTableSize;
            var strings = reader.ReadBytes(stringTableSize);
            for (var index = 0; index < fileCount; index++)
            {
                _ = reader.ReadInt32();
                var offset = reader.ReadInt32();
                var compressed = reader.ReadInt32();
                var decompressed = reader.ReadInt32();
                _ = reader.ReadInt32();
                _ = reader.ReadInt64();
                var count = reader.ReadInt32();
                var first = reader.ReadInt32();
                var nameLength = reader.ReadInt32();
                var nameOffset = reader.ReadInt32();
                if (nameOffset < 0 || nameLength < 0 || (long)nameOffset + nameLength > strings.Length)
                    throw new InvalidDataException("Invalid ARC entry name.");
                var name = Encoding.ASCII.GetString(strings, nameOffset, nameLength).TrimEnd('\0').Replace('\\', '/');
                if (decompressed > 0 && name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                    entries[name] = new(offset, compressed, decompressed, count, first);
            }
        }

        public bool TryReadHeader(string name, out byte[] header)
        {
            header = [];
            if (!entries.TryGetValue(name, out var entry)) return false;
            using var stream = Open();
            if (entry.PartCount == 0)
            {
                if (entry.CompressedLength != entry.DecompressedLength)
                    throw new InvalidDataException("Compressed ARC entry has no parts.");
                stream.Position = entry.Offset;
                header = new byte[Math.Min(16, entry.DecompressedLength)];
                stream.ReadExactly(header);
                return true;
            }
            if (entry.FirstPart < 0 || entry.FirstPart >= parts.Length)
                throw new InvalidDataException("Invalid ARC part index.");
            var part = parts[entry.FirstPart];
            if (part.Offset < 0 || part.CompressedLength < 0 || part.DecompressedLength < 16 ||
                (long)part.Offset + part.CompressedLength > stream.Length || part.DecompressedLength > 64 * 1024 * 1024)
                throw new InvalidDataException("Invalid ARC animation part.");
            stream.Position = part.Offset;
            var compressed = new byte[part.CompressedLength];
            stream.ReadExactly(compressed);
            if (part.CompressedLength == part.DecompressedLength) header = compressed[..16];
            else
            {
                var decoded = new byte[part.DecompressedLength];
                if (LZ4Codec.Decode(compressed, decoded) != decoded.Length)
                    throw new InvalidDataException("Incomplete ARC decompression.");
                header = decoded[..16];
            }
            return true;
        }

        private FileStream Open() => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        private sealed record ArcPart(int Offset, int CompressedLength, int DecompressedLength);
        private sealed record ArcEntry(int Offset, int CompressedLength, int DecompressedLength, int PartCount, int FirstPart);
    }
}
