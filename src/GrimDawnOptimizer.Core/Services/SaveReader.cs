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
                var ownedAttachments = BuildOwnedAttachments(allItems.Select(x => x.Item), catalog);
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
        var source = new ItemSourceLocation(
            raw.SourceKind, filePath,
            raw.SourceKind == ItemSourceKind.Equipped && raw.EquippedSlot is not null ? (int)raw.EquippedSlot.Value : raw.ContainerIndex,
            raw.X, raw.Y);
        var itemName = ResolveItemName(baseRecord, raw, catalog);
        var effects = BuildTriggeredEffects(records, catalog);
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
            var skill = record.Text("itemSkillName");
            var modeledWeaponPool = !string.IsNullOrWhiteSpace(skill) &&
                                    catalog.Records.TryGetValue(skill, out var granted) &&
                                    (granted.Text("Class") + granted.RecordType).Contains("WPAttack", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(skill) && !modeledWeaponPool && effects.All(x => x.Id != skill))
                unsupported.Add(new UnsupportedEffect(skill, "尚未识别的装备触发技能", ["dps", "effectiveHealth"]));
        }

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
            PhysiqueRequirement = (int)baseRecord.NumericValues["characterStrengthReq"],
            CunningRequirement = (int)baseRecord.NumericValues["characterDexterityReq"],
            SpiritRequirement = (int)baseRecord.NumericValues["characterIntelligenceReq"],
            IsTwoHanded = GameStatTranslator.IsTwoHanded(baseRecord),
            AttachmentQualifier = GameStatTranslator.ResolveAttachmentQualifier(baseRecord),
            SetId = baseRecord.Text("itemSetName"),
            Stats = stats,
            Effects = effects,
            UnsupportedEffects = unsupported,
            Source = source
        };
    }

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
                GameStatTranslator.TranslateAtLevel(skill, Math.Max(1, (int)record.NumericValues["itemSkillLevelEq"])),
                NormalizeTrigger(controller?.Text("triggerType"))));
        }
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
