using GrimDawnOptimizer.Core.Contracts;
using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

public static class StatKeys
{
    public const string WeaponArmorPiercing = "weaponArmorPiercing";
    public static readonly string[] DamageTypes =
    [
        "physical", "pierce", "fire", "cold", "lightning", "acid", "vitality", "aether", "chaos", "bleeding"
    ];

    public static readonly string[] IncomingDamageTypes = DamageTypes;

    public static string Resistance(string damageType) => $"{damageType}Resistance";
    public static string MaxResistance(string damageType) => $"{damageType}MaxResistance";
    public static string DamageFlat(string damageType) => $"damage.{damageType}.flat";
    public static string DamagePercent(string damageType) => $"damage.{damageType}.percent";
    public static string DamageDot(string damageType) => $"damage.{damageType}.dot";
    public static string DamageDotPercent(string damageType) => $"damage.{damageType}.dotPercent";
    public static string WeaponDamage(string damageType) => $"weaponDamage.{damageType}";
    public static string RetaliationFlat(string damageType) => $"retaliation.{damageType}.flat";
    public static string RetaliationPercent(string damageType) => $"retaliation.{damageType}.percent";
    public static string RetaliationDot(string damageType) => $"retaliation.{damageType}.dot";
    public static string RetaliationDotTotal(string damageType) => $"retaliation.{damageType}.dotTotal";
    public static string RetaliationDotPercent(string damageType) => $"retaliation.{damageType}.dotPercent";
}

