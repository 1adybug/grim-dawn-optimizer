using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

public static class DemoDataFactory
{
    public static (CharacterSnapshot Character, GameDataCatalog Catalog) Create()
    {
        var baseline = new StatBlock(new Dictionary<string, double>
        {
            ["health"] = 9500, ["energy"] = 2600, ["healthRegen"] = 90, ["energyRegen"] = 45,
            ["offensiveAbility"] = 2750, ["defensiveAbility"] = 2700, ["armor"] = 900,
            ["armorAbsorption"] = 70, ["fireResistance"] = 45, ["coldResistance"] = 45,
            ["lightningResistance"] = 45, ["acidResistance"] = 35, ["vitalityResistance"] = 35,
            ["aetherResistance"] = 35, ["chaosResistance"] = 35, ["physicalResistance"] = 8,
            ["attackSpeed"] = 35, ["lifeSteal"] = 5
        });
        var items = new List<ItemInstance>();
        var current = new Dictionary<EquipmentSlot, string>();
        var coordinates = 0;

        foreach (var target in new[]
        {
            EquipmentSlot.Head, EquipmentSlot.Shoulders, EquipmentSlot.Chest, EquipmentSlot.Hands,
            EquipmentSlot.Legs, EquipmentSlot.Feet, EquipmentSlot.Waist, EquipmentSlot.Amulet,
            EquipmentSlot.Medal, EquipmentSlot.Relic, EquipmentSlot.RingLeft, EquipmentSlot.RingRight,
            EquipmentSlot.MainHand, EquipmentSlot.OffHand
        })
        {
            var itemSlot = target is EquipmentSlot.RingLeft or EquipmentSlot.RingRight ? EquipmentSlot.Ring : target;
            var currentItem = MakeItem(itemSlot, $"守望者·{SlotName(target)}", coordinates++, "current", new StatBlock(new Dictionary<string, double>
            {
                ["health"] = target is EquipmentSlot.MainHand or EquipmentSlot.OffHand ? 80 : 180,
                ["defensiveAbility"] = 18,
                [ResistanceFor(target)] = 14,
                [StatKeys.DamagePercent("fire")] = 18,
                [StatKeys.DamageFlat("fire")] = target == EquipmentSlot.MainHand ? 85 : 4,
                [StatKeys.WeaponDamage("physical")] = target == EquipmentSlot.MainHand ? 120 : 0
            }), sourceKind: ItemSourceKind.Equipped);
            items.Add(currentItem);
            current[target] = currentItem.Id;

            items.Add(MakeItem(itemSlot, $"焚火·{SlotName(target)}", coordinates++, "damage", new StatBlock(new Dictionary<string, double>
            {
                ["offensiveAbility"] = 32,
                ["attackSpeed"] = target is EquipmentSlot.MainHand or EquipmentSlot.Hands ? 7 : 1,
                [StatKeys.DamagePercent("fire")] = 42,
                [StatKeys.DamageFlat("fire")] = target == EquipmentSlot.MainHand ? 145 : 11,
                [StatKeys.WeaponDamage("physical")] = target == EquipmentSlot.MainHand ? 135 : 0,
                [ResistanceFor(target)] = 7
            })));

            items.Add(MakeItem(itemSlot, $"堡垒·{SlotName(target)}", coordinates++, "defense", new StatBlock(new Dictionary<string, double>
            {
                ["health"] = 340,
                ["defensiveAbility"] = 38,
                ["armor"] = 90,
                [ResistanceFor(target)] = 20,
                [StatKeys.DamagePercent("fire")] = 8
            })));
        }

        items.Add(MakeItem(EquipmentSlot.MainHand, "余烬裁决（双手）", coordinates++, "twohand", new StatBlock(new Dictionary<string, double>
        {
            [StatKeys.WeaponDamage("physical")] = 260,
            [StatKeys.DamageFlat("fire")] = 220,
            [StatKeys.DamagePercent("fire")] = 180,
            ["offensiveAbility"] = 100,
            ["attackSpeed"] = 12
        }), twoHanded: true));

        var allArmor = Enum.GetValues<EquipmentSlot>().Where(x => x is not (EquipmentSlot.RingLeft or EquipmentSlot.RingRight or EquipmentSlot.Relic)).ToHashSet();
        var attachments = new List<AttachmentDefinition>
        {
            new()
            {
                Id = "demo/component/wardstone", Name = "守护之石", Kind = AttachmentKind.Component,
                CompatibleSlots = allArmor,
                Stats = new StatBlock(new Dictionary<string, double> { ["fireResistance"] = 8, ["coldResistance"] = 8, ["lightningResistance"] = 8 })
            },
            new()
            {
                Id = "demo/component/ember", Name = "灼热余烬", Kind = AttachmentKind.Component,
                CompatibleSlots = allArmor,
                Stats = new StatBlock(new Dictionary<string, double> { [StatKeys.DamageFlat("fire")] = 12, [StatKeys.DamagePercent("fire")] = 22 })
            },
            new()
            {
                Id = "demo/augment/survivor", Name = "幸存者的毅力", Kind = AttachmentKind.Augment,
                CompatibleSlots = allArmor,
                Stats = new StatBlock(new Dictionary<string, double> { ["health"] = 120, ["defensiveAbility"] = 12 })
            },
            new()
            {
                Id = "demo/augment/malmouth", Name = "马尔茅斯烈焰", Kind = AttachmentKind.Augment,
                CompatibleSlots = allArmor,
                Stats = new StatBlock(new Dictionary<string, double> { ["offensiveAbility"] = 16, [StatKeys.DamagePercent("fire")] = 18 })
            }
        };

        var character = new CharacterSnapshot
        {
            Name = "演示角色 · 炎术士",
            SaveFilePath = @"C:\demo\main\_demo\player.gdc",
            Level = 100,
            Physique = 1050,
            Cunning = 720,
            Spirit = 890,
            BaselineStats = baseline,
            Items = items,
            CurrentLoadout = current,
            PrimarySkillRecord = "demo/skill/fire-strike",
            Skills = [new CharacterSkill("demo/skill/fire-strike", "烈焰打击", 26, 0, true, string.Empty, string.Empty)],
            FactionReputation = new Dictionary<string, double> { ["faction:demo"] = 25_000 },
            KnownFormulas = new HashSet<string>()
        };
        var catalog = new GameDataCatalog
        {
            Version = "demo-1.3",
            Attachments = attachments,
            Records = new Dictionary<string, GameRecord>
            {
                ["demo/skill/fire-strike"] = new GameRecord
                {
                    RecordName = "demo/skill/fire-strike",
                    RecordType = "Skill_WeaponPool_BasicAttack",
                    NumericValues = new StatBlock(new Dictionary<string, double>
                    {
                        ["weaponDamagePct"] = 155,
                        ["skillManaCost"] = 4,
                        ["offensiveFireMin"] = 85
                    }),
                    TextValues = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["Class"] = ["Skill_WeaponPool_BasicAttack"]
                    }
                }
            }
        };
        return (character, catalog);
    }

    private static ItemInstance MakeItem(
        EquipmentSlot slot,
        string name,
        int index,
        string setId,
        StatBlock stats,
        ItemSourceKind sourceKind = ItemSourceKind.SharedStash,
        bool twoHanded = false)
    {
        var source = new ItemSourceLocation(sourceKind, @"C:\demo\save\transfer.gst", index / 18, index % 10, index % 18);
        var record = $"demo/items/{slot}/{index}.dbr";
        return new ItemInstance
        {
            Id = ItemInstance.CreateStableId(source, record, (uint)(1000 + index)),
            Name = name,
            Slot = slot,
            BaseRecord = record,
            Seed = (uint)(1000 + index),
            LevelRequirement = 94,
            IsTwoHanded = twoHanded,
            SetId = setId,
            Stats = stats,
            Source = source
        };
    }

    private static string ResistanceFor(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Head or EquipmentSlot.Hands or EquipmentSlot.RingLeft => "fireResistance",
        EquipmentSlot.Shoulders or EquipmentSlot.Feet or EquipmentSlot.RingRight => "coldResistance",
        EquipmentSlot.Chest or EquipmentSlot.Legs or EquipmentSlot.Waist => "lightningResistance",
        EquipmentSlot.Amulet or EquipmentSlot.Medal => "aetherResistance",
        _ => "chaosResistance"
    };

    private static string SlotName(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Head => "头盔", EquipmentSlot.Shoulders => "护肩", EquipmentSlot.Chest => "胸甲",
        EquipmentSlot.Hands => "手套", EquipmentSlot.Legs => "护腿", EquipmentSlot.Feet => "靴子",
        EquipmentSlot.Waist => "腰带", EquipmentSlot.Amulet => "项链", EquipmentSlot.Medal => "勋章",
        EquipmentSlot.Relic => "遗物", EquipmentSlot.RingLeft => "戒指Ⅰ", EquipmentSlot.RingRight => "戒指Ⅱ",
        EquipmentSlot.MainHand => "主手", EquipmentSlot.OffHand => "副手", _ => slot.ToString()
    };
}
