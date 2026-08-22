using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Parsing;

internal sealed class TransferStashParser
{
    public IReadOnlyList<RawItem> Parse(string filePath)
    {
        var reader = new GdCryptoReader(File.ReadAllBytes(filePath));
        reader.ReadKey();
        if (reader.ReadUInt32() != 2) throw new InvalidDataException("不是受支持的共享仓库文件");
        var outer = reader.BeginBlock(18);
        var version = reader.ReadUInt32();
        if (version is not (4 or 5 or 8 or 9 or 11)) throw new InvalidDataException($"不支持的共享仓库版本：{version}");
        if (reader.ReadUInt32(updateKey: false) != 0) throw new InvalidDataException("共享仓库头校验失败");
        _ = reader.ReadString();
        if (version >= 5) _ = reader.ReadBoolean();
        var tabCount = reader.ReadUInt32();
        if (tabCount > 100) throw new InvalidDataException("共享仓库页数异常");

        var result = new List<RawItem>();
        for (var tab = 0; tab < tabCount; tab++)
        {
            var tabBlock = reader.BeginBlock(0);
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            var itemCount = reader.ReadUInt32();
            for (var i = 0; i < itemCount; i++)
            {
                var item = ReadItem(reader, version);
                item.SourceKind = ItemSourceKind.SharedStash;
                item.ContainerIndex = tab;
                item.X = CharacterFileParser.ReadCoordinate(reader);
                item.Y = CharacterFileParser.ReadCoordinate(reader);
                result.Add(item);
            }
            if (version >= 9)
            {
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadWideString();
            }
            reader.EndBlock(tabBlock);
        }
        reader.EndBlock(outer);
        return result;
    }

    private static RawItem ReadItem(GdCryptoReader reader, uint version)
    {
        return CharacterFileParser.ReadItem(reader, checked((int)version));
    }
}
