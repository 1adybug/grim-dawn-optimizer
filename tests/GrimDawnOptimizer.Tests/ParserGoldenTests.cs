using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;
using GrimDawnOptimizer.Core.Services;
using K4os.Compression.LZ4;
using Microsoft.Data.Sqlite;

namespace GrimDawnOptimizer.Tests;

public sealed class ParserGoldenTests
{
    [Fact]
    public void CharacterFixtureParsesAndLeavesBytesUnchanged()
    {
        using var fixture = new TemporaryFixture();
        var path = fixture.Write("player.gdc", BuildCharacter());
        var before = SHA256.HashData(File.ReadAllBytes(path));

        var character = new CharacterFileParser().Parse(path);

        Assert.Equal("黄金角色", character.Name);
        Assert.Equal(100, character.Level);
        Assert.True(character.IsHardcore);
        Assert.Equal(1050, character.Physique);
        var skill = Assert.Single(character.Skills);
        Assert.Equal("records/skills/playerclass/test.dbr", skill.Record);
        Assert.Equal(12, skill.Level);
        Assert.True(skill.Enabled);
        Assert.True(skill.Locked);
        Assert.Equal("records/skills/playerclass/test.dbr", character.PrimarySkillRecord);
        Assert.Equal("records/skills/playerclass/rightclick.dbr", character.SecondarySkillRecord);
        var backpackItems = character.Items.Where(x => x.SourceKind == ItemSourceKind.Backpack).ToArray();
        Assert.Collection(backpackItems,
            item =>
            {
                Assert.Equal("records/items/gearhands/test.dbr", item.BaseRecord);
                Assert.Equal((2, 3), (item.X, item.Y));
            },
            item =>
            {
                Assert.Equal("records/items/gearrings/test.dbr", item.BaseRecord);
                Assert.Equal((6, 13), (item.X, item.Y));
            });
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void CharacterVersionElevenReadsItemsAndSkillsWithoutFieldMisalignment()
    {
        using var fixture = new TemporaryFixture();
        var path = fixture.Write("player.gdc", BuildCharacter(inventoryVersion: 11));

        var character = new CharacterFileParser().Parse(path);

        Assert.Equal(100, character.Level);
        var item = Assert.Single(character.Items, x => x.BaseRecord == "records/items/gearhands/test.dbr");
        Assert.Equal(37, item.Unknown);
        Assert.Equal(0x50607080u, item.AugmentSeed);
        Assert.Equal("records/items/ascendant/prefix.dbr", item.AscendantPrefixRecord);
        Assert.Equal("records/items/ascendant/suffix.dbr", item.AscendantSuffixRecord);
        Assert.Contains(character.Skills, x => x.Record == "records/skills/playerclass/test.dbr" && x.Level == 12);
        Assert.Equal("records/skills/playerclass/test.dbr", character.PrimarySkillRecord);
    }

    [Fact]
    public void TransferVersionNineReadsAsterkarnFieldsAndItemLocation()
    {
        using var fixture = new TemporaryFixture();
        var path = fixture.Write("transfer.gsh", BuildTransferV9());
        var before = SHA256.HashData(File.ReadAllBytes(path));

        var items = new TransferStashParser().Parse(path);

        var item = Assert.Single(items);
        Assert.Equal("records/items/gearhead/test.dbr", item.BaseRecord);
        Assert.Equal(0xDEADBEEFu, item.Seed);
        Assert.Equal(0x50607080u, item.AugmentSeed);
        Assert.Equal("records/items/ascendant/prefix.dbr", item.AscendantPrefixRecord);
        Assert.Equal("records/items/ascendant/suffix.dbr", item.AscendantSuffixRecord);
        Assert.Equal(2, item.StackCount);
        Assert.Equal(ItemSourceKind.SharedStash, item.SourceKind);
        Assert.Equal(0, item.ContainerIndex);
        Assert.Equal(4, item.X);
        Assert.Equal(7, item.Y);
        var source = new ItemSourceLocation(item.SourceKind, path, item.ContainerIndex, item.X, item.Y);
        Assert.Equal("共享仓库第 1 页 · 左上格 (5,8)", source.DisplayName);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void TransferVersionElevenReadsSecondAsterkarnItemField()
    {
        using var fixture = new TemporaryFixture();
        var path = fixture.Write("transfer.gst", BuildTransferV11());

        var item = Assert.Single(new TransferStashParser().Parse(path));

        Assert.Equal("records/items/ascendant/prefix.dbr", item.AscendantPrefixRecord);
        Assert.Equal("records/items/ascendant/suffix.dbr", item.AscendantSuffixRecord);
        Assert.Equal(17, item.AsterkarnUnknown);
        Assert.Equal(23, item.AsterkarnUnknown2);
    }

    [Fact]
    public void ArcFixtureReadsUtf8TagsAndRemovesControlCodes()
    {
        using var fixture = new TemporaryFixture();
        var path = fixture.Write("Text_CH.arc", BuildArc("tagItemName=^o余烬之刃\r\ntagSkillName=烈焰打击\r\n"));

        var tags = new ArcArchiveReader().ReadTags(path);

        Assert.Equal("余烬之刃", tags["tagItemName"]);
        Assert.Equal("烈焰打击", tags["tagSkillName"]);
    }

    [Fact]
    public async Task InstallationPrefersTextZhChineseTagsOverEnglishFallback()
    {
        using var fixture = new TemporaryFixture();
        var resources = Path.Combine(fixture.Root, "resources");
        Directory.CreateDirectory(resources);
        File.WriteAllBytes(Path.Combine(resources, "Text_EN.arc"), BuildArc("tagItemName=Ember Blade\r\n"));
        File.WriteAllBytes(Path.Combine(resources, "Text_ZH.arc"), BuildArc("tagItemName=余烬之刃\r\n"));
        var installation = GameDataSource.CreateInstallation(fixture.Root);

        var catalog = await new GameDataSource(Path.Combine(fixture.Root, "cache")).LoadCatalogAsync(installation);

        Assert.Equal(["Text_EN.arc", "Text_ZH.arc"],
            installation.DataFiles.Select(x => Path.GetFileName(x)!).ToArray());
        Assert.Equal("余烬之刃", catalog.Tags["tagItemName"]);
    }

    [Fact]
    public void FormulaVersionThreeReadsKnownBlueprintsAndLeavesBytesUnchanged()
    {
        using var fixture = new TemporaryFixture();
        var path = fixture.Write("formulas.gst", BuildFormulaV3());
        var before = SHA256.HashData(File.ReadAllBytes(path));

        var formulas = new FormulaFileParser().Parse(path);

        Assert.Contains("records/items/crafting/blueprints/armor/craft_feet_test.dbr", formulas);
        Assert.Contains("records/items/crafting/blueprints/relic/craft_relic_test.dbr", formulas);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void LooseComponentsAndAugmentsEnterOwnedAttachmentPool()
    {
        var component = new RawItem
        {
            BaseRecord = "records/items/components/loose_component.dbr",
            CompletionBonusRecord = "records/items/components/completion.dbr"
        };
        var augment = new RawItem { BaseRecord = "records/items/augments/loose_augment.dbr" };
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [component.BaseRecord] = new GameRecord
                {
                    RecordName = component.BaseRecord,
                    RecordType = "ItemRelic"
                },
                [augment.BaseRecord] = new GameRecord
                {
                    RecordName = augment.BaseRecord,
                    RecordType = "ItemEnchantment"
                }
            }
        };

        var owned = SaveReader.BuildOwnedAttachments([component, augment], catalog);

        Assert.Contains(owned, x => x.RecordId == component.BaseRecord &&
                                    x.Kind == AttachmentKind.Component &&
                                    x.CompletionBonusRecord == component.CompletionBonusRecord);
        Assert.Contains(owned, x => x.RecordId == augment.BaseRecord && x.Kind == AttachmentKind.Augment);
    }

    [Fact]
    public void AllocatedSkillAndDevotionLevelsAreLockedIntoBaseline()
    {
        const string skillRecord = "records/skills/devotion/test.dbr";
        var raw = new RawCharacter("test.gdc", "test", "class", 100, false)
        {
            Health = 100, Energy = 50, Physique = 10, Cunning = 10, Spirit = 10
        };
        raw.Skills.Add(new RawSkill(
            skillRecord, 1, Enabled: false, Locked: false, DevotionLevel: 3,
            Experience: 0, SubLevel: 0, Active: false, Transition: false,
            AutoCastSkill: string.Empty, AutoCastController: string.Empty));
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>
            {
                [skillRecord] = new GameRecord
                {
                    RecordName = skillRecord,
                    RecordType = "Skill_Passive",
                    NumericSeries = new Dictionary<string, IReadOnlyList<double>>
                    {
                        ["characterLife"] = [10, 20, 30]
                    }
                }
            }
        };

        var baseline = SaveReader.BuildBaseline(raw, catalog);

        Assert.Equal(130, baseline["health"]);
    }

