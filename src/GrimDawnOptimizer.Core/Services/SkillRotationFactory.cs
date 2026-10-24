using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

public static class SkillRotationFactory
{
    private static readonly string[] DamageRecordLinks =
    [
        "skillProjectileName", "buffSkillName", "buffSelfSkillName", "buffSelf2SkillName"
    ];

    public static SkillRotationDefinition Build(
        CharacterSnapshot character,
        GameDataCatalog catalog,
        CharacterSkill? selectedSkill,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants = null)
    {
        if (selectedSkill is null ||
            string.IsNullOrWhiteSpace(selectedSkill.Record) ||
            !catalog.Records.TryGetValue(selectedSkill.Record, out var mainRecord))
            return Fallback(character, catalog, selectedSkill, equippedVariants);

        var baseLevel = Math.Max(1, Math.Max(selectedSkill.Level, selectedSkill.DevotionLevel));
        var itemSkillBonus = ResolveCurrentSkillBonus(character, catalog, selectedSkill.Record, equippedVariants);
        var level = baseLevel + itemSkillBonus;
        var records = ResolveDamageRecords(mainRecord, catalog)
            .Select(x => (Record: x, Level: level))
            .ToList();
        var childRecords = ResolveAllocatedChildRecords(character, catalog, selectedSkill.Record)
            .Select(x => (x.Record, Level: x.Level + ResolveCurrentSkillBonus(character, catalog, x.Record.RecordName, equippedVariants)))
            .ToArray();
        // Secondary attacks are distinct hit payloads. Adding their weapon
        // percentage to the primary hit would also apply its dual-wield strike
        // count to the secondary, and leak secondary conversions/bonuses back
        // into the primary attack.
        records.AddRange(childRecords.Where(x => !IsSecondaryAttack(x.Record)));
        var secondaryAttacks = childRecords.Where(x => IsSecondaryAttack(x.Record))
            .Select(x => BuildSecondaryAttack(character, catalog, x.Record, x.Level, equippedVariants))
            .ToList();
        var itemModifierRecords = ResolveCurrentSkillModifiers(character, catalog, selectedSkill.Record, equippedVariants);
        records.AddRange(itemModifierRecords.Select(x => (x, Level: 1)));
        var flatDamage = new StatBlock();
        foreach (var entry in records)
            flatDamage.Add(GameStatTranslator.TranslateAtLevel(entry.Record, entry.Level));

        var weaponDamage = FirstPositive(records.Select(x => x.Record), "weaponDamagePct", level);
        var energy = FirstPositive(records.Select(x => x.Record), "skillManaCost", level);
        if (energy <= 0) energy = FirstPositive(records.Select(x => x.Record), "skillActiveManaCost", level);
        foreach (var child in childRecords)
            energy += Math.Max(0, GameStatTranslator.NumericAtLevel(child.Record, "skillManaCost", child.Level));
        var cooldown = FirstPositive(records.Select(x => x.Record), "skillCooldownTime", level);
        var usesAttackSpeed = UsesWeaponAttackSpeed(mainRecord);
        var activationRate = ResolveActivationRate(mainRecord, level);
        var hasDamage = weaponDamage > 0 || secondaryAttacks.Count > 0 || StatKeys.DamageTypes.Any(x =>
            Math.Abs(flatDamage[StatKeys.DamageFlat(x)]) > 1e-9);

        // A weapon-pool/default attack record with no explicit percentage inherits
        // the normal 100% weapon hit. Pure spells intentionally remain at 0%.
        if (weaponDamage <= 0 && usesAttackSpeed) weaponDamage = 100;

        var (weaponPoolChance, weaponPoolMultiplier, weaponPoolFlat, weaponPoolAttacks) = usesAttackSpeed
            ? ResolveWeaponPool(character, catalog, selectedSkill.Record, equippedVariants)
            : (0d, 1d, new StatBlock(), new List<WeaponPoolAttackDefinition>());
        var speedName = usesAttackSpeed ? "攻击速度" : "施法速度";
        var summary = hasDamage || weaponDamage > 0
            ? $"Lv.{level} · {weaponDamage:0.#}% 武器伤害 · {speedName}" +
              (itemSkillBonus > 0 ? $"（基础 {baseLevel} + 装备 {itemSkillBonus}）" : string.Empty) +
              (cooldown > 0 ? $" · 冷却 {cooldown:0.##}s" : string.Empty)
              + (activationRate < 0.999 ? $" · 每 {Math.Round(1 / activationRate):0} 击触发" : string.Empty)
              + (childRecords.Length > 0 ? $" · {childRecords.Length} 个已加点子技能" : string.Empty)
              + (itemModifierRecords.Count > 0 ? $" · {itemModifierRecords.Count} 个装备技能修正" : string.Empty)
              + (flatDamage["criticalDamage"] > 0 ? $" · 面板暴击修正 +{flatDamage["criticalDamage"]:0.#}%" : string.Empty)
              + (weaponPoolChance > 0 ? $" · 武器池 {weaponPoolChance:0.#}%" : string.Empty)
            : $"Lv.{level} 未找到直接伤害，暂按默认攻击估算";

        return new SkillRotationDefinition
        {
            MainSkillRecord = selectedSkill.Record,
            MainSkillName = selectedSkill.Name,
            WeaponDamagePercent = hasDamage || weaponDamage > 0 ? weaponDamage : 100,
            BaseAttacksPerSecond = usesAttackSpeed
                ? CharacterStatEngine.ResolveAnimationProfile(character, catalog)?.UnarmedPanelAttacksPerSecond ?? 1.5
                : 1.5,
            EnergyPerUse = energy,
            CooldownSeconds = cooldown,
            ActivationRate = activationRate,
            WeaponPoolChance = weaponPoolChance,
            WeaponPoolDamageMultiplier = weaponPoolMultiplier,
            UsesAttackSpeed = usesAttackSpeed,
            IsFallback = !hasDamage && weaponDamage <= 0,
            CalculationSummary = summary,
            FlatDamage = flatDamage,
            WeaponPoolFlatDamage = weaponPoolFlat,
            WeaponPoolAttacks = weaponPoolAttacks,
            SecondaryAttacks = secondaryAttacks
        };
    }

