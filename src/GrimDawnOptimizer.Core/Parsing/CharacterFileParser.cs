using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Parsing;

internal sealed class CharacterFileParser
{
    private static readonly EquipmentSlot[] EquipmentOrder =
    [
        EquipmentSlot.Head, EquipmentSlot.Amulet, EquipmentSlot.Chest, EquipmentSlot.Legs,
        EquipmentSlot.Feet, EquipmentSlot.Hands, EquipmentSlot.RingLeft, EquipmentSlot.RingRight,
        EquipmentSlot.Waist, EquipmentSlot.Shoulders, EquipmentSlot.Medal, EquipmentSlot.Relic
    ];

    public RawCharacter Parse(string filePath)
    {
        var reader = new GdCryptoReader(File.ReadAllBytes(filePath));
        reader.ReadKey();
        var magic = reader.ReadInt32();
        var headerVersion = reader.ReadInt32();
        if (magic != 0x58434447 || headerVersion is not (1 or 2))
            throw new InvalidDataException($"不是受支持的 player.gdc 文件（标识 0x{magic:X8}，头版本 {headerVersion}）");

        var name = reader.ReadWideString();
        var isMale = reader.ReadBoolean();
        var classId = reader.ReadString();
        var headerLevel = reader.ReadInt32();
        var hardcore = reader.ReadBoolean();
        if (headerVersion >= 2) _ = reader.ReadByte(); // expansion character flag (1.3.x)
        if (reader.ReadInt32(updateKey: false) != 0) throw new InvalidDataException("角色头校验失败");
        var dataVersion = reader.ReadInt32();
        if (dataVersion is < 6 or > 8) throw new InvalidDataException($"不支持的角色版本：{dataVersion}");
        _ = reader.ReadUid();

        var result = new RawCharacter(filePath, name, classId, headerLevel, hardcore) { IsFemale = !isMale };
        while (reader.Remaining > 0)
        {
            var block = reader.BeginBlock();
            switch (block.Id)
            {
                case 2: ReadBio(reader, result); break;
                case 3: ReadInventory(reader, result); break;
                case 4: ReadPersonalStash(reader, result); break;
                case 8: ReadSkills(reader, result); break;
                case 13: ReadFactions(reader, result); break;
                case 14: ReadSkillBindings(reader, result); break;
            }
            reader.EndBlock(block);
        }
        return result;
    }

    private static void ReadBio(GdCryptoReader reader, RawCharacter result)
    {
        var version = reader.ReadInt32();
        if (version < 8) throw new InvalidDataException($"不支持的角色 Bio 版本：{version}");
        result.Level = reader.ReadInt32();
        result.Experience = reader.ReadInt32();
        result.ModifierPoints = reader.ReadInt32();
        result.SkillPoints = reader.ReadInt32();
        result.DevotionPoints = reader.ReadInt32();
        result.TotalDevotion = reader.ReadInt32();
        result.Physique = reader.ReadSingle();
        result.Cunning = reader.ReadSingle();
        result.Spirit = reader.ReadSingle();
        result.Health = reader.ReadSingle();
        result.Energy = reader.ReadSingle();
    }

    private static void ReadInventory(GdCryptoReader reader, RawCharacter result)
    {
        var version = reader.ReadInt32();
        if (version < 4) throw new InvalidDataException($"不支持的背包版本：{version}");
        if (!reader.ReadBoolean()) return;
        var sackCount = reader.ReadInt32();
        _ = reader.ReadInt32();
        _ = reader.ReadInt32();
        for (var sack = 0; sack < sackCount; sack++)
        {
            var block = reader.BeginBlock(0);
            _ = reader.ReadByte();
            var itemCount = reader.ReadInt32();
            for (var i = 0; i < itemCount; i++)
            {
                var item = ReadItem(reader, version);
                item.ContainerIndex = sack;
                // Character backpack positions are grid indices, unlike the
                // floating-point offsets used by stash containers.
                item.X = reader.ReadInt32();
                item.Y = reader.ReadInt32();
                item.SourceKind = ItemSourceKind.Backpack;
                result.Items.Add(item);
            }
            reader.EndBlock(block);
        }

        var useAltWeaponSet = reader.ReadBoolean();
        for (var index = 0; index < EquipmentOrder.Length; index++)
        {
            var item = ReadItem(reader, version);
            _ = reader.ReadByte();
            item.SourceKind = ItemSourceKind.Equipped;
            item.EquippedSlot = EquipmentOrder[index];
            if (!string.IsNullOrWhiteSpace(item.BaseRecord)) result.Items.Add(item);
        }
        ReadWeaponSet(reader, result, version, setIndex: 0, isActive: !useAltWeaponSet);
        ReadWeaponSet(reader, result, version, setIndex: 1, isActive: useAltWeaponSet);
    }