    [Fact]
    public void BaselineAddsPersistentBuffChildButSkipsUnallocatedDevotionEntry()
    {
        const string aura = "records/skills/aura.dbr";
        const string buff = "records/skills/aura_buff.dbr";
        const string unallocated = "records/skills/devotion/unallocated.dbr";
        var raw = new RawCharacter("test.gdc", "test", "class", 38, false)
        {
            Health = 100, Energy = 50, Physique = 100, Cunning = 100, Spirit = 100
        };
        raw.Skills.Add(new RawSkill(aura, 2, true, false, 0, 0, 0, false, false, string.Empty, string.Empty));
        raw.Skills.Add(new RawSkill(unallocated, 0, false, false, 1, 0, 0, false, false, string.Empty, string.Empty));
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>
            {
                [aura] = new GameRecord
                {
                    RecordName = aura, RecordType = "Skill_BuffSelf",
                    TextValues = new Dictionary<string, IReadOnlyList<string>> { ["buffSkillName"] = [buff] }
                },
                [buff] = new GameRecord
                {
                    RecordName = buff, RecordType = "SkillBuff_Passive",
                    NumericSeries = new Dictionary<string, IReadOnlyList<double>>
                    {
                        ["characterOffensiveAbility"] = [50, 100]
                    }
                },
                [unallocated] = new GameRecord
                {
                    RecordName = unallocated, RecordType = "Skill_Passive",
                    NumericSeries = new Dictionary<string, IReadOnlyList<double>>
                    {
                        ["characterOffensiveAbility"] = [36]
                    }
                }
            }
        };