    private static bool IsSecondaryAttack(GameRecord record) =>
        RecordClass(record).Contains("Secondary", StringComparison.OrdinalIgnoreCase);

    private static SkillSecondaryAttackDefinition BuildSecondaryAttack(
        CharacterSnapshot character,
        GameDataCatalog catalog,
        GameRecord secondary,
        int level,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants)
    {
        var records = ResolveDamageRecords(secondary, catalog)
            .Select(x => (Record: x, Level: level))
            .Concat(ResolveCurrentSkillModifiers(character, catalog, secondary.RecordName, equippedVariants)
                .Select(x => (Record: x, Level: 1)))
            .ToArray();
        var sources = records.Select(x => new SkillDamageSourceDefinition
        {
            Record = x.Record.RecordName,
            Level = x.Level,
            WeaponDamagePercent = Math.Max(0, GameStatTranslator.NumericAtLevel(x.Record, "weaponDamagePct", x.Level)),
            TranslatedStats = GameStatTranslator.TranslateAtLevel(x.Record, x.Level)
        }).ToList();
        var flatDamage = new StatBlock();
        foreach (var source in sources) flatDamage.Add(source.TranslatedStats);
        return new SkillSecondaryAttackDefinition
        {
            Record = secondary.RecordName,
            Name = character.Skills.FirstOrDefault(x => x.Record.Equals(secondary.RecordName, StringComparison.OrdinalIgnoreCase))?.Name
                ?? secondary.RecordName,
            Level = level,
            WeaponDamagePercent = sources.Select(x => x.WeaponDamagePercent).FirstOrDefault(x => x > 0),
            FlatDamage = flatDamage,
            SourceRecords = sources
        };
    }

