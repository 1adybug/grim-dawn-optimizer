using GrimDawnOptimizer.App.ViewModels;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class AttachmentAcquisitionTests
{
    [Fact]
    public void SaveReaderRetainsLooseAndInstalledAttachmentLocations()
    {
        var file = Path.Combine(Path.GetTempPath(), "attachment-source-test.gdc");
        var hostRaw = new RawItem
        {
            BaseRecord = "records/items/gearhands/host.dbr",
            Seed = 17,
            ComponentRecord = "records/items/materia/component.dbr",
            CompletionBonusRecord = "records/items/materia/completion.dbr",
            AugmentRecord = "records/items/enchants/augment.dbr",
            SourceKind = ItemSourceKind.PersonalStash,
            ContainerIndex = 2,
            X = 4,
            Y = 6
        };
        var hostSource = new ItemSourceLocation(ItemSourceKind.PersonalStash, file, 2, 4, 6);
        var host = new ItemInstance
        {
            Id = ItemInstance.CreateStableId(hostSource, hostRaw.BaseRecord, hostRaw.Seed),
            Name = "宿主手套",
            Slot = EquipmentSlot.Hands,
            BaseRecord = hostRaw.BaseRecord,
            Seed = hostRaw.Seed,
            Source = hostSource
        };
        var looseRaw = new RawItem
        {
            BaseRecord = "records/items/materia/component.dbr",
            SourceKind = ItemSourceKind.Backpack,
            ContainerIndex = 1,
            X = 2,
            Y = 3
        };
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [hostRaw.BaseRecord] = Record(hostRaw.BaseRecord, "ArmorProtective_Hands"),
                [looseRaw.BaseRecord] = Record(looseRaw.BaseRecord, "ItemRelic")
            }
        };

        var owned = SaveReader.BuildOwnedAttachments(
            [(hostRaw, file), (looseRaw, file)],
            [host],
            catalog);

        var loose = Assert.Single(owned, x => x.Kind == AttachmentKind.Component &&
                                               x.Source?.Kind == AttachmentAcquisitionKind.Loose);
        Assert.Equal(ItemSourceKind.Backpack, loose.Source!.Location!.Kind);
        Assert.Equal((1, 2, 3), (loose.Source.Location.ContainerIndex, loose.Source.Location.X, loose.Source.Location.Y));
        var installed = Assert.Single(owned, x => x.Kind == AttachmentKind.Component &&
                                                   x.Source?.Kind == AttachmentAcquisitionKind.Installed);
        Assert.Equal("宿主手套", installed.Source!.HostItemName);
        Assert.Equal(host.Id, installed.Source.HostItemId);
        Assert.Equal(hostSource, installed.Source.Location);
        Assert.Contains(owned, x => x.Kind == AttachmentKind.Augment &&
                                    x.Source?.Kind == AttachmentAcquisitionKind.Installed);
    }

    [Fact]
    public void StaticSourceIndexResolvesBlueprintAndFactionTier()
    {
        const string component = "records/items/materia/component.dbr";
        const string augment = "records/items/enchants/augment.dbr";
        const string faction = "records/factions/survivors.dbr";
        var records = new Dictionary<string, GameRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["records/items/crafting/blueprints/component.dbr"] = TextRecord(
                "records/items/crafting/blueprints/component.dbr", ("artifactName", component)),
            [faction] = TextRecord(faction, ("myFaction", "Survivors")),
            ["records/creatures/npcs/merchants/survivors.dbr"] = TextRecord(
                "records/creatures/npcs/merchants/survivors.dbr",
                ("factions", faction),
                ("marketFileName", "records/markets/survivors.dbr")),
            ["records/markets/survivors.dbr"] = TextRecord(
                "records/markets/survivors.dbr", ("honoredNormalTable", "records/markets/survivors_honored.dbr")),
            ["records/markets/survivors_honored.dbr"] = TextRecord(
                "records/markets/survivors_honored.dbr", ("marketStaticItems", augment))
        };
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tagFactionSurvivors"] = "恶魔十字",
            ["tagFactionStateHonored"] = "崇敬"
        };

        var sources = GameDataSource.BuildStaticAttachmentSources(records, tags);

        var formula = Assert.Single(sources[component]);
        Assert.Equal(AttachmentAcquisitionKind.Blacksmith, formula.Kind);
        Assert.Equal("records/items/crafting/blueprints/component.dbr", formula.FormulaRecord);
        var vendor = Assert.Single(sources[augment]);
        Assert.Equal(AttachmentAcquisitionKind.FactionVendor, vendor.Kind);
        Assert.Equal("恶魔十字", vendor.FactionName);
        Assert.Equal("崇敬", vendor.ReputationName);
        Assert.Equal("faction:1", vendor.FactionReputationKey);
        Assert.Equal(10_000, vendor.RequiredReputation);
    }

    [Fact]
    public void AvailabilityRequiresAnActionableUnlockedSource()
    {
        const string formula = "records/items/crafting/blueprints/component.dbr";
        var component = Attachment(
            "component", AttachmentKind.Component,
            new AttachmentAcquisitionSource(AttachmentAcquisitionKind.Blacksmith, FormulaRecord: formula));
        var augment = Attachment(
            "augment", AttachmentKind.Augment,
            new AttachmentAcquisitionSource(
                AttachmentAcquisitionKind.FactionVendor,
                FactionName: "恶魔十字",
                FactionReputationKey: "faction:0",
                ReputationName: "崇敬",
                RequiredReputation: 10_000));
        var basicComponent = Attachment(
            "basic-component", AttachmentKind.Component,
            new AttachmentAcquisitionSource(
                AttachmentAcquisitionKind.Blacksmith,
                FormulaRecord: "records/items/crafting/blueprints/component/craft_component_base_test.dbr",
                IsAlwaysAvailable: true));
        var unknown = Attachment("unknown", AttachmentKind.Component);
        var catalog = new GameDataCatalog { Attachments = [component, augment, basicComponent, unknown] };
        var unlocked = new CharacterSnapshot
        {
            Name = "test",
            SaveFilePath = "test.gdc",
            Level = 100,
            KnownFormulas = new HashSet<string> { formula },
            FactionReputation = new Dictionary<string, double> { ["faction:0"] = 10_000 }
        };

        var available = new GameDataSource().GetUnlockedAttachments(unlocked, catalog);

        Assert.Contains(available, x => x.Id == "component");
        Assert.Contains(available, x => x.Id == "augment");
        Assert.Contains(available, x => x.Id == "basic-component");
        Assert.DoesNotContain(available, x => x.Id == "unknown");

        var locked = new CharacterSnapshot { Name = "test", SaveFilePath = "test.gdc", Level = 100 };
        var lockedAvailable = new GameDataSource().GetUnlockedAttachments(locked, catalog);
        var availableBasicComponent = Assert.Single(lockedAvailable);
        Assert.Equal("basic-component", availableBasicComponent.Id);
        Assert.True(BeamSearchOptimizer.IsAttachmentCompatible(
            availableBasicComponent,
            Item("hands", EquipmentSlot.Hands),
            locked,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void CompletedComponentOnlyUsesPhysicalSourceWithMatchingBonus()
    {
        const string componentRecord = "records/items/materia/component.dbr";
        const string completionRecord = "records/items/materia/completion.dbr";
        const string formulaRecord = "records/items/crafting/blueprints/component.dbr";
        var component = Attachment(
            componentRecord,
            AttachmentKind.Component,
            new AttachmentAcquisitionSource(AttachmentAcquisitionKind.Blacksmith, FormulaRecord: formulaRecord));
        var physical = new AttachmentAcquisitionSource(
            AttachmentAcquisitionKind.Loose,
            new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc", 0, 1, 2));
        var character = new CharacterSnapshot
        {
            Name = "test",
            SaveFilePath = "test.gdc",
            Level = 100,
            KnownFormulas = new HashSet<string> { formulaRecord },
            OwnedAttachments = [new OwnedAttachmentInstance(
                componentRecord, AttachmentKind.Component, completionRecord, physical)]
        };
        var catalog = new GameDataCatalog
        {
            Attachments = [component],
            Records = new Dictionary<string, GameRecord>
            {
                [completionRecord] = new GameRecord { RecordName = completionRecord }
            }
        };

        var available = new GameDataSource().GetUnlockedAttachments(character, catalog);

        var baseVariant = Assert.Single(available, x => string.IsNullOrWhiteSpace(x.CompletionBonusRecord));
        Assert.Contains(baseVariant.AcquisitionSources, x => x.Kind == AttachmentAcquisitionKind.Blacksmith);
        var completed = Assert.Single(available, x => x.CompletionBonusRecord == completionRecord);
        Assert.Single(completed.AcquisitionSources);
        Assert.Equal(physical, completed.AcquisitionSources[0]);
    }

    [Fact]
    public void FormatterUsesFirstSourceFromFirstTwoDifferentCategories()
    {
        var attachment = Attachment(
            "component", AttachmentKind.Component,
            new AttachmentAcquisitionSource(
                AttachmentAcquisitionKind.Loose,
                new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc", 0, 5, 5)),
            new AttachmentAcquisitionSource(
                AttachmentAcquisitionKind.Loose,
                new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc", 0, 1, 2)),
            new AttachmentAcquisitionSource(
                AttachmentAcquisitionKind.Loose,
                new ItemSourceLocation(ItemSourceKind.PersonalStash, "test.gdc", 1, 3, 4)),
            new AttachmentAcquisitionSource(
                AttachmentAcquisitionKind.Blacksmith,
                FormulaRecord: "formula"));

        var display = ResultViewModel.FormatAttachment(attachment);

        Assert.Contains("首选：背包第 1 页 · 左上格 (2,3)", display);
        Assert.Contains("备选：个人仓库第 2 页 · 左上格 (4,5)", display);
        Assert.DoesNotContain("铁匠制作", display);
    }

    [Fact]
    public void FormatterUsesSecondLocationWhenOnlyOneSourceCategoryExists()
    {
        var attachment = Attachment(
            "component", AttachmentKind.Component,
            new AttachmentAcquisitionSource(
                AttachmentAcquisitionKind.Loose,
                new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc", 0, 5, 5)),
            new AttachmentAcquisitionSource(
                AttachmentAcquisitionKind.Loose,
                new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc", 0, 1, 2)));

        var display = ResultViewModel.FormatAttachment(attachment);

        Assert.Contains("首选：背包第 1 页 · 左上格 (2,3)", display);
        Assert.Contains("备选：背包第 1 页 · 左上格 (6,6)", display);
    }

    [Fact]
    public void FormatterSimplifiesInstalledAndExplainsFactionVendorSources()
    {
        var installed = Attachment(
            "component", AttachmentKind.Component,
            new AttachmentAcquisitionSource(
                AttachmentAcquisitionKind.Installed,
                new ItemSourceLocation(ItemSourceKind.Equipped, "test.gdc", (int)EquipmentSlot.Head),
                HostItemName: "守望者头盔",
                HostSlot: EquipmentSlot.Head),
            new AttachmentAcquisitionSource(AttachmentAcquisitionKind.Blacksmith, FormulaRecord: "formula"));
        var augment = Attachment(
            "augment", AttachmentKind.Augment,
            new AttachmentAcquisitionSource(
                AttachmentAcquisitionKind.FactionVendor,
                FactionName: "拜死教",
                FactionReputationKey: "faction:1",
                ReputationName: "崇敬",
                RequiredReputation: 10_000));

        Assert.Contains("首选：已镶嵌", ResultViewModel.FormatAttachment(installed));
        Assert.DoesNotContain("守望者头盔", ResultViewModel.FormatAttachment(installed));
        Assert.DoesNotContain("备选：", ResultViewModel.FormatAttachment(installed));
        Assert.DoesNotContain("铁匠制作", ResultViewModel.FormatAttachment(installed));
        Assert.Contains("阵营军需官：拜死教（崇敬）", ResultViewModel.FormatAttachment(augment));
    }

    [Fact]
    public void PlanEquipmentCannotBeUsedAsAnotherItemsAttachmentSource()
    {
        const string donorId = "donor-item";
        var installedSource = new AttachmentAcquisitionSource(
            AttachmentAcquisitionKind.Installed,
            HostItemId: donorId,
            HostItemName: "来源装备");
        var component = Attachment(
            "component", AttachmentKind.Component,
            installedSource,
            new AttachmentAcquisitionSource(AttachmentAcquisitionKind.Blacksmith, FormulaRecord: "formula"));
        var target = Item("target-item", EquipmentSlot.Hands);
        var donor = Item(donorId, EquipmentSlot.Head, componentRecord: "component");
        var plan = new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.Hands] = new(target, component, null),
            [EquipmentSlot.Head] = new(donor, component, null)
        };

        var display = ResultViewModel.FormatAttachment(
            component,
            target,
            plan.Values.Select(x => x.Item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));

        Assert.DoesNotContain("已镶嵌", display);
        Assert.Contains("首选：铁匠制作（配方已学会）", display);
        Assert.True(BeamSearchOptimizer.HasActionableAttachmentSources(plan));

        var installedOnly = Attachment("installed-only", AttachmentKind.Component, installedSource);
        var invalidPlan = new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.Hands] = new(target, installedOnly, null),
            [EquipmentSlot.Head] = new(donor, component, null)
        };
        Assert.False(BeamSearchOptimizer.HasActionableAttachmentSources(invalidPlan));
    }

    [Fact]
    public void AttachmentAlreadyOnRecommendedItemOnlySaysInstalled()
    {
        var component = Attachment(
            "component", AttachmentKind.Component,
            new AttachmentAcquisitionSource(
                AttachmentAcquisitionKind.Installed,
                HostItemId: "target-item",
                HostItemName: "目标装备"));
        var target = Item("target-item", EquipmentSlot.Hands, componentRecord: "component");

        var display = ResultViewModel.FormatAttachment(
            component,
            target,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.Id });

        Assert.Equal($"component{Environment.NewLine}已镶嵌", display);
        Assert.True(BeamSearchOptimizer.HasActionableAttachmentSources(
            new Dictionary<EquipmentSlot, LoadoutVariant>
            {
                [EquipmentSlot.Hands] = new(target, component, null)
            }));
    }

    private static AttachmentDefinition Attachment(
        string id,
        AttachmentKind kind,
        params AttachmentAcquisitionSource[] sources) => new()
    {
        Id = id,
        RecordId = id,
        Name = id,
        Kind = kind,
        LevelRequirement = 1,
        CompatibleSlots = new HashSet<EquipmentSlot> { EquipmentSlot.Hands },
        RequiredFormula = sources.FirstOrDefault(x => x.Kind == AttachmentAcquisitionKind.Blacksmith)?.FormulaRecord ?? string.Empty,
        RequiredFaction = sources.FirstOrDefault(x => x.Kind == AttachmentAcquisitionKind.FactionVendor)?.FactionReputationKey ?? string.Empty,
        RequiredReputation = sources.FirstOrDefault(x => x.Kind == AttachmentAcquisitionKind.FactionVendor)?.RequiredReputation ?? 0,
        AcquisitionSources = sources
    };

    private static ItemInstance Item(
        string id,
        EquipmentSlot slot,
        string componentRecord = "") => new()
    {
        Id = id,
        BaseRecord = $"records/{id}.dbr",
        Name = id,
        Slot = slot,
        ComponentRecord = componentRecord,
        Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc", 0, 0, 0)
    };

    private static GameRecord Record(string name, string @class) => new()
    {
        RecordName = name,
        RecordType = @class,
        TextValues = new Dictionary<string, IReadOnlyList<string>> { ["Class"] = [@class] }
    };

    private static GameRecord TextRecord(string name, params (string Key, string Value)[] fields) => new()
    {
        RecordName = name,
        TextValues = fields
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Select(v => v.Value).ToArray(), StringComparer.OrdinalIgnoreCase)
    };
}
