using System.Text;
using K4os.Compression.LZ4;

namespace GrimDawnOptimizer.Core.Parsing;

internal sealed class ArcArchiveReader
{
    public IReadOnlyDictionary<string, string> ReadTags(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        _ = reader.ReadInt32();
        var version = reader.ReadInt32();
        var fileCount = reader.ReadInt32();
        var partCount = reader.ReadInt32();
        var partTableSize = reader.ReadInt32();
        var stringTableSize = reader.ReadInt32();
        var partTableOffset = reader.ReadInt32();
        if (version != 3) throw new InvalidDataException($"不支持的 ARC 版本：{version}");

        stream.Position = partTableOffset;
        var parts = new ArcPart[partCount];
        for (var i = 0; i < parts.Length; i++) parts[i] = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

        stream.Position = partTableOffset + partTableSize;
        var rawStrings = reader.ReadBytes(stringTableSize);
        var names = new string[fileCount];
        var stringOffset = 0;
        for (var i = 0; i < names.Length && stringOffset < rawStrings.Length; i++)
        {
            var end = Array.IndexOf(rawStrings, (byte)0, stringOffset);
            if (end < 0) end = rawStrings.Length;
            names[i] = Encoding.ASCII.GetString(rawStrings, stringOffset, end - stringOffset);
            stringOffset = end + 1;
        }

        var tocOffset = partTableOffset + partTableSize + stringTableSize;
        stream.Position = tocOffset;
        var entries = new List<ArcEntry>();
        for (var i = 0; i < fileCount; i++)
        {
            var entry = new ArcEntry(
                reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
                reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            if (entry.DecompressedLength > 0) entries.Add(entry);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var count = Math.Min(names.Length, entries.Count);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!names[i].EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
            var content = Encoding.UTF8.GetString(Extract(stream, parts, entries[i])).TrimStart('\uFEFF');
            content = RemoveControlSequences(content).Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (var line in content.Split('\n'))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0 || !line.AsSpan(0, separator).Trim().StartsWith("tag", StringComparison.OrdinalIgnoreCase)) continue;
                result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
        return result;
    }

    private static byte[] Extract(Stream stream, IReadOnlyList<ArcPart> parts, ArcEntry entry)
    {
        var result = new byte[entry.DecompressedLength];
        var destinationOffset = 0;
        for (var i = 0; i < entry.PartCount; i++)
        {
            var part = parts[entry.FirstPartIndex + i];
            stream.Position = part.Offset;
            var compressed = new byte[part.CompressedLength];
            stream.ReadExactly(compressed);
            if (part.CompressedLength == part.DecompressedLength)
                Buffer.BlockCopy(compressed, 0, result, destinationOffset, part.DecompressedLength);
            else
                LZ4Codec.Decode(compressed, 0, compressed.Length, result, destinationOffset, part.DecompressedLength);
            destinationOffset += part.DecompressedLength;
        }
        return result;
    }

    private static string RemoveControlSequences(string input)
    {
        var builder = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '^' && i + 1 < input.Length) { i++; continue; }
            builder.Append(input[i]);
        }
        return builder.ToString();
    }

    private sealed record ArcPart(int Offset, int CompressedLength, int DecompressedLength);
    private sealed record ArcEntry(int Type, int Offset, int CompressedLength, int DecompressedLength, int Unknown, long FileTime, int PartCount, int FirstPartIndex, int StringLength, int StringOffset);
}