    private static void ReadWeaponSet(
        GdCryptoReader reader,
        RawCharacter result,
        int version,
        int setIndex,
        bool isActive)
    {
        _ = reader.ReadBoolean();
        for (var index = 0; index < 2; index++)
        {
            RawItem item;
            try
            {
                item = ReadItem(reader, version);
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or OverflowException)
            {
                throw new InvalidDataException(
                    $"武器套装读取失败（背包版本 {version}，套装 {setIndex + 1}，槽位 {index + 1}，偏移 {reader.Position}）",
                    ex);
            }
            _ = reader.ReadBoolean();
            item.SourceKind = isActive ? ItemSourceKind.Equipped : ItemSourceKind.AlternateWeaponSet;
            item.ContainerIndex = setIndex;
            item.EquippedSlot = index == 0 ? EquipmentSlot.MainHand : EquipmentSlot.OffHand;
            if (!string.IsNullOrWhiteSpace(item.BaseRecord)) result.Items.Add(item);
        }
    }

    private static void ReadPersonalStash(GdCryptoReader reader, RawCharacter result)
    {
        var version = reader.ReadInt32();
        if (version < 5) throw new InvalidDataException($"不支持的个人仓库版本：{version}");
        if (version == 5)
        {
            ReadPersonalStashTab(reader, result, version, 0);
            return;
        }

        var stashCount = reader.ReadInt32();
        if (stashCount is < 0 or > 100) throw new InvalidDataException($"个人仓库页数异常：{stashCount}");
        for (var tab = 0; tab < stashCount; tab++)
        {
            var block = reader.BeginBlock(0);
            ReadPersonalStashTab(reader, result, version, tab);
            reader.EndBlock(block);
        }
    }

    private static void ReadPersonalStashTab(GdCryptoReader reader, RawCharacter result, int version, int tab)
    {
        _ = reader.ReadInt32();
        _ = reader.ReadInt32();
        var itemCount = reader.ReadInt32();
        if (itemCount is < 0 or > 100_000) throw new InvalidDataException($"个人仓库物品数异常：{itemCount}");
        for (var i = 0; i < itemCount; i++)
        {
            var item = ReadItem(reader, version);
            item.X = ReadCoordinate(reader);
            item.Y = ReadCoordinate(reader);
            item.ContainerIndex = tab;
            item.SourceKind = ItemSourceKind.PersonalStash;
            result.Items.Add(item);
        }
    }

    private static void ReadSkills(GdCryptoReader reader, RawCharacter result)
    {
        var version = reader.ReadInt32();
        if (version is not (5 or 6 or 8)) throw new InvalidDataException($"不支持的技能版本：{version}");
        var skillCount = reader.ReadInt32();
        if (skillCount is < 0 or > 100_000) throw new InvalidDataException($"技能数量异常：{skillCount}");
        for (var i = 0; i < skillCount; i++)
        {
            var record = reader.ReadString();
            var level = reader.ReadInt32();
            var enabled = reader.ReadBoolean();
            var locked = version >= 8 && reader.ReadBoolean();
            result.Skills.Add(new RawSkill(
                record, level, enabled, locked, reader.ReadInt32(),
                reader.ReadInt32(), reader.ReadInt32(), reader.ReadBoolean(), reader.ReadBoolean(),
                reader.ReadString(), reader.ReadString()));
        }

        result.MasteriesAllowed = reader.ReadInt32();
        result.SkillPointsReclaimed = reader.ReadInt32();
        result.DevotionPointsReclaimed = reader.ReadInt32();
        SkipItemSkills(reader);
        if (version >= 6) SkipSubSkills(reader);
    }

    private static void SkipItemSkills(GdCryptoReader reader)
    {
        var count = reader.ReadInt32();
        if (count is < 0 or > 100_000) throw new InvalidDataException($"装备技能数量异常：{count}");
        for (var i = 0; i < count; i++)
        {
            _ = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadInt32();
            _ = reader.ReadString();
        }
    }