        var baseline = SaveReader.BuildBaseline(raw, catalog);

        Assert.Equal(165, baseline["offensiveAbility"]);
    }

    [Fact]
    public async Task ArzFixtureAndSqliteCacheInvalidateByContentHash()
    {
        using var fixture = new TemporaryFixture();
        var arzPath = fixture.Write("database.arz", BuildArz(123));
        var cacheRoot = Path.Combine(fixture.Root, "cache");
        var source = new GameDataSource(cacheRoot);
        var install = new GameInstallation(fixture.Root, "test", "1.3-test", [arzPath]);

        var first = await source.LoadCatalogAsync(install);
        var cached = await source.LoadCatalogAsync(install);
        Assert.Equal(123, first.Records["records/items/test.dbr"].NumericValues["characterLife"]);
        Assert.Equal(123, cached.Records["records/items/test.dbr"].NumericValues["characterLife"]);
        Assert.Equal(512, first.Records["records/items/test.dbr"].NumericValues["strengthRequirement"]);
        Assert.Equal(410, first.Records["records/items/test.dbr"].NumericValues["dexterityRequirement"]);
        Assert.Equal(320, first.Records["records/items/test.dbr"].NumericValues["intelligenceRequirement"]);
        Assert.Equal("records/game/itemcostformulas_heavy.dbr", first.Records["records/items/test.dbr"].Text("itemCostName"));
        Assert.Equal("itemLevel * 10 + 84", first.Records["records/items/test.dbr"].Text("handsStrengthEquation"));
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(cacheRoot, "catalog.db")};Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT typeof(payload), length(payload) FROM catalog_cache";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("blob", reader.GetString(0));
            Assert.True(reader.GetInt64(1) > 5);
        }

        await File.WriteAllBytesAsync(arzPath, BuildArz(456));
        var refreshed = await source.LoadCatalogAsync(install);

        Assert.Equal(456, refreshed.Records["records/items/test.dbr"].NumericValues["characterLife"]);
    }

    [Fact]
    public async Task BrokenCacheNeverBlocksGameDataImport()
    {
        using var fixture = new TemporaryFixture();
        var arzPath = fixture.Write("database.arz", BuildArz(789));
        var cacheRoot = Path.Combine(fixture.Root, "cache");
        Directory.CreateDirectory(cacheRoot);
        await File.WriteAllTextAsync(Path.Combine(cacheRoot, "catalog.db"), "not a sqlite database");
        var source = new GameDataSource(cacheRoot);
        var install = new GameInstallation(fixture.Root, "test", "1.3-test", [arzPath]);

        var catalog = await source.LoadCatalogAsync(install);

        Assert.Equal(789, catalog.Records["records/items/test.dbr"].NumericValues["characterLife"]);
    }

    [Fact]
    public async Task LegacyTextCacheIsIgnoredAndRebuilt()
    {
        using var fixture = new TemporaryFixture();
        var arzPath = fixture.Write("database.arz", BuildArz(321));
        var cacheRoot = Path.Combine(fixture.Root, "cache");
        var source = new GameDataSource(cacheRoot);
        var install = new GameInstallation(fixture.Root, "test", "1.3-test", [arzPath]);
        await source.LoadCatalogAsync(install);

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(cacheRoot, "catalog.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE catalog_cache SET payload='{}'";
            await command.ExecuteNonQueryAsync();
        }

        var rebuilt = await source.LoadCatalogAsync(install);

        Assert.Equal(321, rebuilt.Records["records/items/test.dbr"].NumericValues["characterLife"]);
        await using var verifyConnection = new SqliteConnection($"Data Source={Path.Combine(cacheRoot, "catalog.db")};Mode=ReadOnly;Pooling=False");
        await verifyConnection.OpenAsync();
        var verify = verifyConnection.CreateCommand();
        verify.CommandText = "SELECT typeof(payload) FROM catalog_cache";
        Assert.Equal("blob", await verify.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SaveReaderUsesGameEquipmentRequirementFields()
    {
        using var fixture = new TemporaryFixture();
        var playerPath = fixture.Write("player.gdc", BuildCharacter());
        const string itemRecord = "records/items/gearhands/test.dbr";
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [itemRecord] = new GameRecord
                {
                    RecordName = itemRecord,
                    RecordType = "ArmorProtective_Hands",
                    NumericValues = new StatBlock(new Dictionary<string, double>
                    {
                        ["strengthRequirement"] = 512,
                        ["dexterityRequirement"] = 410,
                        ["intelligenceRequirement"] = 320
                    })
                }
            }
        };
        var location = new SaveLocation(fixture.Root, "test", [playerPath], null, null);

        var character = Assert.Single(await new SaveReader().ReadCharactersAsync(location, catalog));
        var item = Assert.Single(character.Items);

        Assert.Equal(512, item.PhysiqueRequirement);
        Assert.Equal(410, item.CunningRequirement);
        Assert.Equal(320, item.SpiritRequirement);
    }

    [Fact]
    public async Task SaveReaderEvaluatesRequirementFormulaWhenStoredValueIsZero()
    {
        using var fixture = new TemporaryFixture();
        var playerPath = fixture.Write("player.gdc", BuildCharacter());
        const string itemRecord = "records/items/gearhands/test.dbr";
        const string formulaRecord = "records/game/itemcostformulas_heavy.dbr";
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [itemRecord] = new GameRecord
                {
                    RecordName = itemRecord,
                    RecordType = "ArmorProtective_Hands",
                    NumericValues = new StatBlock(new Dictionary<string, double> { ["itemLevel"] = 40 }),
                    TextValues = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["itemCostName"] = [formulaRecord]
                    }
                },
                [formulaRecord] = new GameRecord
                {
                    RecordName = formulaRecord,
                    TextValues = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["handsStrengthEquation"] = ["itemLevel * 10 + 84"]
                    }
                }
            }
        };
        var location = new SaveLocation(fixture.Root, "test", [playerPath], null, null);

        var character = Assert.Single(await new SaveReader().ReadCharactersAsync(location, catalog));
        var item = Assert.Single(character.Items);

        Assert.Equal(484, item.PhysiqueRequirement);
        Assert.Equal(0, item.CunningRequirement);
        Assert.Equal(0, item.SpiritRequirement);
    }

    private static byte[] BuildCharacter(int inventoryVersion = 4)
    {
        var writer = new GdFixtureWriter(0x49D12A77);
        writer.WriteUInt32(0x58434447);
        writer.WriteUInt32(2);
        writer.WriteWideString("黄金角色");
        writer.WriteByte(0);
        writer.WriteString("tagClass01");
        writer.WriteUInt32(100);
        writer.WriteBoolean(true);
        writer.WriteByte(1);
        writer.WriteUInt32(0, updateKey: false);
        writer.WriteUInt32(8);
        writer.WriteEncryptedBytes(new byte[16]);

        var bio = writer.BeginBlock(2);
        writer.WriteUInt32(8);
        writer.WriteUInt32(100);
        writer.WriteUInt32(1_000_000);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteSingle(1050);
        writer.WriteSingle(720);
        writer.WriteSingle(890);
        writer.WriteSingle(10_000);
        writer.WriteSingle(2_500);
        writer.EndBlock(bio);

        var inventory = writer.BeginBlock(3);
        writer.WriteUInt32(checked((uint)inventoryVersion));
        writer.WriteBoolean(true);
        writer.WriteUInt32(1);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        var sack = writer.BeginBlock(0);
        writer.WriteByte(0);
        writer.WriteUInt32(2);
        WriteCharacterItem(
            writer,
            "records/items/gearhands/test.dbr",
            inventoryVersion,
            augmentLevel: 37,
            augmentSeed: 0x50607080,
            ascendantPrefix: "records/items/ascendant/prefix.dbr",
            ascendantSuffix: "records/items/ascendant/suffix.dbr");
        writer.WriteUInt32(2);
        writer.WriteUInt32(3);
        WriteCharacterItem(writer, "records/items/gearrings/test.dbr", inventoryVersion);
        writer.WriteUInt32(6);
        writer.WriteUInt32(13);
        writer.EndBlock(sack);
        writer.WriteBoolean(false);
        for (var i = 0; i < 12; i++)
        {
            WriteCharacterItem(writer, string.Empty, inventoryVersion);
            writer.WriteByte(0);
        }
        for (var weaponSet = 0; weaponSet < 2; weaponSet++)
        {
            writer.WriteBoolean(false);
            for (var i = 0; i < 2; i++)
            {
                WriteCharacterItem(writer, string.Empty, inventoryVersion);
                writer.WriteBoolean(false);
            }
        }
        writer.EndBlock(inventory);

        var stash = writer.BeginBlock(4);
        writer.WriteUInt32(5);
        writer.WriteUInt32(10);
        writer.WriteUInt32(20);
        writer.WriteUInt32(0);
        writer.EndBlock(stash);

        var skills = writer.BeginBlock(8);
        writer.WriteUInt32(8);
        writer.WriteUInt32(1);
        writer.WriteString("records/skills/playerclass/test.dbr");
        writer.WriteUInt32(12);
        writer.WriteBoolean(true);
        writer.WriteBoolean(true);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(2);
        writer.WriteBoolean(false);
        writer.WriteByte(0);
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteUInt32(2);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.EndBlock(skills);

        var factions = writer.BeginBlock(13);
        writer.WriteUInt32(1);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.EndBlock(factions);

        var bindings = writer.BeginBlock(14);
        writer.WriteUInt32(8);
        writer.WriteBoolean(false);
        writer.WriteUInt32(0);
        writer.WriteBoolean(true);
        for (var i = 0; i < 5; i++)
        {
            writer.WriteString(i == 0 ? "records/skills/playerclass/test.dbr" : string.Empty);
            writer.WriteString(i == 0 ? "records/skills/playerclass/rightclick.dbr" : string.Empty);
            writer.WriteBoolean(i == 0);
        }
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        for (var i = 0; i < 46; i++) writer.WriteUInt32(1);
        writer.WriteSingle(12);
        writer.WriteUInt32(0);
        writer.EndBlock(bindings);
        return writer.ToArray();
    }

    private static void WriteCharacterItem(
        GdFixtureWriter writer,
        string baseRecord,
        int containerVersion = 4,
        uint augmentLevel = 0,
        uint augmentSeed = 0,
        string ascendantPrefix = "",
        string ascendantSuffix = "")
    {
        writer.WriteString(baseRecord);
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteUInt32(0);
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteUInt32(0);
        writer.WriteString(string.Empty);
        writer.WriteUInt32(augmentLevel);
        writer.WriteUInt32(augmentSeed);
        if (containerVersion >= 8)
        {
            writer.WriteString(ascendantPrefix);
            writer.WriteString(ascendantSuffix);
        }
        writer.WriteUInt32(0);
        writer.WriteUInt32(1);
        if (containerVersion >= 8) writer.WriteUInt32(0);
        if (containerVersion >= 11) writer.WriteUInt32(0);
    }

    private static byte[] BuildTransferV9()
    {
        var writer = new GdFixtureWriter(0x4F81B119);
        writer.WriteUInt32(2);
        var outer = writer.BeginBlock(18);
        writer.WriteUInt32(9);
        writer.WriteUInt32(0, updateKey: false);
        writer.WriteString("expert");
        writer.WriteBoolean(true);
        writer.WriteUInt32(1);

        var tab = writer.BeginBlock(0);
        writer.WriteUInt32(10);
        writer.WriteUInt32(18);
        writer.WriteUInt32(1);
        writer.WriteString("records/items/gearhead/test.dbr");
        writer.WriteString("records/items/prefix/test.dbr");
        writer.WriteString("records/items/suffix/test.dbr");
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteUInt32(0xDEADBEEF);
        writer.WriteString("records/items/components/test.dbr");
        writer.WriteString("records/items/completion/test.dbr");
        writer.WriteUInt32(0x10203040);
        writer.WriteString("records/items/augments/test.dbr");
        writer.WriteUInt32(0);
        writer.WriteUInt32(0x50607080);
        writer.WriteString("records/items/ascendant/prefix.dbr");
        writer.WriteString("records/items/ascendant/suffix.dbr");
        writer.WriteUInt32(0);
        writer.WriteUInt32(2);
        writer.WriteUInt32(0);
        writer.WriteSingle(4);
        writer.WriteSingle(7);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteWideString("Asterkarn");
        writer.EndBlock(tab);
        writer.EndBlock(outer);
        return writer.ToArray();
    }

    private static byte[] BuildTransferV11()
    {
        var writer = new GdFixtureWriter(0x4F81B119);
        writer.WriteUInt32(2);
        var outer = writer.BeginBlock(18);
        writer.WriteUInt32(11);
        writer.WriteUInt32(0, updateKey: false);
        writer.WriteString("normal");
        writer.WriteBoolean(true);
        writer.WriteUInt32(1);

        var tab = writer.BeginBlock(0);
        writer.WriteUInt32(10);
        writer.WriteUInt32(19);
        writer.WriteUInt32(1);
        writer.WriteString("records/items/gearhead/test.dbr");
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteUInt32(0x01020304);
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteUInt32(0);
        writer.WriteString(string.Empty);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteString("records/items/ascendant/prefix.dbr");
        writer.WriteString("records/items/ascendant/suffix.dbr");
        writer.WriteUInt32(0);
        writer.WriteUInt32(1);
        writer.WriteUInt32(17);
        writer.WriteUInt32(23);
        writer.WriteUInt32(2);
        writer.WriteUInt32(3);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteWideString(string.Empty);
        writer.EndBlock(tab);
        writer.EndBlock(outer);
        return writer.ToArray();
    }

    private static byte[] BuildFormulaV3()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
        WriteFormulaString(writer, "begin_block");
        writer.Write(0xB01DFACEu);
        WriteFormulaString(writer, "formulasVersion");
        writer.Write(3u);
        WriteFormulaString(writer, "numEntries");
        writer.Write(2u);
        WriteFormulaString(writer, "expansionStatus");
        writer.Write((byte)7);
        foreach (var record in new[]
        {
            "records/items/crafting/blueprints/armor/craft_feet_test.dbr",
            "records/items/crafting/blueprints/relic/craft_relic_test.dbr"
        })
        {
            WriteFormulaString(writer, "itemName");
            WriteFormulaString(writer, record);
            WriteFormulaString(writer, "formulaRead");
            writer.Write(0u);
        }
        WriteFormulaString(writer, "end_block");
        return stream.ToArray();
    }

    private static void WriteFormulaString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static byte[] BuildArc(string text)
    {
        var content = Encoding.UTF8.GetBytes(text);
        var names = Encoding.ASCII.GetBytes("tags.txt\0");
        const int headerSize = 28;
        const int partTableSize = 12;
        const int tocSize = 44;
        var dataOffset = headerSize + partTableSize + names.Length + tocSize;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0);
        writer.Write(3);
        writer.Write(1);
        writer.Write(1);
        writer.Write(partTableSize);
        writer.Write(names.Length);
        writer.Write(headerSize);
        writer.Write(dataOffset);
        writer.Write(content.Length);
        writer.Write(content.Length);
        writer.Write(names);
        writer.Write(0);
        writer.Write(0);
        writer.Write(content.Length);
        writer.Write(content.Length);
        writer.Write(0);
        writer.Write(0L);
        writer.Write(1);
        writer.Write(0);
        writer.Write(names.Length - 1);
        writer.Write(0);
        writer.Write(content);
        return stream.ToArray();
    }

    private static byte[] BuildArz(uint value)
    {
        var strings = new[]
        {
            "records/items/test.dbr", "characterLife", "Class", "WeaponArmor_Offhand",
            "strengthRequirement", "dexterityRequirement", "intelligenceRequirement",
            "itemCostName", "records/game/itemcostformulas_heavy.dbr",
            "handsStrengthEquation", "itemLevel * 10 + 84"
        };
        using var stringStream = new MemoryStream();
        using (var stringsWriter = new BinaryWriter(stringStream, Encoding.ASCII, leaveOpen: true))
        {
            stringsWriter.Write((uint)strings.Length);
            foreach (var item in strings)
            {
                var bytes = Encoding.ASCII.GetBytes(item);
                stringsWriter.Write((uint)bytes.Length);
                stringsWriter.Write(bytes);
            }
        }

        var record = new byte[84];
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8, 4), value);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20, 4), 3);
        WriteNumericField(record, 24, 4, 512);
        WriteNumericField(record, 36, 5, 410);
        WriteNumericField(record, 48, 6, 320);
        WriteTextField(record, 60, 7, 8);
        WriteTextField(record, 72, 9, 10);
        var compressed = new byte[LZ4Codec.MaximumOutputSize(record.Length)];
        var compressedLength = LZ4Codec.Encode(record, 0, record.Length, compressed, 0, compressed.Length);

        const uint recordTableStart = 24;
        const uint recordTableSize = 32;
        var stringTable = stringStream.ToArray();
        var stringTableStart = recordTableStart + recordTableSize;
        var dataStart = stringTableStart + (uint)stringTable.Length;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((ushort)2);
        writer.Write((ushort)3);
        writer.Write(recordTableStart);
        writer.Write(0u);
        writer.Write(1u);
        writer.Write(stringTableStart);
        writer.Write((uint)stringTable.Length);
        writer.Write(0u);
        writer.Write(4u);
        writer.Write(Encoding.ASCII.GetBytes("test"));
        writer.Write(dataStart - 24);
        writer.Write((uint)compressedLength);
        writer.Write((uint)record.Length);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(stringTable);
        writer.Write(compressed, 0, compressedLength);
        return stream.ToArray();
    }

    private static void WriteNumericField(byte[] record, int offset, uint keyIndex, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 2, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4, 4), keyIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 8, 4), value);
    }

    private static void WriteTextField(byte[] record, int offset, uint keyIndex, uint valueIndex)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 2, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4, 4), keyIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 8, 4), valueIndex);
    }

    private sealed class TemporaryFixture : IDisposable
    {
        public TemporaryFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "gd-optimizer-golden", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Write(string name, byte[] bytes)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class GdFixtureWriter
    {
        private const uint XorKey = 1431655765u;
        private const uint Prime = 39916801u;
        private readonly List<byte> _data = [];
        private readonly uint[] _table = new uint[256];
        private uint _key;

        public GdFixtureWriter(uint key)
        {
            _key = key;
            WriteRawUInt32(key ^ XorKey);
            var seed = key;
            for (var i = 0; i < _table.Length; i++)
            {
                seed = (seed >> 1) | (seed << 31);
                seed *= Prime;
                _table[i] = seed;
            }
        }

        public void WriteUInt32(uint value, bool updateKey = true)
        {
            var raw = value ^ _key;
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, raw);
            foreach (var item in bytes) _data.Add(item);
            if (updateKey)
                foreach (var item in bytes) _key ^= _table[item];
        }

        public void WriteSingle(float value) => WriteUInt32(unchecked((uint)BitConverter.SingleToInt32Bits(value)));
        public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteByte(byte value)
        {
            var raw = (byte)(value ^ _key);
            _data.Add(raw);
            _key ^= _table[raw];
        }

        public void WriteString(string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            WriteUInt32((uint)bytes.Length);
            WriteEncryptedBytes(bytes);
        }

        public void WriteWideString(string value)
        {
            WriteUInt32((uint)value.Length);
            WriteEncryptedBytes(Encoding.Unicode.GetBytes(value));
        }

        public void WriteEncryptedBytes(IEnumerable<byte> bytes)
        {
            foreach (var item in bytes) WriteByte(item);
        }

        public Block BeginBlock(uint id)
        {
            WriteUInt32(id);
            var lengthPosition = _data.Count;
            var keyAtLength = _key;
            _data.AddRange([0, 0, 0, 0]);
            return new Block(lengthPosition, _data.Count, keyAtLength);
        }

        public void EndBlock(Block block)
        {
            var length = checked((uint)(_data.Count - block.ContentStart));
            var raw = length ^ block.KeyAtLength;
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, raw);
            for (var i = 0; i < bytes.Length; i++) _data[block.LengthPosition + i] = bytes[i];
            WriteUInt32(0, updateKey: false);
        }

        public byte[] ToArray() => _data.ToArray();
        private void WriteRawUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            foreach (var item in bytes) _data.Add(item);
        }

        public readonly record struct Block(int LengthPosition, int ContentStart, uint KeyAtLength);
    }
}