public sealed class CharacterStatEngine : ICharacterStatEngine
{
    public CalculatedLoadout Calculate(
        CharacterSnapshot character,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots,
        GameDataCatalog catalog)
    {
        var stats = character.BaselineStats.Clone();
        var effects = new List<TriggeredEffect>();
        var unsupported = new List<UnsupportedEffect>(character.ImportDiagnostics);
        var errors = new List<string>();
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        var canDualWield = SkillRotationFactory.ResolveCanDualWield(character, catalog, slots);

        foreach (var (targetSlot, variant) in slots)
        {
            if (!IsCompatible(targetSlot, variant.Item.Slot, canDualWield) ||
                targetSlot == EquipmentSlot.OffHand && variant.Item.IsTwoHanded)
                errors.Add($"{variant.Item.Name} 不能装备在 {targetSlot}");

            if (!itemIds.Add(variant.Item.Id))
                errors.Add($"同一实物被重复装备：{variant.Item.Name}");

            stats.Add(variant.CombinedStats);
            effects.AddRange(variant.Effects);
            unsupported.AddRange(variant.UnsupportedEffects);
        }

        foreach (var (setId, count) in ItemSetRules.CountPieces(slots.Values.Select(variant => variant.Item)))
        {
            if (!catalog.SetBonuses.TryGetValue(setId, out var bonuses)) continue;
            foreach (var (requiredPieces, bonus) in bonuses)
                if (count >= requiredPieces)
                {
                    stats.Add(bonus);
                    if (catalog.SetBonusEffects.TryGetValue(setId, out var effectsByPieces))
                        effects.AddRange(effectsByPieces.GetValueOrDefault(requiredPieces) ?? []);
                    if (catalog.SetBonusUnsupportedEffects.TryGetValue(setId, out var unsupportedByPieces))
                        unsupported.AddRange(unsupportedByPieces.GetValueOrDefault(requiredPieces) ?? []);
                }
        }

        ApplyEquipmentSkillBonuses(stats, character, slots, catalog);
        ApplyGrantedSkillStats(stats, character, slots, catalog, unsupported);
        stats.Add(SkillRotationFactory.ResolveActiveTemporaryLearnedStats(character, catalog, slots));
        stats.Add(SkillRotationFactory.ResolveQualifiedLearnedStats(character, catalog, slots));
        AddSkillAndAnimationDiagnostics(unsupported, character, slots, catalog);

        ApplyPercentModifier(stats, "physique");
        ApplyPercentModifier(stats, "cunning");
        ApplyPercentModifier(stats, "spirit");
        PlayerAttributeRules.ApplyResourceBonuses(stats, catalog);
        ApplyPercentModifier(stats, "health");
        ApplyPercentModifier(stats, "energy");
        ApplyPercentModifier(stats, "healthRegen");
        ApplyPercentModifier(stats, "energyRegen");
        PlayerAttributeRules.ApplyRegeneration(stats, catalog, unsupported);
        ArmorRules.Apply(stats, slots, catalog);
        ResolveShieldStats(stats, slots, catalog);
        if (stats["usePlayerAbilityFormula"] > 0)
            ApplyPlayerAbilityFormula(stats);
        else
        {
            ApplyPercentModifier(stats, "offensiveAbility");
            ApplyPercentModifier(stats, "defensiveAbility");
        }

        ResolveEquippedWeaponDamage(stats, slots, character, catalog);

        PlayerAttributeRules.ApplyDamageBonuses(stats, catalog);

        var requirementState = ResolveRequirementState(character, slots, catalog);
        var requirementStats = requirementState.Stats;
        var finalPhysique = AttributeValue(requirementStats, "physique", character.Physique);
        var finalCunning = AttributeValue(requirementStats, "cunning", character.Cunning);
        var finalSpirit = AttributeValue(requirementStats, "spirit", character.Spirit);

        foreach (var variant in slots.Values)
        {
            var item = variant.Item;
            if (item.LevelRequirement > character.Level)
                errors.Add($"{item.Name} 需要等级 {item.LevelRequirement}（当前 {character.Level}）");

            if (!requirementState.BlockedItemIds.Contains(item.Id)) continue;

            var requiredPhysique = ReducedRequirement(item.PhysiqueRequirement, RequirementReduction(requirementStats, item, "physique"));
            var requiredCunning = ReducedRequirement(item.CunningRequirement, RequirementReduction(requirementStats, item, "cunning"));
            var requiredSpirit = ReducedRequirement(item.SpiritRequirement, RequirementReduction(requirementStats, item, "spirit"));
            if (requiredPhysique > finalPhysique)
                errors.Add($"{item.Name} 需要体格 {requiredPhysique}（当前 {DisplayAttribute(finalPhysique)}）");
            if (requiredCunning > finalCunning)
                errors.Add($"{item.Name} 需要灵巧 {requiredCunning}（当前 {DisplayAttribute(finalCunning)}）");
            if (requiredSpirit > finalSpirit)
                errors.Add($"{item.Name} 需要精神 {requiredSpirit}（当前 {DisplayAttribute(finalSpirit)}）");
        }

        if (slots.TryGetValue(EquipmentSlot.MainHand, out var mainHand) &&
            mainHand.Item.IsTwoHanded && slots.ContainsKey(EquipmentSlot.OffHand))
            errors.Add("双手武器不能与副手同时装备");

        return new CalculatedLoadout
        {
            Slots = new Dictionary<EquipmentSlot, LoadoutVariant>(slots),
            Stats = stats,
            Effects = effects,
            UnsupportedEffects = unsupported.Distinct().ToArray(),
            ValidationErrors = errors.Distinct().ToArray()
        };
    }

    public static bool IsCompatible(EquipmentSlot target, EquipmentSlot itemSlot, bool canDualWield = false) => target switch
    {
        EquipmentSlot.RingLeft or EquipmentSlot.RingRight => itemSlot == EquipmentSlot.Ring,
        EquipmentSlot.OffHand => itemSlot == EquipmentSlot.OffHand || canDualWield && itemSlot == EquipmentSlot.MainHand,
        _ => target == itemSlot
    };

