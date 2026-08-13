using System.Buffers.Binary;
using System.Text;
using GrimDawnOptimizer.Core.Domain;
using K4os.Compression.LZ4;

namespace GrimDawnOptimizer.Core.Parsing;

internal sealed class ArzArchiveReader
{
    private static readonly HashSet<string> NumericFields = BuildNumericFields();

    private static readonly HashSet<string> TextFields = BuildTextFields();

    public IReadOnlyDictionary<string, GameRecord> Read(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var unknown = reader.ReadUInt16();
        var version = reader.ReadUInt16();
        var recordTableStart = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        var recordCount = reader.ReadUInt32();
        var stringTableStart = reader.ReadUInt32();
        var stringTableSize = reader.ReadUInt32();
        if (unknown != 2 || version != 3) throw new InvalidDataException($"不支持的 ARZ 版本：{unknown}/{version}");

        var strings = ReadStringTable(stream, reader, stringTableStart, stringTableSize);
        stream.Position = recordTableStart;
        var entries = new ArzEntry[recordCount];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = new ArzEntry(
                reader.ReadUInt32(), ReadLengthPrefixedAscii(reader), reader.ReadUInt32(),
                reader.ReadUInt32(), reader.ReadUInt32());
            stream.Position += 8;
        }

        var result = new Dictionary<string, GameRecord>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (entry.StringIndex >= strings.Count) continue;
            stream.Position = entry.Offset + 24L;
            var compressed = reader.ReadBytes(checked((int)entry.CompressedSize));
            var output = new byte[entry.UncompressedSize];
            var decoded = LZ4Codec.Decode(compressed, 0, compressed.Length, output, 0, output.Length);
            if (decoded <= 0) continue;
            var recordName = strings[(int)entry.StringIndex];
            result[recordName] = ParseRecord(recordName, entry.Type, output, strings);
        }
        return result;
    }

    private static List<string> ReadStringTable(Stream stream, BinaryReader reader, uint start, uint size)
    {
        stream.Position = start;
        var end = start + size;
        var result = new List<string>();
        while (stream.Position < end)
        {
            var count = reader.ReadUInt32();
            for (var i = 0; i < count; i++) result.Add(ReadLengthPrefixedAscii(reader));
        }
        return result;
    }

    private static string ReadLengthPrefixedAscii(BinaryReader reader)
    {
        var length = reader.ReadUInt32();
        return Encoding.ASCII.GetString(reader.ReadBytes(checked((int)length)));
    }

    private static GameRecord ParseRecord(string name, string type, byte[] data, IReadOnlyList<string> strings)
    {
        var numeric = new StatBlock();
        var series = new Dictionary<string, IReadOnlyList<double>>(StringComparer.OrdinalIgnoreCase);
        var text = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            var valueType = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
            var keyIndex = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            var fieldLength = 8 + count * 4;
            if (fieldLength <= 0 || offset + fieldLength > data.Length || keyIndex >= strings.Count) break;
            var key = strings[(int)keyIndex];
            if (valueType == 2)
            {
                if (TextFields.Contains(key))
                {
                    var values = new List<string>(count);
                    for (var i = 0; i < count; i++)
                    {
                        var index = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8 + i * 4, 4));
                        if (index < strings.Count && !string.IsNullOrWhiteSpace(strings[(int)index])) values.Add(strings[(int)index]);
                    }
                    if (values.Count > 0) text[key] = values;
                }
            }
            else if (NumericFields.Contains(key))
            {
                var values = new double[count];
                for (var i = 0; i < count; i++)
                {
                    var raw = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8 + i * 4, 4));
                    values[i] = valueType == 1 ? BitConverter.Int32BitsToSingle(unchecked((int)raw)) : raw;
                }
                var nonZero = values.Where(x => Math.Abs(x) > 0.000001).ToArray();
                series[key] = values;
                if (nonZero.Length > 0)
                {
                    numeric.Set(key, nonZero.Average());
                    if (values.Length > 1)
                    {
                        numeric.Set(key + ".min", values.Min());
                        numeric.Set(key + ".max", values.Max());
                    }
                }
            }
            offset += fieldLength;
        }
        return new GameRecord { RecordName = name, RecordType = type, NumericValues = numeric, NumericSeries = series, TextValues = text };
    }

    private static HashSet<string> BuildNumericFields()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "augmentAllLevel",
            "characterAttackSpeed",
            "characterAttackSpeedModifier",
            "characterDefensiveAbility",
            "characterDefensiveAbilityModifier",
            "characterDeflectProjectile",
            "characterDexterity",
            "characterDexterityReq",
            "characterDodgePercent",
            "characterIntelligence",
            "characterIntelligenceReq",
            "characterLife",
            "characterLifeModifier",
            "characterLifeRegen",
            "characterMana",
            "characterManaRegen",
            "characterOffensiveAbility",
            "characterOffensiveAbilityModifier",
            "characterSpellCastSpeed",
            "characterSpellCastSpeedModifier",
            "characterStrength",
            "characterStrengthReq",
            "conversionPercentage",
            "conversionPercentage2",
            "defensiveAbsorption",
            "defensiveAllMaxResist",
            "defensiveBlockAmount",
            "defensiveBlockChance",
            "defensiveProtection",
            "defensiveElementalResistance",
            "defensiveSlowLifeLeach",
            "factionValue",
            "itemSkillLevelEq",
            "levelRequirement",
            "maxLevel",
            "minLevel",
            "offensiveLifeLeechMin",
            "skillChance",
            "skillCooldownTime",
            "triggerCooldown",
            "triggerEventChance",
            "usesTwoHands"
        };

        foreach (var damageType in new[] { "Physical", "Pierce", "Fire", "Cold", "Lightning", "Poison", "Life", "Aether", "Chaos" })
        {
            result.Add($"offensive{damageType}Min");
            result.Add($"offensive{damageType}Max");
            result.Add($"offensive{damageType}Modifier");
            result.Add($"defensive{damageType}");
            result.Add($"defensive{damageType}Resistance");
            result.Add($"defensive{damageType}MaxResist");
        }
        return result;
    }

    private static HashSet<string> BuildTextFields()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Class",
            "artifactFormula",
            "characterAttributeEquations",
            "characterDefensiveAbility",
            "characterLife",
            "characterOffensiveAbility",
            "charLevel",
            "conversionInType",
            "conversionInType2",
            "conversionOutType",
            "conversionOutType2",
            "factionName",
            "itemNameTag",
            "itemSetName",
            "itemSkillName",
            "lootRandomizerName",
            "monsterClassification",
            "relicName",
            "skillAutoController",
            "skillDisplayName",
            "triggerType",
            "setBonus2",
            "setBonus3",
            "setBonus4",
            "setBonus5",
            "setBonus6",
            "setBonus7",
            "setBonus8",
            "setBonus9"
        };
        for (var index = 1; index <= 32; index++)
        {
            result.Add($"skillName{index}");
            result.Add($"skillLevel{index}");
        }
        return result;
    }

    private sealed record ArzEntry(uint StringIndex, string Type, uint Offset, uint CompressedSize, uint UncompressedSize);
}