    public static bool IsDirectAttackSkill(CharacterSkill skill, GameDataCatalog catalog)
    {
        if (!catalog.Records.TryGetValue(skill.Record, out var record)) return false;
        var @class = RecordClass(record);
        if (@class.Contains("Passive", StringComparison.OrdinalIgnoreCase) ||
            @class.Contains("Modifier", StringComparison.OrdinalIgnoreCase) ||
            @class.Contains("Secondary", StringComparison.OrdinalIgnoreCase) ||
            @class.Contains("Transmuter", StringComparison.OrdinalIgnoreCase) ||
            @class.Contains("WPAttack", StringComparison.OrdinalIgnoreCase))
            return false;
        return @class.Contains("Attack", StringComparison.OrdinalIgnoreCase) ||
               @class.Contains("WeaponPool", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ContributesPersistentStats(GameRecord record)
    {
        var @class = RecordClass(record);
        // A learned/enabled conditional passive is eligible to trigger; it is
        // not evidence that its temporary buff is currently active. A runtime
        // buff state would need to be supplied separately from the save.
        if (@class.Contains("PassiveOn", StringComparison.OrdinalIgnoreCase)) return false;
        return @class.Contains("Passive", StringComparison.OrdinalIgnoreCase) ||
               @class.Contains("Mastery", StringComparison.OrdinalIgnoreCase) ||
               @class.Contains("BuffSelf", StringComparison.OrdinalIgnoreCase) ||
               @class.Contains("BuffRadius", StringComparison.OrdinalIgnoreCase) ||
               IsReactiveAura(record);
    }

    internal static GameRecord? ResolvePersistentSkillOwner(
        GameRecord record,
        IEnumerable<string> allocatedSkillRecords,
        GameDataCatalog catalog)
    {
        if (ContributesPersistentStats(record)) return record;
        var @class = RecordClass(record);
        if (!@class.Contains("Modifier", StringComparison.OrdinalIgnoreCase) &&
            !@class.Contains("Transmuter", StringComparison.OrdinalIgnoreCase)) return null;

        // Numbered mastery skills form a family (for example blastshield1 is
        // Flame Touched, blastshield2 is Temper). A learned modifier inherits
        // persistence only from a learned, persistent root in that family.
        // Attack modifiers and modifiers of conditional buffs remain excluded.
        var directory = Path.GetDirectoryName(record.RecordName)?.Replace('\\', '/');
        var stem = Path.GetFileNameWithoutExtension(record.RecordName).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return allocatedSkillRecords.Where(x => !x.Equals(record.RecordName, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.Equals(Path.GetDirectoryName(x)?.Replace('\\', '/'), directory, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileNameWithoutExtension(x).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9')
                            .Equals(stem, StringComparison.OrdinalIgnoreCase))
            .Select(x => catalog.Records.GetValueOrDefault(x))
            .FirstOrDefault(x => x is not null && ContributesPersistentStats(x) &&
                                 !RecordClass(x).Contains("Mastery", StringComparison.OrdinalIgnoreCase));
    }

    internal static StatBlock TranslatePersistentStats(GameRecord record, int level, GameRecord? owner = null)
    {
        if (!IsReactiveAura(owner ?? record)) return GameStatTranslator.TranslateAtLevel(record, level);

        // Reactive auras such as Vindictive Flame combine always-on character
        // bonuses with a separate on-hit attack in the same DBR. Only the
        // character fields belong in the persistent loadout: its attack damage
        // must not become flat weapon damage or permanent retaliation.
        var characterFields = new GameRecord
        {
            RecordName = record.RecordName,
            RecordType = record.RecordType,
            NumericValues = new StatBlock(record.NumericValues.Values
                .Where(x => x.Key.StartsWith("character", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase)),
            NumericSeries = record.NumericSeries
                .Where(x => x.Key.StartsWith("character", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
            TextValues = record.TextValues
        };
        return GameStatTranslator.TranslateAtLevel(characterFields, level);
    }

    private static bool IsReactiveAura(GameRecord record) =>
        RecordClass(record).Equals("Skill_OnHitAttackRadius", StringComparison.OrdinalIgnoreCase);

    private static SkillRotationDefinition Fallback(
        CharacterSnapshot character,
        GameDataCatalog catalog,
        CharacterSkill? selectedSkill,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants)
    {
        var (chance, multiplier, flat, attacks) = ResolveWeaponPool(character, catalog, string.Empty, equippedVariants);
        return new SkillRotationDefinition
        {
            MainSkillRecord = selectedSkill?.Record ?? string.Empty,
            MainSkillName = selectedSkill?.Name ?? "默认武器攻击",
            WeaponDamagePercent = 100,
            BaseAttacksPerSecond = CharacterStatEngine.ResolveAnimationProfile(character, catalog)?.UnarmedPanelAttacksPerSecond ?? 1.5,
            WeaponPoolChance = chance,
            WeaponPoolDamageMultiplier = multiplier,
            WeaponPoolFlatDamage = flat,
            WeaponPoolAttacks = attacks,
            UsesAttackSpeed = true,
            IsFallback = true,
            CalculationSummary = selectedSkill is null ? $"默认武器攻击 · 武器池 {chance:0.#}%" : "无可用伤害记录，按默认武器攻击估算"
        };
    }

    private static IReadOnlyList<GameRecord> ResolveDamageRecords(GameRecord main, GameDataCatalog catalog)
    {
        var result = new List<GameRecord> { main };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { main.RecordName };
        foreach (var link in DamageRecordLinks)
        {
            var recordName = main.Text(link);
            if (!string.IsNullOrWhiteSpace(recordName) && seen.Add(recordName) &&
                catalog.Records.TryGetValue(recordName, out var linked))
                result.Add(linked);
        }
        return result;
    }

    private static IReadOnlyList<(GameRecord Record, int Level)> ResolveAllocatedChildRecords(
        CharacterSnapshot character,
        GameDataCatalog catalog,
        string mainRecord)
    {
        var mainStem = Path.GetFileNameWithoutExtension(mainRecord).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        var directory = Path.GetDirectoryName(mainRecord)?.Replace('\\', '/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(mainStem)) return [];

        return character.Skills
            .Where(x => x.Level > 0 && !x.Record.Equals(mainRecord, StringComparison.OrdinalIgnoreCase))
            .Where(x => (Path.GetDirectoryName(x.Record)?.Replace('\\', '/') ?? string.Empty)
                .Equals(directory, StringComparison.OrdinalIgnoreCase))
            .Where(x => Path.GetFileNameWithoutExtension(x.Record).StartsWith(mainStem, StringComparison.OrdinalIgnoreCase))
            .Select(x => (Skill: x, Record: catalog.Records.GetValueOrDefault(x.Record)))
            .Where(x => x.Record is not null &&
                        (RecordClass(x.Record).Contains("Secondary", StringComparison.OrdinalIgnoreCase) ||
                         RecordClass(x.Record).Contains("Modifier", StringComparison.OrdinalIgnoreCase) ||
                         RecordClass(x.Record).Contains("Transmuter", StringComparison.OrdinalIgnoreCase)))
            .Select(x => (x.Record!, Math.Max(1, x.Skill.Level)))
            .ToArray();
    }

    private static (double Chance, double Multiplier, StatBlock FlatDamage, List<WeaponPoolAttackDefinition> Attacks) ResolveWeaponPool(
        CharacterSnapshot character,
        GameDataCatalog catalog,
        string selectedRecord,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants)
    {
        var totalChance = 0d;
        var multiplierBonus = 0d;
        var weightedFlat = new StatBlock();
        var attacks = new List<WeaponPoolAttackDefinition>();
        var dualWieldEquipped = IsDualWieldEquipped(character, equippedVariants);
        var learned = character.Skills.Where(x => x.Level > 0 &&
                     !x.Record.Equals(selectedRecord, StringComparison.OrdinalIgnoreCase) &&
                     !x.Record.Contains("/default/", StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Record, x.Name, Level: Math.Max(1,
                x.Level + ResolveCurrentSkillBonus(character, catalog, x.Record, equippedVariants))))
            .Concat(ResolveGrantedWeaponPoolSkills(character, catalog, equippedVariants));
        foreach (var skill in learned
                     .GroupBy(x => x.Record, StringComparer.OrdinalIgnoreCase)
                     .Select(x => x.OrderByDescending(y => y.Level).First()))
        {
            if (!catalog.Records.TryGetValue(skill.Record, out var record) ||
                !RecordClass(record).Contains("WPAttack", StringComparison.OrdinalIgnoreCase)) continue;
            var level = skill.Level;
            var chance = Math.Max(0, GameStatTranslator.NumericAtLevel(record, "skillChanceWeight", level));
            if (chance <= 0) continue;
            var fraction = chance / 100d;
            totalChance += chance;
            var weaponDamage = GameStatTranslator.NumericAtLevel(record, "weaponDamagePct", level);
            if (weaponDamage <= 0) weaponDamage = 100;
            multiplierBonus += fraction * (weaponDamage / 100d - 1);
            var flat = GameStatTranslator.TranslateAtLevel(record, level);
            var strikes = UsesBothWeapons(record, dualWieldEquipped, catalog) ? 2d : 1d;
            attacks.Add(new WeaponPoolAttackDefinition
            {
                Record = skill.Record,
                Name = skill.Name,
                Chance = chance,
                WeaponDamageMultiplier = weaponDamage / 100d,
                WeaponStrikes = strikes,
                FlatDamage = flat
            });
            foreach (var (key, value) in flat.Values)
                if (key.StartsWith("damage.", StringComparison.OrdinalIgnoreCase))
                    weightedFlat.Add(key, value * fraction);
        }
        return (Math.Min(100, totalChance), Math.Max(0, 1 + multiplierBonus), weightedFlat, attacks);
    }

    private static IEnumerable<(string Record, string Name, int Level)> ResolveGrantedWeaponPoolSkills(
        CharacterSnapshot character,
        GameDataCatalog catalog,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants)
    {
        foreach (var (item, variant) in ResolveEquippedItems(character, equippedVariants))
        foreach (var ownerName in ResolveEquippedRecordNames(item, variant))
        {
            if (!catalog.Records.TryGetValue(ownerName, out var owner)) continue;
            var skillName = owner.Text("itemSkillName");
            if (string.IsNullOrWhiteSpace(skillName) ||
                !catalog.Records.TryGetValue(skillName, out var skill) ||
                !RecordClass(skill).Contains("WPAttack", StringComparison.OrdinalIgnoreCase)) continue;
            var level = Math.Max(1, (int)Math.Round(owner.NumericValues["itemSkillLevelEq"]));
            var name = catalog.Tags.GetValueOrDefault(skill.Text("skillDisplayName"), Path.GetFileNameWithoutExtension(skillName));
            yield return (skillName, name, level);
        }
    }

    private static bool UsesBothWeapons(GameRecord record, bool dualWieldEquipped, GameDataCatalog catalog)
    {
        if (!dualWieldEquipped) return false;
        // A weapon-pool technique replaces the normal 25/25/50 hand selection
        // and executes its dual-wield animation with both equipped weapons.
        // Some localized descriptions omit that implementation detail.
        if (RecordClass(record).Contains("WPAttack", StringComparison.OrdinalIgnoreCase)) return true;
        var descriptionTag = record.Text("skillBaseDescription");
        if (string.IsNullOrWhiteSpace(descriptionTag)) descriptionTag = record.Text("skillDescription");
        var description = catalog.Tags.GetValueOrDefault(descriptionTag, descriptionTag);
        return description.Contains("主手和副手", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("双手武器", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("both weapons", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("main hand and offhand", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDualWieldEquipped(
        CharacterSnapshot character,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants)
    {
        if (!character.CanDualWield) return false;
        if (equippedVariants is not null)
            return equippedVariants.TryGetValue(EquipmentSlot.MainHand, out var main) &&
                   equippedVariants.TryGetValue(EquipmentSlot.OffHand, out var off) &&
                   main.Item.Slot == EquipmentSlot.MainHand && off.Item.Slot == EquipmentSlot.MainHand;

        if (!character.CurrentLoadout.TryGetValue(EquipmentSlot.MainHand, out var mainId) ||
            !character.CurrentLoadout.TryGetValue(EquipmentSlot.OffHand, out var offId))
            return false;
        var mainItem = character.Items.FirstOrDefault(x => x.Id == mainId);
        var offItem = character.Items.FirstOrDefault(x => x.Id == offId);
        return mainItem?.Slot == EquipmentSlot.MainHand && offItem?.Slot == EquipmentSlot.MainHand;
    }

    internal static int ResolveCurrentSkillBonus(
        CharacterSnapshot character,
        GameDataCatalog catalog,
        string skillRecord,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants)
    {
        if (string.IsNullOrWhiteSpace(skillRecord)) return 0;
        var bonus = 0d;
        var masteryRecord = ResolveMasteryRecord(character, skillRecord);
        foreach (var (item, variant) in ResolveEquippedItems(character, equippedVariants))
        {
            foreach (var recordName in ResolveEquippedRecordNames(item, variant))
            {
                if (!catalog.Records.TryGetValue(recordName, out var record)) continue;
                bonus += record.NumericValues["augmentAllLevel"];
                for (var index = 1; index <= 32; index++)
                {
                    if (record.Text($"augmentSkillName{index}").Equals(skillRecord, StringComparison.OrdinalIgnoreCase))
                        bonus += record.NumericValues[$"augmentSkillLevel{index}"];
                    if (!string.IsNullOrWhiteSpace(masteryRecord) &&
                        record.Text($"augmentMasteryName{index}").Equals(masteryRecord, StringComparison.OrdinalIgnoreCase))
                        bonus += record.NumericValues[$"augmentMasteryLevel{index}"];
                }
            }
        }
        return Math.Max(0, (int)Math.Round(bonus));
    }

    private static string ResolveMasteryRecord(CharacterSnapshot character, string skillRecord)
    {
        var directory = Path.GetDirectoryName(skillRecord)?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(directory)) return string.Empty;
        return character.Skills.FirstOrDefault(x =>
            (Path.GetDirectoryName(x.Record)?.Replace('\\', '/') ?? string.Empty)
                .Equals(directory, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(x.Record).StartsWith("_classtraining", StringComparison.OrdinalIgnoreCase))?.Record ?? string.Empty;
    }

    internal static IReadOnlyList<GameRecord> ResolveCurrentSkillModifiers(
        CharacterSnapshot character,
        GameDataCatalog catalog,
        string skillRecord,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants)
    {
        var result = new List<GameRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, variant) in ResolveEquippedItems(character, equippedVariants))
        {
            foreach (var recordName in ResolveEquippedRecordNames(item, variant))
            {
                if (!catalog.Records.TryGetValue(recordName, out var owner)) continue;
                for (var index = 1; index <= 32; index++)
                {
                    if (!owner.Text($"modifiedSkillName{index}").Equals(skillRecord, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var modifierName = owner.Text($"modifierSkillName{index}");
                    if (!string.IsNullOrWhiteSpace(modifierName) && seen.Add(modifierName) &&
                        catalog.Records.TryGetValue(modifierName, out var modifier))
                        result.Add(modifier);
                }
            }
        }
        return result;
    }

    private static IEnumerable<(ItemInstance Item, LoadoutVariant? Variant)> ResolveEquippedItems(
        CharacterSnapshot character,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants)
    {
        if (equippedVariants is not null)
            return equippedVariants.Values
                .GroupBy(x => x.Item.Id, StringComparer.Ordinal)
                .Select(x => (x.First().Item, (LoadoutVariant?)x.First()))
                .ToArray();

        return character.CurrentLoadout.Values
            .Distinct(StringComparer.Ordinal)
            .Select(itemId => character.Items.FirstOrDefault(x => x.Id == itemId))
            .Where(x => x is not null)
            .Select(x => (x!, (LoadoutVariant?)null))
            .ToArray();
    }

    private static IEnumerable<string> ResolveEquippedRecordNames(ItemInstance item, LoadoutVariant? variant)
    {
        var componentRecord = variant?.Component is null
            ? string.Empty
            : GameDataSource.AttachmentRecordId(variant.Component);
        var completionRecord = variant?.Component?.CompletionBonusRecord ?? string.Empty;
        var augmentRecord = variant?.Augment is null
            ? string.Empty
            : GameDataSource.AttachmentRecordId(variant.Augment);
        if (variant is null)
        {
            componentRecord = item.ComponentRecord;
            completionRecord = item.CompletionBonusRecord;
            augmentRecord = item.AugmentRecord;
        }

        return new[]
        {
            item.BaseRecord, item.PrefixRecord, item.SuffixRecord,
            item.ModifierRecord, item.TransmuteRecord, componentRecord,
            completionRecord, augmentRecord
        }.Where(x => !string.IsNullOrWhiteSpace(x));
    }

    private static double FirstPositive(IEnumerable<GameRecord> records, string key, int level)
    {
        foreach (var record in records)
        {
            var value = GameStatTranslator.NumericAtLevel(record, key, level);
            if (value > 0) return value;
        }
        return 0;
    }

    private static bool UsesWeaponAttackSpeed(GameRecord record)
    {
        var @class = RecordClass(record);
        return @class.Contains("Weapon", StringComparison.OrdinalIgnoreCase) ||
               @class.Contains("WPAttack", StringComparison.OrdinalIgnoreCase);
    }

    private static double ResolveActivationRate(GameRecord record, int level)
    {
        if (!RecordClass(record).Contains("ChargedFinale", StringComparison.OrdinalIgnoreCase)) return 1;
        var chargeLevel = GameStatTranslator.NumericAtLevel(record, "skillChargeLevel", level);
        return 1d / Math.Max(1, chargeLevel > 0 ? chargeLevel + 1 : 3);
    }

    private static string RecordClass(GameRecord record) =>
        string.IsNullOrWhiteSpace(record.Text("Class")) ? record.RecordType : record.Text("Class");
}