    private static void ApplyEquipmentSkillBonuses(
        StatBlock stats,
        CharacterSnapshot character,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots,
        GameDataCatalog catalog)
    {
        foreach (var skill in character.Skills.Where(x => x.Level > 0))
        {
            if (!catalog.Records.TryGetValue(skill.Record, out var record)) continue;
            if (SkillRotationFactory.CollectGrantedRecordNames(catalog).Contains(skill.Record) &&
                !SkillRotationFactory.IsIntrinsicLearnedSkill(character, skill.Record)) continue;
            var owner = SkillRotationFactory.ResolvePersistentSkillOwner(record,
                character.Skills.Where(x => x.Level > 0).Select(x => x.Record), catalog);
            if (owner is null || !SkillRotationFactory.IsPersistentOwnerActive(owner, character)) continue;
            var ownerLevel = character.Skills.FirstOrDefault(x => x.Record.Equals(owner.RecordName, StringComparison.OrdinalIgnoreCase))?.Level ?? skill.Level;
            if (SkillRotationFactory.HasWeaponRequirements(owner) ||
                SkillRotationFactory.ResolveBuffDuration(owner, catalog, ownerLevel) > 0) continue;
            var @class = string.IsNullOrWhiteSpace(record.Text("Class")) ? record.RecordType : record.Text("Class");
            if (@class.Contains("Mastery", StringComparison.OrdinalIgnoreCase)) continue;

            var bonus = SkillRotationFactory.ResolveCurrentSkillBonus(character, catalog, skill.Record, slots);
            if (bonus > 0)
            {
                var baseLevel = Math.Max(skill.Level, skill.DevotionLevel);
                var effectiveLevel = baseLevel + bonus;
                AddLevelDelta(record, baseLevel, effectiveLevel, owner);
                foreach (var childName in new[]
                         {
                             record.Text("buffSkillName"), record.Text("buffSelfSkillName"),
                             record.Text("buffSelf2SkillName")
                         }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (catalog.Records.TryGetValue(childName, out var child))
                        AddLevelDelta(child, baseLevel, effectiveLevel, owner);
            }

            // Item skill modifiers are not ordinary item stats: the game applies
            // them only while their target skill is active. Direct attack modifiers
            // are folded into SkillRotationFactory.Build; modifiers for toggled and
            // passive skills must instead become persistent loadout stats here.
            foreach (var modifier in SkillRotationFactory.ResolveCurrentSkillModifiers(
                         character, catalog, skill.Record, slots))
                stats.Add(SkillRotationFactory.TranslatePersistentStats(modifier, 1, owner));
        }
        return;

        void AddLevelDelta(GameRecord record, int baseLevel, int effectiveLevel, GameRecord owner)
        {
            var improved = SkillRotationFactory.TranslatePersistentStats(record, effectiveLevel, owner);
            var baseline = SkillRotationFactory.TranslatePersistentStats(record, baseLevel, owner);
            improved.Add(baseline, -1);
            stats.Add(improved);
        }
    }

    private static void ApplyGrantedSkillStats(StatBlock stats, CharacterSnapshot character,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots, GameDataCatalog catalog,
        List<UnsupportedEffect> unsupported)
    {
        var grants = SkillRotationFactory.ResolveGrantedSkills(character, catalog, slots);
        var exclusiveRecords = grants.Where(x => x.IsExclusive && x.MeetsWeaponRequirements).Select(x => x.Record)
            .Concat(character.Skills.Where(x => x.Level > 0 && x.Enabled && x.Active == true &&
                catalog.Records.TryGetValue(x.Record, out var skill) && skill.NumericValues["exclusiveSkill"] > 0)
                .Select(x => x.Record)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var grant in grants)
        {
            if (!grant.CanAffectPanelStats || !grant.MeetsWeaponRequirements ||
                !catalog.Records.TryGetValue(grant.Record, out var root)) continue;
            if (SkillRotationFactory.IsIntrinsicLearnedSkill(character, grant.Record)) continue;
            if (grant.IsExclusive && exclusiveRecords.Length > 1)
            {
                unsupported.Add(new UnsupportedEffect(grant.Record,
                    "候选装备引入多个互斥技能；尚未还原游戏的替换优先级，未叠加该授予效果。",
                    [CalculationDiagnostics.PanelDps, CalculationDiagnostics.CombatDps, CalculationDiagnostics.Survival]));
                continue;
            }
            if (!grant.IsPassive && !grant.IsToggle && !character.ActiveItemSkillRecords.Any(name =>
                    name.Equals(grant.Record, StringComparison.OrdinalIgnoreCase))) continue;
            AddActiveUpkeepDiagnostics(unsupported, root, catalog, grant.Level);
            foreach (var record in SkillRotationFactory.ResolveBuffRecords(root, catalog))
                stats.Add(SkillRotationFactory.TranslatePersistentStats(record, grant.Level, root));
            foreach (var modifier in SkillRotationFactory.ResolveCurrentSkillModifiers(character, catalog, grant.Record, slots))
            {
                AddActiveUpkeepDiagnostics(unsupported, modifier, catalog, 1);
                stats.Add(SkillRotationFactory.TranslatePersistentStats(modifier, 1, root));
            }
        }
    }

    private static int ReducedRequirement(int requirement, double reductionPercent)
    {
        if (requirement <= 0) return 0;
        var multiplier = Math.Max(0, 1 - Math.Clamp(reductionPercent, 0, 100) / 100d);
        return (int)Math.Ceiling(requirement * multiplier - 1e-9);
    }

    private static void AddSkillAndAnimationDiagnostics(List<UnsupportedEffect> diagnostics, CharacterSnapshot character,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots, GameDataCatalog catalog)
    {
        var playerName = character.IsFemale ? "records/creatures/pc/femalepc01.dbr" : "records/creatures/pc/malepc01.dbr";
        if (catalog.Records.ContainsKey(playerName) || character.BaselineStats["usePlayerAttributeFormula"] > 0)
        {
            slots.TryGetValue(EquipmentSlot.MainHand, out var main);
            slots.TryGetValue(EquipmentSlot.OffHand, out var off);
            var prefix = ResolveWeaponAnimationPrefix(main?.Item, off?.Item.Slot == EquipmentSlot.MainHand);
            if (ResolveAnimationProfile(character, catalog)?.GetAttackAnimationRatio(prefix) is not > 0)
                diagnostics.Add(new(playerName, $"缺少完整的 {prefix} 攻击动画时长，攻击频率正在使用估算值。", ["panelDps", "dps"]));
        }

        var granted = SkillRotationFactory.CollectGrantedRecordNames(catalog);
        var allocated = character.Skills.Where(x => x.Level > 0 &&
            (!granted.Contains(x.Record) || SkillRotationFactory.IsIntrinsicLearnedSkill(character, x.Record))).ToArray();
        var exclusive = allocated.Where(x => x.Enabled && catalog.Records.TryGetValue(x.Record, out var record) &&
            record.NumericValues["exclusiveSkill"] > 0 && SkillRotationFactory.MeetsSkillWeaponRequirements(record, character, slots)).ToArray();
        if (exclusive.Length > 0 && !exclusive.Any(x => x.Active == true))
            diagnostics.Add(new(exclusive[0].Record,
                "已学习的互斥技能没有已确认的激活项（exclusiveSkill / Active）；无法从存档确定重启后的自动激活结果，暂未计入这些增益。",
                ["panelDps", "dps", "effectiveHealth"]));
        foreach (var skill in allocated)
        {
            if (!catalog.Records.TryGetValue(skill.Record, out var record))
            {
                diagnostics.Add(new(skill.Record, "已学习技能的游戏记录缺失，无法完整重算属性。", ["panelDps", "dps", "effectiveHealth"]));
                continue;
            }
            if (SkillRotationFactory.IsNonPersistentAction(record)) continue;
            if (!SkillRotationFactory.IsKnownSkillClass(record))
            {
                diagnostics.AddRange(SkillRotationFactory.FindSkillDiagnostics(record, catalog, skill.Level));
                continue;
            }
            var owner = SkillRotationFactory.ResolvePersistentSkillOwner(record, allocated.Select(x => x.Record), catalog);
            if (owner is null || !SkillRotationFactory.IsPersistentOwnerActive(owner, character)) continue;
            if (!SkillRotationFactory.MeetsSkillWeaponRequirements(owner, character, slots)) continue;
            var ownerLevel = allocated.FirstOrDefault(x => x.Record.Equals(owner.RecordName, StringComparison.OrdinalIgnoreCase))?.Level ?? skill.Level;
            if (SkillRotationFactory.ResolveBuffDuration(owner, catalog, ownerLevel) > 0 &&
                !character.ActiveItemSkillRecords.Contains(owner.RecordName, StringComparer.OrdinalIgnoreCase)) continue;
            var level = Math.Max(skill.Level, skill.DevotionLevel) + SkillRotationFactory.ResolveCurrentSkillBonus(character, catalog, skill.Record, slots);
            AddActiveUpkeepDiagnostics(diagnostics, record, catalog, level);
            foreach (var modifier in SkillRotationFactory.ResolveCurrentSkillModifiers(character, catalog, skill.Record, slots))
                AddActiveUpkeepDiagnostics(diagnostics, modifier, catalog, 1);
            diagnostics.AddRange(SkillRotationFactory.FindSkillDiagnostics(record, catalog, level, persistentOwner: owner));
        }
    }

    private static void AddActiveUpkeepDiagnostics(List<UnsupportedEffect> diagnostics,
        GameRecord root, GameDataCatalog catalog, int level)
    {
        // The UI subtracts SkillManager::GetActiveManaCost after the resource
        // equation. Only flag a source that is actually active in this candidate;
        // per-use attack costs and disabled/removed buffs are different cases.
        foreach (var record in SkillRotationFactory.ResolveBuffRecords(root, catalog))
            if (GameStatTranslator.NumericAtLevel(record, "skillActiveManaCost", level) > 0)
                diagnostics.Add(new(record.RecordName,
                    "该已生效增益的持续能量消耗（skillActiveManaCost）尚未从回复与实战能量预算中扣除；当前回复为扣除持续消耗前的数值。",
                    [CalculationDiagnostics.CombatDps]));
    }

    private static RequirementState ResolveRequirementState(
        CharacterSnapshot character,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots,
        GameDataCatalog catalog)
    {
        var currentIds = character.CurrentLoadout.Values.ToHashSet(StringComparer.Ordinal);
        var activeIds = slots.Values.Where(x => currentIds.Contains(x.Item.Id))
            .Select(x => x.Item.Id).ToHashSet(StringComparer.Ordinal);
        StatBlock requirementStats;

        while (true)
        {
            requirementStats = BuildRequirementStats(character, slots.Values, activeIds, catalog);
            var newlyWearable = slots.Values.Where(x => !activeIds.Contains(x.Item.Id))
                .Where(x => x.Item.LevelRequirement <= character.Level)
                .Where(x => MeetsAttributeRequirements(character, requirementStats, x.Item))
                .Select(x => x.Item.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (newlyWearable.Length == 0) break;
            foreach (var itemId in newlyWearable) activeIds.Add(itemId);
        }

        requirementStats = BuildRequirementStats(character, slots.Values, activeIds, catalog);
        var blocked = slots.Values.Select(x => x.Item.Id)
            .Where(x => !activeIds.Contains(x)).ToHashSet(StringComparer.Ordinal);
        return new RequirementState(requirementStats, blocked);
    }

    private static StatBlock BuildRequirementStats(
        CharacterSnapshot character,
        IEnumerable<LoadoutVariant> variants,
        IReadOnlySet<string> activeItemIds,
        GameDataCatalog catalog)
    {
        var stats = character.BaselineStats.Clone();
        var activeVariants = variants.Where(x => activeItemIds.Contains(x.Item.Id)).ToArray();
        foreach (var variant in activeVariants)
        {
            // Deliberately use only the physical item's stats. Components,
            // augments and completion bonuses cannot unlock equipment.
            stats.Add(variant.Item.Stats);
        }

        foreach (var (setId, count) in ItemSetRules.CountPieces(activeVariants.Select(variant => variant.Item)))
        {
            if (!catalog.SetBonuses.TryGetValue(setId, out var bonuses)) continue;
            foreach (var (requiredPieces, bonus) in bonuses)
                if (count >= requiredPieces) stats.Add(bonus);
        }

        ApplyPercentModifier(stats, "physique");
        ApplyPercentModifier(stats, "cunning");
        ApplyPercentModifier(stats, "spirit");
        return stats;
    }

    private static bool MeetsAttributeRequirements(
        CharacterSnapshot character,
        StatBlock stats,
        ItemInstance item) =>
        ReducedRequirement(item.PhysiqueRequirement, RequirementReduction(stats, item, "physique")) <=
        AttributeValue(stats, "physique", character.Physique) &&
        ReducedRequirement(item.CunningRequirement, RequirementReduction(stats, item, "cunning")) <=
        AttributeValue(stats, "cunning", character.Cunning) &&
        ReducedRequirement(item.SpiritRequirement, RequirementReduction(stats, item, "spirit")) <=
        AttributeValue(stats, "spirit", character.Spirit);

    private static double AttributeValue(StatBlock stats, string key, int fallback) =>
        stats[key] > 0 ? stats[key] : fallback;

    private static double RequirementReduction(StatBlock stats, ItemInstance item, string attribute)
    {
        var reduction = stats["requirementReduction.global"];
        var isWeapon = item.Slot == EquipmentSlot.MainHand;
        var isRanged = item.AttachmentQualifier.StartsWith("ranged", StringComparison.OrdinalIgnoreCase);
        var isMelee = isWeapon && !isRanged &&
                      !item.AttachmentQualifier.Equals("staff", StringComparison.OrdinalIgnoreCase);

        switch (attribute)
        {
            case "physique":
                if (item.Slot is EquipmentSlot.Head or EquipmentSlot.Shoulders or EquipmentSlot.Chest or
                    EquipmentSlot.Hands or EquipmentSlot.Legs or EquipmentSlot.Feet or EquipmentSlot.Waist)
                    reduction += stats["requirementReduction.armorPhysique"];
                if (isWeapon) reduction += stats["requirementReduction.weaponPhysique"];
                if (isMelee) reduction += stats["requirementReduction.meleePhysique"];
                if (item.Slot == EquipmentSlot.OffHand &&
                    item.AttachmentQualifier.Equals("shield", StringComparison.OrdinalIgnoreCase))
                    reduction += stats["requirementReduction.shieldPhysique"];
                break;
            case "cunning":
                if (isRanged) reduction += stats["requirementReduction.rangedCunning"];
                if (isMelee) reduction += stats["requirementReduction.meleeCunning"];
                break;
            case "spirit":
                if (item.Slot is EquipmentSlot.Ring or EquipmentSlot.Amulet)
                    reduction += stats["requirementReduction.jewelrySpirit"];
                if (item.Slot is EquipmentSlot.MainHand or EquipmentSlot.OffHand)
                    reduction += stats["requirementReduction.weaponSpirit"];
                if (isMelee) reduction += stats["requirementReduction.meleeSpirit"];
                break;
        }

        return reduction;
    }

    private static int DisplayAttribute(double value) => (int)Math.Floor(value + 1e-9);

    private sealed record RequirementState(StatBlock Stats, IReadOnlySet<string> BlockedItemIds);

    private static void ResolveEquippedWeaponDamage(
        StatBlock stats,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots,
        CharacterSnapshot character,
        GameDataCatalog catalog)
    {
        slots.TryGetValue(EquipmentSlot.MainHand, out var mainHand);
        slots.TryGetValue(EquipmentSlot.OffHand, out var offHand);
        var offHandIsWeapon = offHand?.Item.Slot == EquipmentSlot.MainHand;
        var prefix = ResolveWeaponAnimationPrefix(mainHand?.Item, offHandIsWeapon);
        var weaponBaseSpeed = stats["weaponBaseAttackSpeed"];
        // Character::GetAttackSpeed halves the base-speed sum only for dual
        // melee weapons. Dual pistols retain both weapons' full base modifiers.
        if (prefix == "dHanded")
            weaponBaseSpeed *= catalog.Records.GetValueOrDefault("records/game/gameengine.dbr")?
                .NumericValues.Values.GetValueOrDefault("dwWeaponSpeedFactor", 0.5) ?? 0.5;
        stats.Set("weaponBaseAttackSpeed", weaponBaseSpeed);
        stats.Set("weaponAttackSpeedFactor", Math.Max(0, 1 + weaponBaseSpeed));
        stats.Set("weaponAttackAnimationRatio", ResolveAnimationProfile(character, catalog)?
            .GetAttackAnimationRatio(prefix) ?? 1);
        stats.Set("averageWeaponStrikesPerAction", offHandIsWeapon ? 1.5 : 1);
        stats.Set("dualWieldWeapon", offHandIsWeapon ? 1 : 0);
        foreach (var damageType in StatKeys.DamageTypes)
        {
            var key = StatKeys.WeaponDamage(damageType);
            var mainDamage = mainHand?.CombinedStats[key] ?? 0;
            var offDamage = offHandIsWeapon ? offHand!.CombinedStats[key] : 0;
            var nonWeaponContribution = stats[key] - mainDamage - offDamage;
            var equippedDamage = offHandIsWeapon ? (mainDamage + offDamage) / 2d : mainDamage;
            stats.Set(key, nonWeaponContribution + equippedDamage);

            var flatKey = StatKeys.DamageFlat(damageType);
            var mainFlat = mainHand?.CombinedStats[flatKey] ?? 0;
            var offFlat = offHandIsWeapon ? offHand!.CombinedStats[flatKey] : 0;
            // A secondary collector receiving both weapon IDs sums the local
            // weapon payloads, while character-wide flat bonuses enter once.
            // Preserve the difference from the primary attack's average so the
            // simulator can use that collector without doubling shared bonuses.
            stats.Set($"secondaryWeaponDamageCorrection.{damageType}",
                offHandIsWeapon ? (mainDamage + offDamage + mainFlat + offFlat) / 2d : 0);

            // Flat damage on a weapon (including its component and augment) belongs
            // only to that hand. Non-weapon flat bonuses are shared by both hands.
            // Keep each hand's original values in Slots; only this aggregate is averaged.
            // Percent bonuses and equipment conversion remain global.
            if (offHandIsWeapon)
            {
                stats.Add(flatKey, -(mainFlat + offFlat) / 2d);
            }
        }
    }

    internal static AnimationSpeedProfile? ResolveAnimationProfile(CharacterSnapshot character, GameDataCatalog catalog)
    {
        var playerRecord = character.IsFemale ? "records/creatures/pc/femalepc01.dbr" : "records/creatures/pc/malepc01.dbr";
        var table = catalog.Records.GetValueOrDefault(playerRecord)?.Text("charAnimationTableName");
        return !string.IsNullOrWhiteSpace(table) ? catalog.AnimationProfiles.GetValueOrDefault(table) : null;
    }

    private static string ResolveWeaponAnimationPrefix(ItemInstance? mainHand, bool dualWield)
    {
        if (mainHand is null) return "unarmed";
        var qualifier = mainHand.AttachmentQualifier;
        if (dualWield) return qualifier.StartsWith("ranged", StringComparison.OrdinalIgnoreCase) ? "dualRanged" : "dHanded";
        return qualifier.ToLowerInvariant() switch
        {
            "ranged1h" => "ranged1h", "ranged2h" => "ranged2h", "axe2h" => "axe2h",
            "mace2h" => "mace2h", "sword2h" => "sword2h", "spear2h" => "spear2h",
            "spear" => "spear", "staff" => "staff", _ => "sHanded"
        };
    }

    private static void ApplyPercentModifier(StatBlock stats, string key)
    {
        var percent = stats[$"{key}.percent"];
        if (Math.Abs(percent) > 1e-9)
            stats.Set(key, stats[key] * Math.Max(0, 1 + percent / 100d));
    }

    private static void ResolveShieldStats(StatBlock stats,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots, GameDataCatalog catalog)
    {
        var hasShield = slots.TryGetValue(EquipmentSlot.OffHand, out var offHand) &&
                        offHand.Item.Slot == EquipmentSlot.OffHand &&
                        (offHand.Item.AttachmentQualifier.Equals("shield", StringComparison.OrdinalIgnoreCase) ||
                         catalog.Records.GetValueOrDefault(offHand.Item.BaseRecord)?.Text("Class") == "WeaponArmor_Shield" ||
                         offHand.Item.Stats["blockChance"] > 0 && offHand.Item.Stats["blockAmount"] > 0);
        if (!hasShield)
        {
            stats.Set("blockChance", 0);
            stats.Set("blockAmount", 0);
            return;
        }
        stats.Set("blockChance", Math.Clamp(stats["blockChance"] + stats["blockChance.bonus"], 0, 100));
        ApplyPercentModifier(stats, "blockAmount");
    }

    private static void ApplyPlayerAbilityFormula(StatBlock stats)
    {
        // records/game/combatformulas.dbr:
        // (flat + level*12 + attribute*0.5) * (1 + modifier/100) + 53
        var levelTerm = Math.Max(1, stats["characterLevel"]) * 12;
        var oa = (stats["offensiveAbility"] + levelTerm + stats["cunning"] * 0.5) *
                 Math.Max(0, 1 + stats["offensiveAbility.percent"] / 100d) + 53;
        var da = (stats["defensiveAbility"] + levelTerm + stats["physique"] * 0.5) *
                 Math.Max(0, 1 + stats["defensiveAbility.percent"] / 100d) + 53;
        stats.Set("offensiveAbility", oa);
        stats.Set("defensiveAbility", da);
    }
}
