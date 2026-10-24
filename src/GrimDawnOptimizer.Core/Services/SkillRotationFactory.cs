using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

public static class SkillRotationFactory
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameDataCatalog, HashSet<string>> GrantedRecordCache = new();

    private static readonly string[] DamageRecordLinks =
    [
        "skillProjectileName", "buffSkillName", "buffSelfSkillName", "buffSelf2SkillName"
    ];

    private static readonly HashSet<string> KnownSkillClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skill_Mastery", "Skill_Passive", "Skill_PassiveDualWieldWeapon", "SkillBuff_Passive",
        "Skill_Modifier", "Skill_ProjectileModifier", "Skill_Transmuter",
        "Skill_BuffSelfToggled", "Skill_BuffRadiusToggled", "Skill_BuffAttackRadiusToggled",
        "Skill_BuffSelfDuration", "Skill_BuffRadius", "Skill_OnHitAttackRadius",
        "Skill_PassiveOnCritBuffSelf", "Skill_PassiveOnLifeBuffSelf", "Skill_PassiveOnHitBuffSelf",
        "Skill_WeaponPool_Default", "Skill_WeaponPool_BasicAttack", "Skill_WeaponPool_ChargedFinale", "Skill_WPAttack_BasicAttack",
        "Skill_AttackProjectile", "Skill_AttackRadius", "Skill_AttackWave", "Skill_AttackSpell",
        "SkillSecondary_AttackRadius", "SkillSecondary_AttackProjectile", "SkillSecondary_AttackWave"
    };

    public static SkillRotationDefinition Build(
        CharacterSnapshot character,
        GameDataCatalog catalog,
        CharacterSkill? selectedSkill,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants = null)
    {
        if (selectedSkill is null || string.IsNullOrWhiteSpace(selectedSkill.Record))
            return Fallback(character, catalog, null, equippedVariants);
        if (!catalog.Records.TryGetValue(selectedSkill.Record, out var mainRecord))
            return Unavailable(selectedSkill, "当前主攻技能记录缺失，无法计算所选技能。");

        var grants = ResolveGrantedSkills(character, catalog, equippedVariants);
        var selectedGrant = grants.FirstOrDefault(x => x.Record.Equals(selectedSkill.Record, StringComparison.OrdinalIgnoreCase));
        var isLearnedMasterySkill = IsIntrinsicLearnedSkill(character, selectedSkill.Record);
        if (!isLearnedMasterySkill && CollectGrantedRecordNames(catalog).Contains(selectedSkill.Record) &&
            (selectedGrant is null || !selectedGrant.MeetsWeaponRequirements))
            return Unavailable(selectedSkill, selectedGrant is null
                ? "当前方案未保留所选主攻技能的授予来源。"
                : "当前方案的武器不满足所选主攻技能要求。");
        if (!MeetsSkillWeaponRequirements(mainRecord, character, equippedVariants))
            return Unavailable(selectedSkill, "当前方案的武器不满足所选主攻技能要求。");

        var baseLevel = selectedGrant is not null && !isLearnedMasterySkill
            ? selectedGrant.Level
            : Math.Max(1, Math.Max(selectedSkill.Level, selectedSkill.DevotionLevel));
        var itemSkillBonus = selectedGrant is not null && !isLearnedMasterySkill
            ? 0 : ResolveCurrentSkillBonus(character, catalog, selectedSkill.Record, equippedVariants);
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
            UnsupportedEffects = FindSkillDiagnostics(mainRecord, catalog, level)
                .Concat(records.Where(x => x.Record.RecordName != mainRecord.RecordName)
                    .SelectMany(x => FindSkillDiagnostics(x.Record, catalog, x.Level, checkClass: false)))
                .Concat(childRecords.Where(x => IsSecondaryAttack(x.Record))
                    .SelectMany(x => FindSkillDiagnostics(x.Record, catalog, x.Level)))
                .Distinct().ToArray(),
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
               IsAttackAura(record) ||
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
        var reactiveAura = IsReactiveAura(owner ?? record);
        var attackAura = IsAttackAura(record) ||
            (IsAttackAura(owner ?? record) && (RecordClass(record).Contains("Modifier", StringComparison.OrdinalIgnoreCase) ||
                RecordClass(record).Contains("Transmuter", StringComparison.OrdinalIgnoreCase)));
        if (!reactiveAura && !attackAura) return GameStatTranslator.TranslateAtLevel(record, level);

        // Reactive auras such as Vindictive Flame combine always-on character
        // bonuses with a separate on-hit attack in the same DBR. Only the
        // character fields belong in the persistent loadout: its attack damage
        // must not become flat weapon damage or permanent retaliation.
        bool AllowedField(string field) => field.StartsWith("character", StringComparison.OrdinalIgnoreCase) ||
            attackAura && (field.StartsWith("defensive", StringComparison.OrdinalIgnoreCase) ||
                           field.StartsWith("retaliation", StringComparison.OrdinalIgnoreCase));
        var characterFields = new GameRecord
        {
            RecordName = record.RecordName,
            RecordType = record.RecordType,
            NumericValues = new StatBlock(record.NumericValues.Values
                .Where(x => AllowedField(x.Key))
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase)),
            NumericSeries = record.NumericSeries
                .Where(x => AllowedField(x.Key))
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
            TextValues = record.TextValues
        };
        return GameStatTranslator.TranslateAtLevel(characterFields, level);
    }

    private static bool IsReactiveAura(GameRecord record) =>
        RecordClass(record).Equals("Skill_OnHitAttackRadius", StringComparison.OrdinalIgnoreCase);

    private static bool IsAttackAura(GameRecord record) =>
        RecordClass(record).Equals("Skill_BuffAttackRadiusToggled", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlySet<string> CollectGrantedRecordNames(GameDataCatalog catalog) =>
        GrantedRecordCache.GetValue(catalog, static data =>
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var owner in data.Records.Values)
            {
                var name = owner.Text("itemSkillName");
                if (string.IsNullOrWhiteSpace(name) || !data.Records.TryGetValue(name, out var skill)) continue;
                foreach (var record in ResolveBuffRecords(skill, data)) names.Add(record.RecordName);
            }
            return names;
        });

    public static int ResolveGrantedSkillLevel(GameRecord owner)
    {
        var level = owner.NumericValues["itemSkillLevelEq"];
        var equation = owner.Text("itemSkillLevelEq");
        if (!string.IsNullOrWhiteSpace(equation) &&
            FormulaEvaluator.TryEvaluate(equation, owner.NumericValues.Values, out var evaluated)) level = evaluated;
        return double.IsFinite(level) ? Math.Max(1, (int)Math.Round(level)) : 1;
    }

    public static bool IsIntrinsicLearnedSkill(CharacterSnapshot character, string record) =>
        IsIntrinsicLearnedSkill(character.Skills.Where(x => x.Level > 0).Select(x => x.Record), record);

    public static bool IsIntrinsicLearnedSkill(IEnumerable<string> learnedRecords, string record)
    {
        var learned = learnedRecords.ToArray();
        if (!learned.Contains(record, StringComparer.OrdinalIgnoreCase)) return false;
        var directory = Path.GetDirectoryName(record)?.Replace('\\', '/');
        return !string.IsNullOrWhiteSpace(directory) && learned.Any(name =>
            string.Equals(Path.GetDirectoryName(name)?.Replace('\\', '/'), directory, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(name).StartsWith("_classtraining", StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<GrantedSkillDefinition> ResolveGrantedSkills(
        CharacterSnapshot character, GameDataCatalog catalog,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants = null) =>
        ResolveGrantedDefinitions(character, catalog, ResolveSkillSourceRecords(character, catalog, equippedVariants), equippedVariants);

    public static IReadOnlyList<GrantedSkillDefinition> ResolveAvailableGrantedSkills(
        CharacterSnapshot character, GameDataCatalog catalog) =>
        ResolveGrantedDefinitions(character, catalog, ResolveSkillSourceRecords(
            character.Items.Select(item => (item, (LoadoutVariant?)null)), catalog)
            .Concat(character.OwnedAttachments.SelectMany(x => new[] { x.RecordId, x.CompletionBonusRecord })
                .Where(catalog.Records.ContainsKey).Select(name => catalog.Records[name])), null);

    private static IReadOnlyList<GrantedSkillDefinition> ResolveGrantedDefinitions(
        CharacterSnapshot character, GameDataCatalog catalog, IEnumerable<GameRecord> owners,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants)
    {
        var result = new List<GrantedSkillDefinition>();
        foreach (var owner in owners)
        {
            var name = owner.Text("itemSkillName");
            if (string.IsNullOrWhiteSpace(name) || !catalog.Records.TryGetValue(name, out var skill)) continue;
            var level = ResolveGrantedSkillLevel(owner);
            var duration = ResolveBuffDuration(skill, catalog, level);
            var controller = owner.Text("itemSkillAutoController");
            var @class = RecordClass(skill);
            var passive = @class.Contains("Passive", StringComparison.OrdinalIgnoreCase) &&
                !@class.Contains("PassiveOn", StringComparison.OrdinalIgnoreCase) && duration <= 0 &&
                string.IsNullOrWhiteSpace(controller);
            var isToggle = (@class.Equals("Skill_BuffSelfToggled", StringComparison.OrdinalIgnoreCase) ||
                            @class.Equals("Skill_BuffRadiusToggled", StringComparison.OrdinalIgnoreCase) ||
                            @class.Equals("Skill_BuffAttackRadiusToggled", StringComparison.OrdinalIgnoreCase)) &&
                           duration <= 0 && string.IsNullOrWhiteSpace(controller);
            var affectsStats = passive || @class.Contains("Buff", StringComparison.OrdinalIgnoreCase) || IsReactiveAura(skill);
            result.Add(new(name, ResolveGrantedDisplayName(character, owner, skill, catalog),
                level, duration, controller, passive, isToggle, affectsStats,
                MeetsSkillWeaponRequirements(skill, character, equippedVariants), [owner.RecordName],
                skill.NumericValues["exclusiveSkill"] > 0));
        }
        return result.GroupBy(x => x.Record, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.Level).First() with
            {
                SourceRecords = group.SelectMany(x => x.SourceRecords).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            }).ToArray();
    }

    private static string ResolveGrantedDisplayName(CharacterSnapshot character, GameRecord owner, GameRecord skill, GameDataCatalog catalog)
    {
        // BuffRadius roots commonly store the actual localized name on their
        // linked buff (Apothecary's Touch and Battle Cry both do this).
        foreach (var record in ResolveBuffRecords(skill, catalog))
            if (catalog.Tags.TryGetValue(record.Text("skillDisplayName"), out var name) && !string.IsNullOrWhiteSpace(name))
                return name;
        var sourceItem = character.Items.FirstOrDefault(item => ResolveEquippedRecordNames(item, null)
            .Contains(owner.RecordName, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(sourceItem?.Name)) return sourceItem.Name + " · 授予增益";
        if (catalog.Tags.TryGetValue(owner.Text("itemNameTag"), out var itemName) && !string.IsNullOrWhiteSpace(itemName))
            return itemName + " · 授予增益";
        return "装备授予增益";
    }

    public static IReadOnlyList<GrantedSkillDefinition> ResolveTemporaryLearnedSkills(
        CharacterSnapshot character, GameDataCatalog catalog,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants = null)
    {
        var grantedRecords = CollectGrantedRecordNames(catalog);
        var result = new List<GrantedSkillDefinition>();
        foreach (var learned in character.Skills.Where(x => x.Level > 0 && x.Enabled &&
            (!grantedRecords.Contains(x.Record) || IsIntrinsicLearnedSkill(character, x.Record))))
        {
            if (!catalog.Records.TryGetValue(learned.Record, out var root)) continue;
            var type = RecordClass(root);
            if ((!type.Contains("Buff", StringComparison.OrdinalIgnoreCase) &&
                 !type.Contains("PassiveOn", StringComparison.OrdinalIgnoreCase)) ||
                type.Contains("Modifier", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("Transmuter", StringComparison.OrdinalIgnoreCase)) continue;
            var level = Math.Max(learned.Level, learned.DevotionLevel) + ResolveCurrentSkillBonus(character, catalog, learned.Record, equippedVariants);
            var duration = ResolveBuffDuration(root, catalog, level);
            if (duration <= 0) continue;
            result.Add(new(learned.Record, learned.Name, level, duration, string.Empty, false, false, true,
                MeetsSkillWeaponRequirements(root, character, equippedVariants), [], root.NumericValues["exclusiveSkill"] > 0));
        }
        return result;
    }

    internal static StatBlock ResolveActiveTemporaryLearnedStats(CharacterSnapshot character, GameDataCatalog catalog,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> equippedVariants) =>
        ResolveLearnedSkillStats(character, catalog, equippedVariants,
            ResolveTemporaryLearnedSkills(character, catalog, equippedVariants).Where(skill =>
                character.ActiveItemSkillRecords.Contains(skill.Record, StringComparer.OrdinalIgnoreCase)));

    internal static StatBlock ResolveQualifiedLearnedStats(CharacterSnapshot character, GameDataCatalog catalog,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> equippedVariants)
    {
        var grantedRecords = CollectGrantedRecordNames(catalog);
        var selected = new List<GrantedSkillDefinition>();
        foreach (var learned in character.Skills.Where(x => x.Level > 0 &&
            (!grantedRecords.Contains(x.Record) || IsIntrinsicLearnedSkill(character, x.Record))))
        {
            if (!catalog.Records.TryGetValue(learned.Record, out var root) || !HasWeaponRequirements(root) ||
                !ContributesPersistentStats(root) || !IsPersistentOwnerActive(root, character)) continue;
            var level = Math.Max(learned.Level, learned.DevotionLevel) + ResolveCurrentSkillBonus(character, catalog, learned.Record, equippedVariants);
            if (ResolveBuffDuration(root, catalog, level) > 0) continue;
            selected.Add(new(learned.Record, learned.Name, level, 0, string.Empty, true, false, true,
                MeetsSkillWeaponRequirements(root, character, equippedVariants), []));
        }
        return ResolveLearnedSkillStats(character, catalog, equippedVariants, selected);
    }

    private static StatBlock ResolveLearnedSkillStats(CharacterSnapshot character, GameDataCatalog catalog,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> equippedVariants, IEnumerable<GrantedSkillDefinition> skills)
    {
        var stats = new StatBlock();
        foreach (var skill in skills)
        {
            if (!skill.MeetsWeaponRequirements) continue;
            var root = catalog.Records[skill.Record];
            var entries = new List<(GameRecord Record, int Level)> { (root, skill.Level) };
            entries.AddRange(ResolveAllocatedChildRecords(character, catalog, skill.Record).Where(x => !IsSecondaryAttack(x.Record))
                .Select(x => (x.Record, x.Level + ResolveCurrentSkillBonus(character, catalog, x.Record.RecordName, equippedVariants))));
            foreach (var entry in entries)
            {
                foreach (var record in ResolveBuffRecords(entry.Record, catalog))
                    stats.Add(TranslatePersistentStats(record, entry.Level, root));
                foreach (var modifier in ResolveCurrentSkillModifiers(character, catalog, entry.Record.RecordName, equippedVariants))
                    stats.Add(TranslatePersistentStats(modifier, 1, root));
            }
        }
        return stats;
    }

    internal static double ResolveBuffDuration(GameRecord root, GameDataCatalog catalog, int level) =>
        ResolveBuffRecords(root, catalog).Max(x => GameStatTranslator.NumericAtLevel(x, "skillActiveDuration", level));

    internal static bool IsKnownSkillClass(GameRecord record) => KnownSkillClasses.Contains(RecordClass(record));

    internal static bool IsNonPersistentAction(GameRecord record) => RecordClass(record) is
        "Skill_Kick" or "Skill_Move" or "Skill_PetAttack" or "Skill_ChargePotion" or "Skill_Evade" or "Skill_PotionModifier";

    internal static IReadOnlyList<UnsupportedEffect> FindSkillDiagnostics(GameRecord root, GameDataCatalog catalog,
        int level, bool checkClass = true, GameRecord? persistentOwner = null)
    {
        var diagnostics = new List<UnsupportedEffect>();
        if (checkClass && !IsKnownSkillClass(root))
            diagnostics.Add(new(root.RecordName, $"技能类型 {RecordClass(root)} 尚未建立完整计算规则。", ["panelDps", "dps", "effectiveHealth"]));
        var pending = new Queue<GameRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(root);
        while (pending.TryDequeue(out var record))
        {
            if (!seen.Add(record.RecordName)) continue;
            var values = record.NumericValues.Clone();
            foreach (var key in record.NumericSeries.Keys) values.Set(key, GameStatTranslator.NumericAtLevel(record, key, level));
            var leveled = new GameRecord { RecordName = record.RecordName, RecordType = record.RecordType, NumericValues = values, TextValues = record.TextValues };
            var fields = GameStatTranslator.FindUnsupportedCombatFields(leveled)
                .Where(x => !x.Equals("skillActiveDuration", StringComparison.OrdinalIgnoreCase) ||
                    !RecordClass(record).Contains("Buff", StringComparison.OrdinalIgnoreCase) &&
                    !RecordClass(root).Contains("PassiveOn", StringComparison.OrdinalIgnoreCase))
                .Where(x => persistentOwner is null || !IsReactiveAura(persistentOwner) || x.StartsWith("character", StringComparison.OrdinalIgnoreCase))
                .Where(x => persistentOwner is null || !IsAttackAura(persistentOwner) ||
                    !IsAttackAura(record) && !RecordClass(record).Contains("Modifier", StringComparison.OrdinalIgnoreCase) ||
                    x.StartsWith("character", StringComparison.OrdinalIgnoreCase) || x.StartsWith("defensive", StringComparison.OrdinalIgnoreCase) ||
                    x.StartsWith("retaliation", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (fields.Length > 0)
                diagnostics.Add(new(record.RecordName, $"技能词条尚未完整计入：{string.Join(", ", fields.Take(6))}", CalculationDiagnostics.AffectedMetrics(fields)));
            foreach (var link in DamageRecordLinks)
            {
                var name = record.Text(link);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (catalog.Records.TryGetValue(name, out var child)) pending.Enqueue(child);
                else diagnostics.Add(new(name, $"技能关联记录缺失：{link}", ["panelDps", "dps", "effectiveHealth"]));
            }
        }
        return diagnostics;
    }

    internal static IReadOnlyList<GameRecord> ResolveBuffRecords(GameRecord root, GameDataCatalog catalog)
    {
        var records = new List<GameRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<GameRecord>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var record))
        {
            if (!seen.Add(record.RecordName)) continue;
            records.Add(record);
            foreach (var field in new[] { "buffSkillName", "buffSelfSkillName", "buffSelf2SkillName" })
                if (catalog.Records.TryGetValue(record.Text(field), out var child)) pending.Enqueue(child);
        }
        return records;
    }

    internal static bool IsPersistentOwnerActive(GameRecord owner, CharacterSnapshot character)
    {
        var saved = character.Skills.FirstOrDefault(x => x.Record.Equals(owner.RecordName, StringComparison.OrdinalIgnoreCase));
        if (owner.NumericValues["exclusiveSkill"] > 0 && saved?.Active != true) return false;
        if (!RecordClass(owner).Contains("Toggled", StringComparison.OrdinalIgnoreCase)) return true;
        // These skills auto-activate when enabled and eligible; the saved Active
        // bit only describes the previous instant, not the next candidate loadout.
        return saved?.Enabled != false;
    }

    public static bool ResolveCanDualWield(CharacterSnapshot character, GameDataCatalog catalog,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants = null)
    {
        // The import-time flag can describe an item that has since been removed.
        // Re-evaluate both learned and granted sources for this candidate.
        if (catalog.Records.Count == 0) return character.CanDualWield;
        var weapons = ResolveEquippedItems(character, equippedVariants).Where(x =>
            x.Item.Slot == EquipmentSlot.MainHand && !x.Item.IsTwoHanded).Select(x => x.Item).ToArray();
        var ranged = weapons.Any(x => x.AttachmentQualifier.StartsWith("ranged", StringComparison.OrdinalIgnoreCase));
        if (ranged && weapons.Any(x => !x.AttachmentQualifier.StartsWith("ranged", StringComparison.OrdinalIgnoreCase)))
            return false;
        bool AllowsWeaponType(GameRecord record) => ranged
            ? record.NumericValues["dualRangedOnly"] > 0 || record.NumericValues["dualRangedOrRanged2hOnly"] > 0 ||
              record.NumericValues["dualRangedOrAllRangedOnly"] > 0
            : record.NumericValues["dualWieldOnly"] > 0;
        var grantedRecords = CollectGrantedRecordNames(catalog);
        if (character.Skills.Any(x => x.Level > 0 && x.Enabled &&
            (!grantedRecords.Contains(x.Record) || IsIntrinsicLearnedSkill(character, x.Record)) &&
            catalog.Records.TryGetValue(x.Record, out var skill) &&
            AllowsWeaponType(skill) &&
            MeetsSkillWeaponRequirements(skill, character, equippedVariants))) return true;
        return ResolveGrantedSkills(character, catalog, equippedVariants).Any(x =>
            x.MeetsWeaponRequirements && catalog.Records.TryGetValue(x.Record, out var skill) && AllowsWeaponType(skill));
    }

    internal static bool HasWeaponRequirements(GameRecord skill) =>
        GameDataSource.AttachmentQualifierKeys.Any(key => skill.NumericValues[key] > 0.5) ||
        new[] { "dualWieldOnly", "dualRangedOnly", "dualRangedOrRanged2hOnly", "dualRangedOrAllRangedOnly" }
            .Any(key => skill.NumericValues[key] > 0.5);

    internal static bool MeetsSkillWeaponRequirements(GameRecord skill, CharacterSnapshot character,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants)
    {
        var hands = ResolveEquippedItems(character, equippedVariants).Where(x => x.Item.Slot is EquipmentSlot.MainHand or EquipmentSlot.OffHand)
            .Select(x => x.Item).ToArray();
        var weapons = hands.Where(x => x.Slot == EquipmentSlot.MainHand).ToArray();
        var dualMelee = weapons.Length == 2 && weapons.All(x => !x.IsTwoHanded && !x.AttachmentQualifier.StartsWith("ranged", StringComparison.OrdinalIgnoreCase));
        var dualRanged = weapons.Length == 2 && weapons.All(x => !x.IsTwoHanded && x.AttachmentQualifier.Equals("ranged1h", StringComparison.OrdinalIgnoreCase));
        var ranged = weapons.Any(x => x.AttachmentQualifier.StartsWith("ranged", StringComparison.OrdinalIgnoreCase));
        var ranged2h = weapons.Any(x => x.AttachmentQualifier.Equals("ranged2h", StringComparison.OrdinalIgnoreCase));
        var hasDualRule = skill.NumericValues["dualWieldOnly"] > 0 || skill.NumericValues["dualRangedOnly"] > 0 ||
            skill.NumericValues["dualRangedOrRanged2hOnly"] > 0 || skill.NumericValues["dualRangedOrAllRangedOnly"] > 0;
        if (hasDualRule && !(skill.NumericValues["dualWieldOnly"] > 0 && dualMelee ||
            skill.NumericValues["dualRangedOnly"] > 0 && dualRanged ||
            skill.NumericValues["dualRangedOrRanged2hOnly"] > 0 && (dualRanged || ranged2h) ||
            skill.NumericValues["dualRangedOrAllRangedOnly"] > 0 && ranged)) return false;
        var required = GameDataSource.AttachmentQualifierKeys.Where(key => skill.NumericValues[key] > 0.5)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (required.Count == 0) return true;
        return hands.Any(x => required.Contains(x.AttachmentQualifier));
    }

    private static IEnumerable<GameRecord> ResolveSkillSourceRecords(CharacterSnapshot character, GameDataCatalog catalog,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants) =>
        ResolveSkillSourceRecords(ResolveEquippedItems(character, equippedVariants), catalog);

    private static IEnumerable<GameRecord> ResolveSkillSourceRecords(
        IEnumerable<(ItemInstance Item, LoadoutVariant? Variant)> equippedItems, GameDataCatalog catalog)
    {
        var equipped = equippedItems.ToArray();
        foreach (var (item, variant) in equipped)
            foreach (var name in ResolveEquippedRecordNames(item, variant).Distinct(StringComparer.OrdinalIgnoreCase))
                if (catalog.Records.TryGetValue(name, out var record)) yield return record;
        foreach (var group in equipped.Where(x => !string.IsNullOrWhiteSpace(x.Item.SetId)).GroupBy(x => x.Item.SetId, StringComparer.OrdinalIgnoreCase))
        {
            if (!catalog.Records.TryGetValue(group.Key, out var set)) continue;
            for (var pieces = 2; pieces <= group.Count(); pieces++)
                if (catalog.Records.TryGetValue(set.Text($"setBonus{pieces}"), out var bonus)) yield return bonus;
        }
    }

    public static SkillRotationDefinition BuildSelectedRecord(
        CharacterSnapshot character, GameDataCatalog catalog, string selectedRecord, string selectedName,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants = null)
    {
        if (string.IsNullOrWhiteSpace(selectedRecord)) return Build(character, catalog, null, equippedVariants);
        var learned = character.Skills.FirstOrDefault(skill =>
            skill.Record.Equals(selectedRecord, StringComparison.OrdinalIgnoreCase) &&
            (skill.Level > 0 || skill.DevotionLevel > 0));
        if (learned is not null) return Build(character, catalog, learned, equippedVariants);

        // Item-granted attacks may exist only in the UI selection, not the save's
        // learned-skills array. Build resolves their level from each candidate's
        // actual owner, including components and set thresholds.
        var selected = new CharacterSkill(selectedRecord,
            string.IsNullOrWhiteSpace(selectedName) ? Path.GetFileNameWithoutExtension(selectedRecord) : selectedName,
            1, 0, true, string.Empty, string.Empty);
        if (!catalog.Records.ContainsKey(selectedRecord) || CollectGrantedRecordNames(catalog).Contains(selectedRecord))
            return Build(character, catalog, selected, equippedVariants);
        return Unavailable(selected, "当前角色未学习所选主攻技能，且无法确认装备授予来源。");
    }

    private static SkillRotationDefinition Unavailable(CharacterSkill selectedSkill, string reason) => new()
    {
        MainSkillRecord = selectedSkill.Record,
        MainSkillName = selectedSkill.Name,
        IsAvailable = false,
        UnavailableReason = reason,
        WeaponDamagePercent = 0,
        IsFallback = true,
        CalculationSummary = reason,
        UnsupportedEffects = [new(selectedSkill.Record, reason, ["panelDps", "dps"])]
    };

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
        var dualWieldEquipped = IsDualWieldEquipped(character, catalog, equippedVariants);
        var learned = character.Skills.Where(x => x.Level > 0 &&
                     (!CollectGrantedRecordNames(catalog).Contains(x.Record) || IsIntrinsicLearnedSkill(character, x.Record)) &&
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
        foreach (var grant in ResolveGrantedSkills(character, catalog, equippedVariants))
        {
            if (!grant.MeetsWeaponRequirements ||
                !catalog.Records.TryGetValue(grant.Record, out var skill) ||
                !RecordClass(skill).Contains("WPAttack", StringComparison.OrdinalIgnoreCase)) continue;
            yield return (grant.Record, grant.Name, grant.Level);
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
        GameDataCatalog catalog,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant>? equippedVariants)
    {
        if (!ResolveCanDualWield(character, catalog, equippedVariants)) return false;
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
        foreach (var record in ResolveSkillSourceRecords(character, catalog, equippedVariants))
        {
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
        foreach (var owner in ResolveSkillSourceRecords(character, catalog, equippedVariants))
        {
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

public sealed record GrantedSkillDefinition(
    string Record, string Name, int Level, double DurationSeconds, string AutoControllerRecord,
    bool IsPassive, bool IsToggle, bool CanAffectPanelStats, bool MeetsWeaponRequirements,
    IReadOnlyList<string> SourceRecords, bool IsExclusive = false);
