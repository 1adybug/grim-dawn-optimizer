using GrimDawnOptimizer.Core.Contracts;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;
using System.Text.RegularExpressions;

namespace GrimDawnOptimizer.Core.Services;

public sealed class SaveReader : ISaveReader
{
    private readonly CharacterFileParser _characterParser = new();
    private readonly TransferStashParser _stashParser = new();
    private readonly FormulaFileParser _formulaParser = new();

    public async Task<IReadOnlyList<CharacterSnapshot>> ReadCharactersAsync(
        SaveLocation location,
        GameDataCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var result = new List<CharacterSnapshot>();
        foreach (var file in location.CharacterFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var raw = await Task.Run(() => _characterParser.Parse(file), cancellationToken);
                var warnings = new List<string>();
                var sharedFile = ResolveSharedStash(location.RootPath, raw.IsHardcore);
                var allItems = new List<(RawItem Item, string FilePath)>(raw.Items.Select(x => (x, file)));
                if (sharedFile is not null)
                {
                    try
                    {
                        allItems.AddRange((await Task.Run(() => _stashParser.Parse(sharedFile), cancellationToken)).Select(x => (x, sharedFile)));
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"共享仓库读取失败：{ex.Message}");
                    }
                }

                var knownFormulas = ReadKnownFormulasBestEffort(location.FormulaFile, warnings);
                var converted = allItems.Select(x => ConvertItem(x.Item, x.FilePath, catalog, warnings))
                    .Where(x => x is not null)
                    .Cast<ItemInstance>()
                    .ToArray();
                var exactSeedCount = converted.Count(x => x.SeedStatsResolved);
                var expectedSeedCount = converted.Count(x => x.Seed != 0 && !x.SeedStatsResolved);
                if (exactSeedCount > 0)
                    warnings.Add($"已按存档 Seed 精确还原 {exactSeedCount} 件装备的实际浮动词条。");
                if (expectedSeedCount > 0)
                    warnings.Add($"另有 {expectedSeedCount} 件装备包含尚未建模的 Seed 字段，已保守回退到期望值。");
                var ownedAttachments = BuildOwnedAttachments(allItems, converted, catalog);
                var current = converted.Where(x => x.Source.Kind == ItemSourceKind.Equipped)
                    .Select(x => (Item: x, Slot: raw.Items.FirstOrDefault(r => r.Seed == x.Seed && r.BaseRecord == x.BaseRecord)?.EquippedSlot))
                    .Where(x => x.Slot is not null)
                    .ToDictionary(x => x.Slot!.Value, x => x.Item.Id);
                var canDualWield = ResolveCanDualWield(raw.Skills, converted, current, catalog);

                var baseline = BuildBaseline(raw, catalog);
                var factions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var faction in raw.Factions.Where(x => x.Unlocked))
                {
                    factions[$"faction:{faction.Index}"] = faction.Value;
                    factions[$"tagFactionUser{Math.Max(0, faction.Index - 6)}"] = faction.Value;
                }

