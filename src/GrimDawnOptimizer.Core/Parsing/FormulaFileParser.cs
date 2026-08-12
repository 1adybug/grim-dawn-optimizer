using System.Text;

namespace GrimDawnOptimizer.Core.Parsing;

internal sealed class FormulaFileParser
{
    public IReadOnlySet<string> Parse(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: false);

        Expect(reader, "begin_block");
        _ = reader.ReadUInt32();
        Expect(reader, "formulasVersion");
        var version = reader.ReadUInt32();
        if (version is < 1 or > 3) throw new InvalidDataException($"不支持的配方文件版本：{version}");

        Expect(reader, "numEntries");
        var count = reader.ReadUInt32();
        if (count > 100_000) throw new InvalidDataException($"配方数量异常：{count}");
        if (version >= 3)
        {
            Expect(reader, "expansionStatus");
            _ = reader.ReadByte();
        }

        var formulas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0u; i < count; i++)
        {
            Expect(reader, "itemName");
            var record = ReadString(reader);
            Expect(reader, "formulaRead");
            _ = reader.ReadUInt32();
            if (!string.IsNullOrWhiteSpace(record)) formulas.Add(record);
        }

        Expect(reader, "end_block");
        return formulas;
    }

    private static void Expect(BinaryReader reader, string expected)
    {
        var actual = ReadString(reader);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"配方文件字段异常：应为 {expected}，实际为 {actual}");
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt32();
        if (length > 64 * 1024 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException($"配方文件字符串长度异常：{length}");
        var bytes = reader.ReadBytes(checked((int)length));
        if (bytes.Length != length) throw new EndOfStreamException("配方文件意外结束");
        return Encoding.Latin1.GetString(bytes);
    }
}