    private static void SkipSubSkills(GdCryptoReader reader)
    {
        var count = reader.ReadInt32();
        if (count is < 0 or > 100_000) throw new InvalidDataException($"子技能数量异常：{count}");
        for (var i = 0; i < count; i++)
        {
            _ = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadString();
        }
    }

    private static void ReadFactions(GdCryptoReader reader, RawCharacter result)
    {
        var version = reader.ReadInt32();
        if (version < 1) throw new InvalidDataException($"不支持的阵营版本：{version}");
        result.PlayerFaction = reader.ReadInt32();
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var changed = reader.ReadBoolean();
            var unlocked = reader.ReadBoolean();
            var value = reader.ReadSingle();
            var positiveBoost = reader.ReadSingle();
            var negativeBoost = reader.ReadSingle();
            result.Factions.Add(new RawFaction(i, changed, unlocked, value, positiveBoost, negativeBoost));
        }
    }

    private static void ReadSkillBindings(GdCryptoReader reader, RawCharacter result)
    {
        var version = reader.ReadInt32();
        if (version < 4) throw new InvalidDataException($"不支持的技能栏版本：{version}");
        result.EquipmentSelection = reader.ReadBoolean();
        var selectedSkillSet = reader.ReadInt32();
        result.SkillWindowSelection = selectedSkillSet;
        var skillSettingValid = reader.ReadBoolean();
        var sets = new List<RawSkillBinding>(5);
        for (var i = 0; i < 5; i++)
            sets.Add(new RawSkillBinding(reader.ReadString(), reader.ReadString(), reader.ReadBoolean()));
        result.SkillBindings.AddRange(sets);

        if (version >= 7)
        {
            _ = reader.ReadInt32();
            _ = reader.ReadInt32();
            _ = reader.ReadInt32();
        }

        var hotSlotCount = version == 4 ? 36 : 46;
        for (var i = 0; i < hotSlotCount; i++)
        {
            var type = reader.ReadInt32();
            if (type == 0)
            {
                var skillName = reader.ReadString();
                if (!string.IsNullOrWhiteSpace(skillName)) result.HotbarSkillRecords.Add(skillName);
                var isItemSkill = reader.ReadBoolean();
                var itemName = reader.ReadString();
                var equipLocation = reader.ReadInt32();
                result.HotbarSlots.Add(new RawHotSlot(i, type, skillName, isItemSkill, itemName, equipLocation));
            }
            else if (type == 4)
            {
                var itemName = reader.ReadString();
                _ = reader.ReadString();
                _ = reader.ReadString();
                _ = reader.ReadWideString();
                result.HotbarSlots.Add(new RawHotSlot(i, type, string.Empty, false, itemName, 0));
            }
            else result.HotbarSlots.Add(new RawHotSlot(i, type, string.Empty, false, string.Empty, 0));
        }

        _ = reader.ReadSingle();
        if (version >= 6) _ = reader.ReadInt32();

        var selected = sets.FirstOrDefault(x => x.Active && !string.IsNullOrWhiteSpace(x.PrimarySkill));
        if (string.IsNullOrWhiteSpace(selected?.PrimarySkill) && skillSettingValid && selectedSkillSet >= 0 && selectedSkillSet < sets.Count)
            selected = sets[selectedSkillSet];
        selected ??= sets.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.PrimarySkill));
        result.PrimarySkillRecord = selected?.PrimarySkill ?? string.Empty;
        result.SecondarySkillRecord = selected?.SecondarySkill ?? string.Empty;
        if (string.IsNullOrWhiteSpace(result.PrimarySkillRecord))
            result.PrimarySkillRecord = result.HotbarSlots.FirstOrDefault(x => x.Index == 10)?.SkillName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(result.SecondarySkillRecord))
            result.SecondarySkillRecord = result.HotbarSlots.FirstOrDefault(x => x.Index == 11)?.SkillName ?? string.Empty;
    }

    internal static RawItem ReadItem(GdCryptoReader reader, int containerVersion)
    {
        var item = new RawItem
        {
            BaseRecord = reader.ReadString(),
            PrefixRecord = reader.ReadString(),
            SuffixRecord = reader.ReadString(),
            ModifierRecord = reader.ReadString(),
            TransmuteRecord = reader.ReadString(),
            Seed = unchecked((uint)reader.ReadInt32()),
            ComponentRecord = reader.ReadString(),
            CompletionBonusRecord = reader.ReadString(),
            ComponentSeed = unchecked((uint)reader.ReadInt32()),
            AugmentRecord = reader.ReadString()
        };

        // Since item version 8, the ascendant affixes are stored after the
        // augment level and seed, not immediately after the augment record.
        item.Unknown = reader.ReadInt32();
        item.AugmentSeed = unchecked((uint)reader.ReadInt32());
        if (containerVersion >= 8)
        {
            item.AscendantPrefixRecord = reader.ReadString();
            item.AscendantSuffixRecord = reader.ReadString();
        }
        item.Unknown2 = reader.ReadInt32();
        item.StackCount = reader.ReadInt32();
        if (containerVersion >= 8) item.AsterkarnUnknown = reader.ReadInt32();
        if (containerVersion >= 11) item.AsterkarnUnknown2 = reader.ReadInt32();
        return item;
    }

    internal static int ReadCoordinate(GdCryptoReader reader)
    {
        var value = reader.ReadSingle();
        if (!float.IsFinite(value) || value is < -1_000_000 or > 1_000_000)
            throw new InvalidDataException($"物品坐标异常：{value}");
        return checked((int)MathF.Round(value));
    }
}