                result.Add(new CharacterSnapshot
                {
                    Name = raw.Name,
                    SaveFilePath = file,
                    Level = raw.Level,
                    IsHardcore = raw.IsHardcore,
                    Physique = (int)Math.Round(raw.Physique),
                    Cunning = (int)Math.Round(raw.Cunning),
                    Spirit = (int)Math.Round(raw.Spirit),
                    BaselineStats = baseline,
                    Skills = raw.Skills.Select(x => new CharacterSkill(
                        x.Record, ResolveSkillName(x.Record, catalog), x.Level, x.DevotionLevel,
                        x.Enabled, x.AutoCastSkill, x.AutoCastController)).ToArray(),
                    FactionReputation = factions,
                    KnownFormulas = knownFormulas,
                    Items = converted,
                    OwnedAttachments = ownedAttachments,
                    CurrentLoadout = current,
                    PrimarySkillRecord = raw.PrimarySkillRecord,
                    SecondarySkillRecord = raw.SecondarySkillRecord,
                    CanDualWield = canDualWield,
                    ImportWarnings = warnings.Distinct().ToArray()
                });
            }
            catch (Exception ex)
            {
                result.Add(new CharacterSnapshot
                {
                    Name = Path.GetFileName(Path.GetDirectoryName(file)) ?? "读取失败",
                    SaveFilePath = file,
                    ImportWarnings = [$"角色存档读取失败：{ex.Message}"]
                });
            }
        }
        return result;
    }

    internal static bool ResolveCanDualWield(
        IEnumerable<RawSkill> skills,
        IReadOnlyList<ItemInstance> items,
        IReadOnlyDictionary<EquipmentSlot, string> currentLoadout,
        GameDataCatalog catalog)
    {
        if (currentLoadout.TryGetValue(EquipmentSlot.OffHand, out var offHandId) &&
            items.FirstOrDefault(x => x.Id == offHandId) is { Slot: EquipmentSlot.MainHand, IsTwoHanded: false })
            return true;

        if (skills.Any(x => x.Level > 0 && IsDualWieldSkill(x.Record, catalog)))
            return true;

        // Relics and armor can grant a dual-wield skill without adding it to the
        // character's normal skill list. Inspect only currently equipped records.
        foreach (var itemId in currentLoadout.Values)
        {
            var item = items.FirstOrDefault(x => x.Id == itemId);
            if (item is null) continue;
            foreach (var recordName in new[]
                     {
                         item.BaseRecord, item.PrefixRecord, item.SuffixRecord,
                         item.ModifierRecord, item.TransmuteRecord
                     }.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (!catalog.Records.TryGetValue(recordName, out var record)) continue;
                var grantedSkill = record.Text("itemSkillName");
                if (IsDualWieldSkill(grantedSkill, catalog)) return true;
            }
        }
        return false;
    }

    internal static bool IsDualWieldSkill(string recordName, GameDataCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(recordName)) return false;
        if (recordName.Contains("dualwield", StringComparison.OrdinalIgnoreCase) ||
            recordName.Contains("gunslinger", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!catalog.Records.TryGetValue(recordName, out var record)) return false;

        var @class = string.IsNullOrWhiteSpace(record.Text("Class")) ? record.RecordType : record.Text("Class");
        if (@class.Contains("DualWield", StringComparison.OrdinalIgnoreCase)) return true;

        var displayTag = record.Text("skillDisplayName");
        if (KnownDualWieldSkillTags.Contains(displayTag)) return true;

        var description = catalog.Tags.GetValueOrDefault(record.Text("skillDescription"), string.Empty);
        return description.Contains("dual wield", StringComparison.OrdinalIgnoreCase) &&
               (description.Contains("enable", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("allow", StringComparison.OrdinalIgnoreCase)) ||
               description.Contains("双持", StringComparison.OrdinalIgnoreCase) &&
               (description.Contains("允许", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("可以", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("启用", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> KnownDualWieldSkillTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "tagClass04SkillName05A",        // Dual Blades
        "tagGDX1Class07SkillName11A",   // Ranged Expertise
        "tagGDX3Class10SkillName05C",   // Frenzied Cry
        "tagGDX3Class10SkillName13A"    // Implements of War
    };

    internal static StatBlock BuildBaseline(RawCharacter raw, GameDataCatalog catalog)
    {
        var baseline = new StatBlock();
        baseline.Set("health", Math.Max(1, raw.Health));
        baseline.Set("energy", Math.Max(0, raw.Energy));
        baseline.Set("physique", raw.Physique);
        baseline.Set("cunning", raw.Cunning);
        baseline.Set("spirit", raw.Spirit);
        var playerRecord = catalog.Records.GetValueOrDefault("records/creatures/pc/malepc01.dbr")
                           ?? catalog.Records.GetValueOrDefault("records/creatures/pc/femalepc01.dbr");
        baseline.Set("offensiveAbility", playerRecord?.NumericValues["characterOffensiveAbility"] ?? 65);
        baseline.Set("defensiveAbility", playerRecord?.NumericValues["characterDefensiveAbility"] ?? 65);
        baseline.Set("characterLevel", raw.Level);
        baseline.Set("usePlayerAbilityFormula", 1);
        baseline.Set("armorAbsorption", 70);

        // level == 0 entries can still carry a non-zero devotionLevel in the save;
        // they are not allocated passive constellation nodes and must not affect stats.
        foreach (var skill in raw.Skills.Where(x => x.Level > 0))
        {
            if (!catalog.Records.TryGetValue(skill.Record, out var record) ||
                !SkillRotationFactory.ContributesPersistentStats(record)) continue;

            var translated = GameStatTranslator.TranslateAtLevel(record, Math.Max(skill.Level, skill.DevotionLevel));
            var @class = string.IsNullOrWhiteSpace(record.Text("Class")) ? record.RecordType : record.Text("Class");
            if (@class.Contains("Mastery", StringComparison.OrdinalIgnoreCase))
            {
                // The save contains the live health/energy values, but its three
                // attributes exclude mastery-bar grants. Add only those grants.
                foreach (var key in new[] { "physique", "cunning", "spirit" })
                    baseline.Add(key, translated[key]);
            }
            else
            {
                baseline.Add(translated);
                // Toggle/passive skill records often only point at the actual
                // stat-bearing buff.  The character sheet evaluates that child
                // at the owner's level (for example the gunner's OA aura).
                foreach (var childName in new[]
                         {
                             record.Text("buffSkillName"), record.Text("buffSelfSkillName"),
                             record.Text("buffSelf2SkillName")
                         }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (catalog.Records.TryGetValue(childName, out var child))
                        baseline.Add(GameStatTranslator.TranslateAtLevel(child, Math.Max(skill.Level, skill.DevotionLevel)));
            }
        }
        return baseline;
    }

    internal static IReadOnlyList<OwnedAttachmentInstance> BuildOwnedAttachments(
        IEnumerable<RawItem> rawItems,
        GameDataCatalog catalog)
    {
        var result = new List<OwnedAttachmentInstance>();
        foreach (var raw in rawItems)
        {
            if (!string.IsNullOrWhiteSpace(raw.ComponentRecord))
                result.Add(new OwnedAttachmentInstance(raw.ComponentRecord, AttachmentKind.Component, raw.CompletionBonusRecord));
            if (!string.IsNullOrWhiteSpace(raw.AugmentRecord))
                result.Add(new OwnedAttachmentInstance(raw.AugmentRecord, AttachmentKind.Augment));

            if (!catalog.Records.TryGetValue(raw.BaseRecord, out var baseRecord)) continue;
            var recordType = string.IsNullOrWhiteSpace(baseRecord.Text("Class")) ? baseRecord.RecordType : baseRecord.Text("Class");
            if (recordType.Equals("ItemRelic", StringComparison.OrdinalIgnoreCase))
                result.Add(new OwnedAttachmentInstance(raw.BaseRecord, AttachmentKind.Component, raw.CompletionBonusRecord));
            else if (recordType.Equals("ItemEnchantment", StringComparison.OrdinalIgnoreCase))
                result.Add(new OwnedAttachmentInstance(raw.BaseRecord, AttachmentKind.Augment));
        }
        return result.Distinct().ToArray();
    }

    internal static IReadOnlyList<OwnedAttachmentInstance> BuildOwnedAttachments(
        IEnumerable<(RawItem Item, string FilePath)> rawItems,
        IReadOnlyList<ItemInstance> convertedItems,
        GameDataCatalog catalog)
    {
        var convertedById = convertedItems.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<OwnedAttachmentInstance>();
        foreach (var (raw, filePath) in rawItems)
        {
            var location = CreateItemSource(raw, filePath);
            var stableId = ItemInstance.CreateStableId(location, raw.BaseRecord, raw.Seed);
            convertedById.TryGetValue(stableId, out var host);
            var installedSource = host is null
                ? null
                : new AttachmentAcquisitionSource(
                    AttachmentAcquisitionKind.Installed,
                    host.Source,
                    host.Id,
                    host.Name,
                    raw.EquippedSlot);

            if (!string.IsNullOrWhiteSpace(raw.ComponentRecord))
                result.Add(new OwnedAttachmentInstance(
                    raw.ComponentRecord,
                    AttachmentKind.Component,
                    raw.CompletionBonusRecord,
                    installedSource));
            if (!string.IsNullOrWhiteSpace(raw.AugmentRecord))
                result.Add(new OwnedAttachmentInstance(
                    raw.AugmentRecord,
                    AttachmentKind.Augment,
                    Source: installedSource));

            if (!catalog.Records.TryGetValue(raw.BaseRecord, out var baseRecord)) continue;
            var recordType = string.IsNullOrWhiteSpace(baseRecord.Text("Class")) ? baseRecord.RecordType : baseRecord.Text("Class");
            var looseSource = new AttachmentAcquisitionSource(AttachmentAcquisitionKind.Loose, location);
            if (recordType.Equals("ItemRelic", StringComparison.OrdinalIgnoreCase))
                result.Add(new OwnedAttachmentInstance(
                    raw.BaseRecord,
                    AttachmentKind.Component,
                    raw.CompletionBonusRecord,
                    looseSource));
            else if (recordType.Equals("ItemEnchantment", StringComparison.OrdinalIgnoreCase))
                result.Add(new OwnedAttachmentInstance(
                    raw.BaseRecord,
                    AttachmentKind.Augment,
                    Source: looseSource));
        }
        return result.Distinct().ToArray();
    }

    private static ItemInstance? ConvertItem(
        RawItem raw,
        string filePath,
        GameDataCatalog catalog,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(raw.BaseRecord) || !catalog.Records.TryGetValue(raw.BaseRecord, out var baseRecord)) return null;
        var slot = GameStatTranslator.ResolveSlot(baseRecord);
        if (slot is null) return null;

        var recordNames = new[]
        {
            raw.BaseRecord, raw.PrefixRecord, raw.SuffixRecord, raw.ModifierRecord,
            raw.TransmuteRecord, raw.AscendantPrefixRecord, raw.AscendantSuffixRecord
        }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var records = recordNames.Select(x => catalog.Records.GetValueOrDefault(x))
            .Where(x => x is not null).Cast<GameRecord>().ToArray();
        catalog.Records.TryGetValue(raw.PrefixRecord, out var prefixRecord);
        catalog.Records.TryGetValue(raw.SuffixRecord, out var suffixRecord);
        var seeded = SeededItemStatResolver.Resolve(baseRecord, prefixRecord, suffixRecord, raw.Seed);
        var supplementalRecords = new[]
        {
            raw.ModifierRecord, raw.TransmuteRecord, raw.AscendantPrefixRecord, raw.AscendantSuffixRecord
        }.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => catalog.Records.GetValueOrDefault(x)).Where(x => x is not null).Cast<GameRecord>().ToArray();

        StatBlock stats;
        if (seeded.IsExact)
        {
            stats = seeded.Stats;
            stats.Add(GameStatTranslator.Translate(supplementalRecords));
        }
        else
        {
            stats = GameStatTranslator.Translate(records);
            if (slot == EquipmentSlot.MainHand)
            {
                var baseWeaponStats = GameStatTranslator.Translate([baseRecord]);
                // Older physical weapons store their base roll as offensivePhysical,
                // while current elemental weapons use offensiveBase<Type>. Only the
                // legacy physical field needs moving out of global flat damage.
                var legacyPhysical = baseWeaponStats[StatKeys.DamageFlat("physical")];
                if (Math.Abs(legacyPhysical) > 1e-9)
                {
                    stats.Add(StatKeys.DamageFlat("physical"), -legacyPhysical);
                    stats.Add(StatKeys.WeaponDamage("physical"), legacyPhysical);
                }
            }
            ApplyExpectedItemScale(stats, baseRecord.NumericValues["attributeScalePercent"]);
        }
        var source = CreateItemSource(raw, filePath);
        var itemName = ResolveItemName(baseRecord, raw, catalog);
        var effects = BuildTriggeredEffects(records, catalog);
        var requirements = ResolveEquipmentRequirements(baseRecord, catalog);
        var unsupported = new List<UnsupportedEffect>();
        if (!seeded.IsExact && raw.Seed != 0)
        {
            var fields = string.Join(", ", seeded.UnmodeledFields.Take(4));
            if (seeded.UnmodeledFields.Count > 4) fields += "…";
            unsupported.Add(new UnsupportedEffect(
                raw.BaseRecord,
                $"装备 Seed 浮动值回退到期望值：{fields}",
                ["dps", "effectiveHealth"]));
        }
        foreach (var record in records)
        {
            var unsupportedFields = GameStatTranslator.FindUnsupportedCombatFields(record);
            if (unsupportedFields.Count > 0)
            {
                var fields = string.Join(", ", unsupportedFields.Take(6));
                if (unsupportedFields.Count > 6) fields += "…";
                unsupported.Add(new UnsupportedEffect(
                    record.RecordName,
                    $"未建模装备词条：{fields}",
                    ["dps", "effectiveHealth"]));
            }
        }
        unsupported.AddRange(FindUnsupportedItemSkillEffects(records, catalog, "未完整建模的装备技能"));

        if (records.Length < recordNames.Length)
            warnings.Add($"{itemName} 存在未在游戏数据库中找到的记录，结果已降低置信度");

        return new ItemInstance
        {
            Id = ItemInstance.CreateStableId(source, raw.BaseRecord, raw.Seed),
            Name = itemName,
            Slot = slot.Value,
            BaseRecord = raw.BaseRecord,
            PrefixRecord = raw.PrefixRecord,
            SuffixRecord = raw.SuffixRecord,
            ModifierRecord = raw.ModifierRecord,
            TransmuteRecord = raw.TransmuteRecord,
            Seed = raw.Seed,
            SeedStatsResolved = seeded.IsExact,
            ComponentRecord = raw.ComponentRecord,
            CompletionBonusRecord = raw.CompletionBonusRecord,
            ComponentSeed = raw.ComponentSeed,
            AugmentRecord = raw.AugmentRecord,
            AugmentSeed = raw.AugmentSeed,
            LevelRequirement = (int)records.Max(x => x.NumericValues["levelRequirement"]),
            PhysiqueRequirement = requirements.Physique,
            CunningRequirement = requirements.Cunning,
            SpiritRequirement = requirements.Spirit,
            IsTwoHanded = GameStatTranslator.IsTwoHanded(baseRecord),
            AttachmentQualifier = GameStatTranslator.ResolveAttachmentQualifier(baseRecord),
            SetId = baseRecord.Text("itemSetName"),
            Stats = stats,
            Effects = effects,
            UnsupportedEffects = unsupported,
            Source = source
        };
    }

    private static ItemSourceLocation CreateItemSource(RawItem raw, string filePath) => new(
        raw.SourceKind,
        filePath,
        raw.SourceKind == ItemSourceKind.Equipped && raw.EquippedSlot is not null
            ? (int)raw.EquippedSlot.Value
            : raw.ContainerIndex,
        raw.X,
        raw.Y);

    internal static EquipmentRequirements ResolveEquipmentRequirements(
        GameRecord record,
        GameDataCatalog catalog)
    {
        var direct = new EquipmentRequirements(
            ReadEquipmentRequirement(record, "strengthRequirement", "characterStrengthReq"),
            ReadEquipmentRequirement(record, "dexterityRequirement", "characterDexterityReq"),
            ReadEquipmentRequirement(record, "intelligenceRequirement", "characterIntelligenceReq"));
        var itemClass = string.IsNullOrWhiteSpace(record.Text("Class")) ? record.RecordType : record.Text("Class");
        var equations = RequirementEquations(itemClass);
        if (equations == default) return direct;

        var formulaRecordName = record.Text("itemCostName");
        if (string.IsNullOrWhiteSpace(formulaRecordName))
            formulaRecordName = "records/game/itemcostformulas.dbr";
        if (!catalog.Records.TryGetValue(formulaRecordName, out var formulas)) return direct;

        var variables = BuildRequirementVariables(record);
        return new EquipmentRequirements(
            EvaluateRequirement(formulas.Text(equations.Physique), variables, direct.Physique),
            EvaluateRequirement(formulas.Text(equations.Cunning), variables, direct.Cunning),
            EvaluateRequirement(formulas.Text(equations.Spirit), variables, direct.Spirit));
    }

    private static EquipmentRequirementEquations RequirementEquations(string itemClass) => itemClass switch
    {
        "ArmorProtective_Head" => new("headStrengthEquation", "", "headIntelligenceEquation"),
        "ArmorProtective_Shoulders" => new("shouldersStrengthEquation", "", ""),
        "ArmorProtective_Chest" => new("chestStrengthEquation", "", "chestIntelligenceEquation"),
        "ArmorProtective_Hands" => new("handsStrengthEquation", "", ""),
        "ArmorProtective_Legs" => new("legsStrengthEquation", "", ""),
        "ArmorProtective_Feet" => new("feetStrengthEquation", "", ""),
        "ArmorProtective_Waist" => new("waistStrengthEquation", "", ""),
        "ArmorJewelry_Amulet" or "ArmorJewelry_Medal" => new("", "", "amuletIntelligenceEquation"),
        "ArmorJewelry_Ring" => new("", "", "ringIntelligenceEquation"),
        "WeaponArmor_Offhand" => new("", "", "offhandIntelligenceEquation"),
        "WeaponArmor_Shield" => new("shieldStrengthEquation", "", ""),
        "WeaponMelee_Axe" => new("axeStrengthEquation", "", ""),
        "WeaponMelee_Mace" or "WeaponHunting_Spear" or "WeaponMelee_Spear2h" =>
            new("maceStrengthEquation", "", ""),
        "WeaponMelee_Sword" => new("", "swordDexterityEquation", ""),
        "WeaponMelee_Dagger" or "WeaponMagical_Staff" =>
            new("", "daggerDexterityEquation", "daggerIntelligenceEquation"),
        "WeaponMelee_Scepter" => new("scepterStrengthEquation", "", "scepterIntelligenceEquation"),
        "WeaponHunting_Ranged1h" => new("", "ranged1hDexterityEquation", ""),
        "WeaponMelee_Axe2h" or "WeaponMelee_Mace2h" or "WeaponMelee_Sword2h" =>
            new("melee2hStrengthEquation", "", ""),
        "WeaponHunting_Ranged2h" => new("", "ranged2hDexterityEquation", ""),
        _ => default
    };

    private static Dictionary<string, double> BuildRequirementVariables(GameRecord record)
    {
        var itemLevel = record.NumericValues["itemLevel"];
        if (itemLevel <= 0) itemLevel = record.NumericValues["levelRequirement"];
        var pierceMin = record.NumericValues["offensivePierceRatioMin"];
        var pierceMax = record.NumericValues["offensivePierceRatioMax"];
        var damageTypes = new[] { "Aether", "Chaos", "Cold", "Fire", "Life", "Lightning", "Poison" };
        var damageAverage = damageTypes.Sum(type => AverageRange(
            record.NumericValues[$"offensiveBase{type}Min"],
            record.NumericValues[$"offensiveBase{type}Max"]));
        damageAverage += AverageRange(
            record.NumericValues["offensivePhysicalMin"] + record.NumericValues["offensiveBasePhysicalMin"],
            record.NumericValues["offensivePhysicalMax"] + record.NumericValues["offensiveBasePhysicalMax"]);

        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["itemLevel"] = itemLevel,
            ["charAttackSpeed"] = record.NumericValues["characterAttackSpeed"],
            ["defenseAttrArmor"] = record.NumericValues["defensiveProtection"],
            ["itemBaseCost"] = 1000,
            ["shieldBlockDefense"] = Math.Max(record.NumericValues["defensiveBlock"], record.NumericValues["defensiveBlockAmount"]),
            ["shieldBlockChance"] = record.NumericValues["defensiveBlockChance"],
            ["damageAvgPierceRatio"] = AverageRange(pierceMin, pierceMax),
            ["damageAvgBase"] = damageAverage,
            ["totalAttCount"] = CountRequirementAttributes(record),
            ["itemPrefixCost"] = 0,
            ["itemSuffixCost"] = 0
        };
    }

    private static int CountRequirementAttributes(GameRecord record) => record.NumericValues.Values.Count(x =>
        x.Value > 0 &&
        (x.Key.StartsWith("offensive", StringComparison.OrdinalIgnoreCase) ||
         x.Key.StartsWith("defensive", StringComparison.OrdinalIgnoreCase) ||
         x.Key.StartsWith("character", StringComparison.OrdinalIgnoreCase)) &&
        !x.Key.EndsWith(".min", StringComparison.OrdinalIgnoreCase) &&
        !x.Key.EndsWith(".max", StringComparison.OrdinalIgnoreCase));

    private static double AverageRange(double minimum, double maximum) =>
        maximum > 0 ? (minimum + maximum) / 2d : minimum;

    private static int EvaluateRequirement(
        string formula,
        IReadOnlyDictionary<string, double> variables,
        int fallback)
    {
        if (string.IsNullOrWhiteSpace(formula) ||
            !FormulaEvaluator.TryEvaluate(formula, variables, out var value)) return fallback;
        return (int)Math.Floor(Math.Max(0, value) + 0.5);
    }

    private static int ReadEquipmentRequirement(GameRecord record, string field, string legacyField)
    {
        var value = record.NumericValues[field];
        if (value <= 0) value = record.NumericValues[legacyField];
        return (int)Math.Ceiling(Math.Max(0, value) - 1e-9);
    }

    internal readonly record struct EquipmentRequirements(int Physique, int Cunning, int Spirit);
    private readonly record struct EquipmentRequirementEquations(string Physique, string Cunning, string Spirit);

    internal static void ApplyExpectedItemScale(StatBlock stats, double attributeScalePercent)
    {
        if (attributeScalePercent <= 0) return;
        var factor = 1 + attributeScalePercent / 100d;
        foreach (var (key, value) in stats.Values.ToArray())
            if (key.StartsWith("damage.", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("weaponDamage.", StringComparison.OrdinalIgnoreCase))
                stats.Set(key, value * factor);
        stats.Set("itemRollRangePercent", 20);
    }

    internal static IReadOnlyList<TriggeredEffect> BuildTriggeredEffects(IReadOnlyList<GameRecord> records, GameDataCatalog catalog)
    {
        var result = new List<TriggeredEffect>();
        foreach (var record in records)
        {
            var skillName = record.Text("itemSkillName");
            if (string.IsNullOrWhiteSpace(skillName) || !catalog.Records.TryGetValue(skillName, out var skill)) continue;
            var controllerName = record.Text("itemSkillAutoController");
            if (string.IsNullOrWhiteSpace(controllerName))
                controllerName = skill.Text("skillAutoController");
            // An itemSkillName without an autocast controller is a granted active
            // skill/WPS, not an automatic proc.
            if (string.IsNullOrWhiteSpace(controllerName)) continue;
            catalog.Records.TryGetValue(controllerName, out var controller);
            var chance = controller?.NumericValues["triggerEventChance"] ?? 0;
            if (chance <= 0)
            {
                var match = Regex.Match(controllerName, @"_(?<chance>\d+(?:\.\d+)?)%", RegexOptions.CultureInvariant);
                if (match.Success && double.TryParse(match.Groups["chance"].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var nameChance))
                    chance = nameChance;
            }
            if (chance <= 0) chance = skill.NumericValues["skillChance"];
            if (chance > 1) chance /= 100d;
            if (chance <= 0) chance = 1;
            var cooldown = controller?.NumericValues["triggerCooldown"] ?? 0;
            if (cooldown <= 0) cooldown = skill.NumericValues["skillCooldownTime"];
            result.Add(new TriggeredEffect(
                skillName,
                catalog.Tags.GetValueOrDefault(skill.Text("skillDisplayName"), Path.GetFileNameWithoutExtension(skillName)),
                chance,
                cooldown,
                TranslateTriggeredSkillDamage(skill, catalog, Math.Max(1, (int)record.NumericValues["itemSkillLevelEq"])),
                NormalizeTrigger(controller?.Text("triggerType")),
                WeaponDamagePercent: Math.Max(0, GameStatTranslator.NumericAtLevel(
                    skill, "weaponDamagePct", Math.Max(1, (int)record.NumericValues["itemSkillLevelEq"])))));
        }
        return result;
    }

    internal static IReadOnlyList<UnsupportedEffect> FindUnsupportedItemSkillEffects(
        IReadOnlyList<GameRecord> records,
        GameDataCatalog catalog,
        string descriptionPrefix)
    {
        var result = new List<UnsupportedEffect>();
        var modeledEffects = BuildTriggeredEffects(records, catalog);
        foreach (var owner in records)
        {
            for (var index = 1; index <= 32; index++)
            {
                var modifiedSkillName = owner.Text($"modifiedSkillName{index}");
                var modifierSkillName = owner.Text($"modifierSkillName{index}");
                if (string.IsNullOrWhiteSpace(modifiedSkillName) &&
                    string.IsNullOrWhiteSpace(modifierSkillName)) continue;
                if (string.IsNullOrWhiteSpace(modifierSkillName) ||
                    !catalog.Records.TryGetValue(modifierSkillName, out var modifier))
                {
                    result.Add(new UnsupportedEffect(
                        string.IsNullOrWhiteSpace(modifierSkillName) ? owner.RecordName : modifierSkillName,
                        $"{descriptionPrefix}：技能修正记录缺失",
                        ["dps", "effectiveHealth"]));
                    continue;
                }

                var modifierUnsupportedFields = GameStatTranslator.FindUnsupportedCombatFields(modifier);
                if (modifierUnsupportedFields.Count == 0) continue;
                var modifierDisplay = string.Join(", ", modifierUnsupportedFields.Take(6));
                if (modifierUnsupportedFields.Count > 6) modifierDisplay += "…";
                result.Add(new UnsupportedEffect(
                    modifierSkillName,
                    $"{descriptionPrefix}修正词条：{modifierDisplay}",
                    ["dps", "effectiveHealth"]));
            }

            var skillName = owner.Text("itemSkillName");
            if (string.IsNullOrWhiteSpace(skillName)) continue;
            if (!catalog.Records.TryGetValue(skillName, out var skill))
            {
                result.Add(new UnsupportedEffect(skillName,
                    $"{descriptionPrefix}：游戏数据库中未找到技能记录", ["dps", "effectiveHealth"]));
                continue;
            }

            var skillClass = skill.Text("Class") + skill.RecordType;
            if (skillClass.Contains("WPAttack", StringComparison.OrdinalIgnoreCase)) continue;

            var effect = modeledEffects.FirstOrDefault(x => x.Id.Equals(skillName, StringComparison.OrdinalIgnoreCase));
            if (effect is null)
            {
                result.Add(new UnsupportedEffect(skillName,
                    $"{descriptionPrefix}：授予型主动或被动技能", ["dps", "effectiveHealth"]));
                continue;
            }

            if (!effect.Trigger.Equals("OnAttack", StringComparison.OrdinalIgnoreCase) &&
                !effect.Trigger.Equals("OnCrit", StringComparison.OrdinalIgnoreCase))
                result.Add(new UnsupportedEffect(skillName,
                    $"{descriptionPrefix}：触发条件 {effect.Trigger}", ["dps", "effectiveHealth"]));

            var hasDamage = effect.WeaponDamagePercent > 0 || StatKeys.DamageTypes.Any(type =>
                Math.Abs(effect.Damage[StatKeys.DamageFlat(type)]) > 1e-9 ||
                Math.Abs(effect.Damage[$"damage.{type}.dotTotal"]) > 1e-9);
            if (!hasDamage)
                result.Add(new UnsupportedEffect(skillName,
                    $"{descriptionPrefix}：治疗、防御或增益型触发效果", ["dps", "effectiveHealth"]));

            var linked = new[]
            {
                skill, catalog.Records.GetValueOrDefault(skill.Text("skillProjectileName")),
                catalog.Records.GetValueOrDefault(skill.Text("buffSkillName")),
                catalog.Records.GetValueOrDefault(skill.Text("buffSelfSkillName")),
                catalog.Records.GetValueOrDefault(skill.Text("buffSelf2SkillName"))
            }.Where(x => x is not null).Cast<GameRecord>();
            var unsupportedFields = linked.SelectMany(GameStatTranslator.FindUnsupportedCombatFields)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (unsupportedFields.Length > 0)
            {
                var display = string.Join(", ", unsupportedFields.Take(6));
                if (unsupportedFields.Length > 6) display += "…";
                result.Add(new UnsupportedEffect(skillName,
                    $"{descriptionPrefix}词条：{display}", ["dps", "effectiveHealth"]));
            }
        }
        return result;
    }

    private static StatBlock TranslateTriggeredSkillDamage(GameRecord skill, GameDataCatalog catalog, int level)
    {
        var result = GameStatTranslator.TranslateAtLevel(skill, level);
        result.Set("proc.weaponDamagePercent", Math.Max(0,
            GameStatTranslator.NumericAtLevel(skill, "weaponDamagePct", level)));
        foreach (var link in new[]
                 {
                     skill.Text("skillProjectileName"), skill.Text("buffSkillName"),
                     skill.Text("buffSelfSkillName"), skill.Text("buffSelf2SkillName")
                 }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (catalog.Records.TryGetValue(link, out var linked))
                result.Add(GameStatTranslator.TranslateAtLevel(linked, level));
        return result;
    }

    private static string NormalizeTrigger(string? trigger) => trigger?.ToLowerInvariant() switch
    {
        null or "" => "OnAttack",
        var value when value.Contains("crit", StringComparison.Ordinal) => "OnCrit",
        var value when value.Contains("attack", StringComparison.Ordinal) => "OnAttack",
        var value when value.Contains("hit", StringComparison.Ordinal) => "OnHit",
        _ => trigger!
    };

    private static string ResolveItemName(GameRecord record, RawItem raw, GameDataCatalog catalog)
    {
        var nameTag = record.Text("itemNameTag");
        if (string.IsNullOrWhiteSpace(nameTag))
            nameTag = record.Text("description").Replace("^k", string.Empty, StringComparison.OrdinalIgnoreCase);
        var baseName = catalog.Tags.GetValueOrDefault(nameTag,
            string.IsNullOrWhiteSpace(nameTag) ? Path.GetFileNameWithoutExtension(record.RecordName) : nameTag);
        var prefix = catalog.Records.TryGetValue(raw.PrefixRecord, out var prefixRecord)
            ? catalog.Tags.GetValueOrDefault(prefixRecord.Text("lootRandomizerName"), string.Empty) : string.Empty;
        var suffix = catalog.Records.TryGetValue(raw.SuffixRecord, out var suffixRecord)
            ? catalog.Tags.GetValueOrDefault(suffixRecord.Text("lootRandomizerName"), string.Empty) : string.Empty;
        // Chinese Grim Dawn displays a rare item's suffix before its prefix and base name,
        // for example: “焚尽之 净化的 烈火兜盔”.
        return string.Join(' ', new[] { suffix, prefix, baseName }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string ResolveSkillName(string recordName, GameDataCatalog catalog)
    {
        if (!catalog.Records.TryGetValue(recordName, out var record)) return Path.GetFileNameWithoutExtension(recordName);
        return catalog.Tags.GetValueOrDefault(record.Text("skillDisplayName"), Path.GetFileNameWithoutExtension(recordName));
    }

    private IReadOnlySet<string> ReadKnownFormulasBestEffort(string? filePath, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return new HashSet<string>();
        try
        {
            return _formulaParser.Parse(filePath);
        }
        catch (Exception ex)
        {
            warnings.Add($"配方文件读取失败：{ex.Message}；本次只启用无需配方的组件");
            return new HashSet<string>();
        }
    }

    private static string? ResolveSharedStash(string root, bool hardcore)
    {
        var path = Path.Combine(root, hardcore ? "transfer.gsh" : "transfer.gst");
        return File.Exists(path) ? path : null;
    }
}