internal sealed class RawCharacter(string filePath, string name, string classId, int level, bool isHardcore)
{
    public string FilePath { get; } = filePath;
    public string Name { get; } = name;
    public string ClassId { get; } = classId;
    public int Level { get; set; } = level;
    public bool IsHardcore { get; } = isHardcore;
    public bool IsFemale { get; init; }
    public int Experience { get; set; }
    public int ModifierPoints { get; set; }
    public int SkillPoints { get; set; }
    public int DevotionPoints { get; set; }
    public int TotalDevotion { get; set; }
    public float Physique { get; set; }
    public float Cunning { get; set; }
    public float Spirit { get; set; }
    public float Health { get; set; }
    public float Energy { get; set; }
    public int PlayerFaction { get; set; }
    public int MasteriesAllowed { get; set; }
    public int SkillPointsReclaimed { get; set; }
    public int DevotionPointsReclaimed { get; set; }
    public string PrimarySkillRecord { get; set; } = string.Empty;
    public string SecondarySkillRecord { get; set; } = string.Empty;
    public bool EquipmentSelection { get; set; }
    public int SkillWindowSelection { get; set; }
    public List<RawSkillBinding> SkillBindings { get; } = [];
    public List<string> HotbarSkillRecords { get; } = [];
    public List<RawHotSlot> HotbarSlots { get; } = [];
    public List<RawItem> Items { get; } = [];
    public List<RawSkill> Skills { get; } = [];
    public List<RawFaction> Factions { get; } = [];
}

internal sealed class RawItem
{
    public string BaseRecord { get; set; } = string.Empty;
    public string PrefixRecord { get; set; } = string.Empty;
    public string SuffixRecord { get; set; } = string.Empty;
    public string ModifierRecord { get; set; } = string.Empty;
    public string TransmuteRecord { get; set; } = string.Empty;
    public uint Seed { get; set; }
    public string ComponentRecord { get; set; } = string.Empty;
    public string CompletionBonusRecord { get; set; } = string.Empty;
    public uint ComponentSeed { get; set; }
    public string AugmentRecord { get; set; } = string.Empty;
    public int Unknown { get; set; }
    public uint AugmentSeed { get; set; }
    public int Unknown2 { get; set; }
    public int StackCount { get; set; }
    public string AscendantPrefixRecord { get; set; } = string.Empty;
    public string AscendantSuffixRecord { get; set; } = string.Empty;
    public int AsterkarnUnknown { get; set; }
    public int AsterkarnUnknown2 { get; set; }
    public ItemSourceKind SourceKind { get; set; }
    public int ContainerIndex { get; set; }
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public EquipmentSlot? EquippedSlot { get; set; }
}

internal sealed record RawSkill(
    string Record, int Level, bool Enabled, bool Locked, int DevotionLevel, int Experience, int SubLevel,
    bool Active, bool Transition, string AutoCastSkill, string AutoCastController);
internal sealed record RawFaction(int Index, bool Changed, bool Unlocked, float Value, float PositiveBoost, float NegativeBoost);
internal sealed record RawSkillBinding(string PrimarySkill, string SecondarySkill, bool Active);
internal sealed record RawHotSlot(int Index, int Type, string SkillName, bool IsItemSkill, string ItemName, int EquipLocation);
