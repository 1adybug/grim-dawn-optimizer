using System.Diagnostics;
using GrimDawnOptimizer.Core.Contracts;
using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

public sealed class BeamSearchOptimizer : ILoadoutOptimizer
{
    private const int MaxComponentOptionsPerItem = 24;
    private const int MaxAugmentOptionsPerItem = 16;
    private const int MaxVariantsPerItem = 48;
    private const int MaxParetoInputPerItem = 128;
    private const int MaxParetoInputPerSignature = 192;

    private static readonly EquipmentSlot[] TargetSlots =
    [
        EquipmentSlot.Head, EquipmentSlot.Shoulders, EquipmentSlot.Chest, EquipmentSlot.Hands,
        EquipmentSlot.Legs, EquipmentSlot.Feet, EquipmentSlot.Waist, EquipmentSlot.Amulet,
        EquipmentSlot.Medal, EquipmentSlot.Relic, EquipmentSlot.RingLeft, EquipmentSlot.RingRight,
        EquipmentSlot.MainHand, EquipmentSlot.OffHand
    ];

    private readonly ICharacterStatEngine _statEngine;
    private readonly ICombatSimulator _simulator;

    public BeamSearchOptimizer(ICharacterStatEngine statEngine, ICombatSimulator simulator)
    {
        _statEngine = statEngine;
        _simulator = simulator;
    }

    public async Task<OptimizationRun> OptimizeAsync(
        CharacterSnapshot character,
        IReadOnlyList<ItemInstance> itemPool,
        IReadOnlyList<AttachmentDefinition> attachments,
        GameDataCatalog catalog,
        OptimizationProfile profile,
        IProgress<SearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Clamp(profile.SearchBudgetSeconds, 1, 60));
        var resultCount = Math.Clamp(profile.ResultCount, 1, 50);
        var evaluated = 0;
        var iteration = 0;

        var variantsBySlot = GenerateVariants(character, itemPool, attachments, profile, cancellationToken)
            .ToDictionary(x => x.Key, x => ParetoPrune(x.Value, profile, character.CurrentLoadout));

        var currentSlots = BuildCurrentLoadout(character, variantsBySlot);
        var currentCalculated = _statEngine.Calculate(character, currentSlots, catalog);
        var currentSimulation = _simulator.Simulate(character, currentCalculated, profile.Scenario, profile.Rotation);
        var currentMetrics = ExtractMetrics(currentCalculated, currentSimulation);
        var fallbackSteps = ResolveFallbackSteps(profile, variantsBySlot);
        var scoring = new ScoringEngine(profile.Metrics, currentMetrics, fallbackSteps);

        var beamWidth = Math.Clamp(profile.SearchBudgetSeconds * 160, 96, 1600);
        var beam = new List<BeamState> { new(new Dictionary<EquipmentSlot, LoadoutVariant>(), 0) };
        var bestScore = double.NegativeInfinity;

        foreach (var targetSlot in TargetSlots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watch.Elapsed >= budget) break;
            iteration++;
            var next = new List<BeamState>(beam.Count * 8);
            var candidates = variantsBySlot.GetValueOrDefault(targetSlot, []);

            if (candidates.Count == 0 || targetSlot == EquipmentSlot.OffHand)
            {
                foreach (var state in beam)
                {
                    if (targetSlot == EquipmentSlot.OffHand &&
                        state.Slots.TryGetValue(EquipmentSlot.MainHand, out var weapon) && weapon.Item.IsTwoHanded)
                    {
                        next.Add(state);
                        continue;
                    }
                    if (candidates.Count == 0) next.Add(state);
                }
            }

            foreach (var state in beam)
            {
                foreach (var variant in candidates)
                {
                    if (watch.Elapsed >= budget) break;
                    if (state.Slots.Values.Any(x => x.Item.Id == variant.Item.Id)) continue;
                    if (targetSlot == EquipmentSlot.OffHand &&
                        state.Slots.TryGetValue(EquipmentSlot.MainHand, out var weapon) && weapon.Item.IsTwoHanded) continue;

                    var slots = new Dictionary<EquipmentSlot, LoadoutVariant>(state.Slots) { [targetSlot] = variant };
                    var result = Evaluate(character, slots, catalog, profile, scoring);
                    evaluated++;
                    var heuristic = scoring.PartialHeuristic(result);
                    next.Add(new BeamState(slots, heuristic));
                    if (heuristic > bestScore) bestScore = heuristic;
                }
            }

            if (next.Count == 0) break;

            beam = next
                .OrderByDescending(x => x.Heuristic)
                .ThenBy(x => Signature(x.Slots), StringComparer.Ordinal)
                .Take(beamWidth)
                .ToList();

            progress?.Report(new SearchProgress(
                "BeamSearch", evaluated, iteration, bestScore, watch.Elapsed,
                $"已完成 {targetSlot}，保留 {beam.Count:N0} 个候选"));
        }

        var complete = beam
            .Select(state => CompleteLoadout(state.Slots, variantsBySlot, currentSlots))
            .Select(slots => Evaluate(character, slots, catalog, profile, scoring))
            .ToList();
        evaluated += complete.Count;

        if (currentSlots.Count > 0)
            complete.Add(Evaluate(character, currentSlots, catalog, profile, scoring));

        var finalists = complete.Order(LoadoutResultComparer.Instance).Take(Math.Max(resultCount * 3, 20)).ToList();
        LocalImprove(character, finalists, variantsBySlot, catalog, profile, scoring, budget, watch, ref evaluated, progress, cancellationToken);

        var results = finalists
            .Order(LoadoutResultComparer.Instance)
            .GroupBy(x => Signature(x.Loadout.Slots))
            .Select(x => x.First())
            .Take(resultCount)
            .ToList();

        for (var i = 0; i < results.Count; i++)
        {
            results[i].Rank = i + 1;
            if (watch.Elapsed >= budget + TimeSpan.FromSeconds(30)) break;
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SearchProgress("MonteCarlo", evaluated, i + 1, bestScore, watch.Elapsed, $"复核候选 {i + 1}/{results.Count}"));
            var seed = StableSeed(profile.Name, Signature(results[i].Loadout.Slots));
            results[i].Simulation = await _simulator.RunMonteCarloAsync(
                character, results[i].Loadout, profile.Scenario, profile.Rotation,
                Math.Clamp(profile.MonteCarloTrials, 500, 10_000), seed, cancellationToken);
        }

        watch.Stop();
        return new OptimizationRun
        {
            Results = results,
            EvaluatedCandidates = evaluated,
            Iterations = iteration,
            Elapsed = watch.Elapsed,
            StopReason = watch.Elapsed >= budget ? "达到搜索时间预算；已返回当前最佳候选" : "候选空间已完成近似搜索"
        };
    }

    private LoadoutResult Evaluate(
        CharacterSnapshot character,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots,
        GameDataCatalog catalog,
        OptimizationProfile profile,
        ScoringEngine scoring)
    {
        var calculated = _statEngine.Calculate(character, slots, catalog);
        var simulation = _simulator.Simulate(character, calculated, profile.Scenario, profile.Rotation);
        var metrics = ExtractMetrics(calculated, simulation);
        var (tiers, violations) = scoring.Score(metrics);
        var confidence = calculated.UnsupportedEffects.Count switch
        {
            0 => ConfidenceLevel.High,
            <= 2 => ConfidenceLevel.Medium,
            _ => ConfidenceLevel.Low
        };
        return new LoadoutResult
        {
            Rank = 0,
            Loadout = calculated,
            Simulation = simulation,
            Metrics = metrics,
            TierScores = tiers,
            ConstraintViolations = violations,
            Confidence = confidence
        };
    }

    private void LocalImprove(
        CharacterSnapshot character,
        List<LoadoutResult> finalists,
        IReadOnlyDictionary<EquipmentSlot, IReadOnlyList<LoadoutVariant>> variantsBySlot,
        GameDataCatalog catalog,
        OptimizationProfile profile,
        ScoringEngine scoring,
        TimeSpan budget,
        Stopwatch watch,
        ref int evaluated,
        IProgress<SearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var seeds = finalists.Take(Math.Min(8, finalists.Count)).ToArray();
        foreach (var seed in seeds)
        {
            var best = seed;
            foreach (var slot in TargetSlots)
            {
                if (watch.Elapsed >= budget) break;
                foreach (var variant in variantsBySlot.GetValueOrDefault(slot, []).Take(80))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (watch.Elapsed >= budget) break;
                    if (best.Loadout.Slots.Any(x => x.Key != slot && x.Value.Item.Id == variant.Item.Id)) continue;
                    var slots = new Dictionary<EquipmentSlot, LoadoutVariant>(best.Loadout.Slots) { [slot] = variant };
                    if (slot == EquipmentSlot.MainHand && variant.Item.IsTwoHanded) slots.Remove(EquipmentSlot.OffHand);
                    var result = Evaluate(character, slots, catalog, profile, scoring);
                    evaluated++;
                    if (LoadoutResultComparer.Instance.Compare(result, best) < 0) best = result;
                }
            }
            finalists.Add(best);
        }

        var top = finalists.Order(LoadoutResultComparer.Instance).Take(3).ToArray();
        foreach (var seed in top)
        {
            for (var first = 0; first < TargetSlots.Length && watch.Elapsed < budget; first++)
            for (var second = first + 1; second < TargetSlots.Length && watch.Elapsed < budget; second++)
            {
                var firstSlot = TargetSlots[first];
                var secondSlot = TargetSlots[second];
                foreach (var firstVariant in variantsBySlot.GetValueOrDefault(firstSlot, []).Take(12))
                foreach (var secondVariant in variantsBySlot.GetValueOrDefault(secondSlot, []).Take(12))
                {
                    if (watch.Elapsed >= budget) break;
                    if (firstVariant.Item.Id == secondVariant.Item.Id) continue;
                    var slots = new Dictionary<EquipmentSlot, LoadoutVariant>(seed.Loadout.Slots)
                    {
                        [firstSlot] = firstVariant,
                        [secondSlot] = secondVariant
                    };
                    if (slots.Values.GroupBy(x => x.Item.Id).Any(x => x.Count() > 1)) continue;
                    if (slots.TryGetValue(EquipmentSlot.MainHand, out var main) && main.Item.IsTwoHanded)
                        slots.Remove(EquipmentSlot.OffHand);
                    finalists.Add(Evaluate(character, slots, catalog, profile, scoring));
                    evaluated++;
                }
            }
        }

        progress?.Report(new SearchProgress("LocalSearch", evaluated, 0, 0, watch.Elapsed, "一槽/两槽邻域改进完成"));
    }

    private static IReadOnlyDictionary<EquipmentSlot, IReadOnlyList<LoadoutVariant>> GenerateVariants(
        CharacterSnapshot character,
        IReadOnlyList<ItemInstance> items,
        IReadOnlyList<AttachmentDefinition> attachments,
        OptimizationProfile profile,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<EquipmentSlot, IReadOnlyList<LoadoutVariant>>();
        var ownedAttachmentRecords = character.OwnedAttachments.Select(x => x.RecordId)
            .Concat(character.Items.SelectMany(x => new[] { x.ComponentRecord, x.AugmentRecord }))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var target in TargetSlots)
        {
            var variants = new List<LoadoutVariant>();
            foreach (var item in items.Where(x =>
                         CharacterStatEngine.IsCompatible(target, x.Slot, character.CanDualWield) &&
                         (target != EquipmentSlot.OffHand || !x.IsTwoHanded)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var components = SelectAttachmentOptions(
                    attachments, AttachmentKind.Component, item, character, profile,
                    item.ComponentRecord, item.CompletionBonusRecord, ownedAttachmentRecords, MaxComponentOptionsPerItem);
                var augments = SelectAttachmentOptions(
                    attachments, AttachmentKind.Augment, item, character, profile,
                    item.AugmentRecord, string.Empty, ownedAttachmentRecords, MaxAugmentOptionsPerItem);
                var itemVariants = new List<LoadoutVariant>(components.Count * augments.Count);
                foreach (var component in components)
                foreach (var augment in augments)
                    itemVariants.Add(new LoadoutVariant(item, component, augment));
                variants.AddRange(ParetoPrune(
                    itemVariants, profile, character.CurrentLoadout,
                    MaxVariantsPerItem, MaxParetoInputPerItem));
            }
            result[target] = variants;
        }
        return result;
    }

    private static IReadOnlyList<AttachmentDefinition?> SelectAttachmentOptions(
        IReadOnlyList<AttachmentDefinition> attachments,
        AttachmentKind kind,
        ItemInstance item,
        CharacterSnapshot character,
        OptimizationProfile profile,
        string installedId,
        string installedCompletionBonus,
        IReadOnlySet<string> ownedAttachmentRecords,
        int limit)
    {
        var compatible = attachments
            .Where(x => x.Kind == kind && IsAttachmentCompatible(x, item, character, ownedAttachmentRecords))
            .ToArray();
        if (compatible.Length + 1 <= limit)
            return compatible.Cast<AttachmentDefinition?>().Prepend(null).ToArray();

        var keys = ResolveParetoKeys(profile);
        var selected = new List<AttachmentDefinition?> { null };
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(AttachmentDefinition? attachment)
        {
            if (attachment is not null && ids.Add(attachment.Id)) selected.Add(attachment);
        }

        foreach (var attachment in compatible.Where(x => x.IsOwned || ownedAttachmentRecords.Contains(GameDataSource.AttachmentRecordId(x))))
            Add(attachment);
        if (!string.IsNullOrWhiteSpace(installedId))
            Add(compatible.FirstOrDefault(x =>
                string.Equals(GameDataSource.AttachmentRecordId(x), installedId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.CompletionBonusRecord, installedCompletionBonus, StringComparison.OrdinalIgnoreCase)));
        foreach (var attachment in compatible.OrderByDescending(x => AttachmentProxyScore(x, profile)).ThenBy(x => x.Id, StringComparer.Ordinal).Take(Math.Max(4, limit / 2)))
            Add(attachment);
        foreach (var key in keys)
            foreach (var attachment in compatible.OrderByDescending(x => x.Stats[key]).ThenBy(x => x.Id, StringComparer.Ordinal).Take(2))
                Add(attachment);
        foreach (var attachment in compatible.Where(x => x.Effects.Count > 0 || x.UnsupportedEffects.Count > 0).OrderBy(x => x.Id, StringComparer.Ordinal))
            Add(attachment);

        return selected.Take(limit).ToArray();
    }

    private static bool IsAttachmentCompatible(
        AttachmentDefinition attachment,
        ItemInstance item,
        CharacterSnapshot character,
        IReadOnlySet<string> ownedAttachmentRecords)
    {
        var isOwned = attachment.IsOwned || ownedAttachmentRecords.Contains(GameDataSource.AttachmentRecordId(attachment));
        if (!isOwned && attachment.LevelRequirement > character.Level) return false;
        if (!attachment.CompatibleSlots.Contains(item.Slot) &&
            !(item.Slot == EquipmentSlot.Ring && (attachment.CompatibleSlots.Contains(EquipmentSlot.RingLeft) || attachment.CompatibleSlots.Contains(EquipmentSlot.RingRight)))) return false;
        if (!isOwned && !string.IsNullOrWhiteSpace(attachment.RequiredFormula) && !character.KnownFormulas.Contains(attachment.RequiredFormula)) return false;
        if (!isOwned && !string.IsNullOrWhiteSpace(attachment.RequiredFaction) &&
            character.FactionReputation.GetValueOrDefault(attachment.RequiredFaction) < attachment.RequiredReputation) return false;
        return true;
    }

    private static IReadOnlyList<LoadoutVariant> ParetoPrune(
        IReadOnlyList<LoadoutVariant> variants,
        OptimizationProfile profile,
        IReadOnlyDictionary<EquipmentSlot, string> currentLoadout,
        int maxResults = 300,
        int maxInputPerSignature = MaxParetoInputPerSignature)
    {
        if (variants.Count <= 1) return variants;
        var keys = ResolveParetoKeys(profile);

        var retained = new List<LoadoutVariant>();
        foreach (var group in variants.GroupBy(InteractionSignature))
        {
            var rows = BoundParetoInput(group.ToArray(), keys, profile, currentLoadout, maxInputPerSignature);
            for (var i = 0; i < rows.Length; i++)
            {
                var dominated = false;
                for (var j = 0; j < rows.Length && !dominated; j++)
                {
                    if (i == j) continue;
                    dominated = Dominates(rows[j].CombinedStats, rows[i].CombinedStats, keys);
                }
                if (!dominated) retained.Add(rows[i]);
            }
        }

        return retained
            .OrderByDescending(x => ProxyScore(x, profile))
            .ThenByDescending(x => IsCurrentVariant(x, currentLoadout))
            .ThenBy(x => x.Item.Id, StringComparer.Ordinal)
            .Take(maxResults)
            .ToArray();
    }

    private static LoadoutVariant[] BoundParetoInput(
        LoadoutVariant[] rows,
        IReadOnlyList<string> keys,
        OptimizationProfile profile,
        IReadOnlyDictionary<EquipmentSlot, string> currentLoadout,
        int limit)
    {
        if (rows.Length <= limit) return rows;
        var selected = new List<LoadoutVariant>(limit);
        var seen = new HashSet<LoadoutVariant>();
        void Add(LoadoutVariant variant)
        {
            if (seen.Add(variant) && selected.Count < limit) selected.Add(variant);
        }

        foreach (var row in rows.Where(x => IsCurrentVariant(x, currentLoadout)).OrderBy(x => x.Item.Id, StringComparer.Ordinal)) Add(row);
        foreach (var row in rows.OrderByDescending(x => ProxyScore(x, profile)).ThenBy(x => x.Item.Id, StringComparer.Ordinal).Take(Math.Max(16, limit / 2))) Add(row);
        var perKey = Math.Max(2, limit / Math.Max(1, keys.Count * 2));
        foreach (var key in keys)
        {
            var direction = profile.Metrics.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))?.Direction
                            ?? MetricDirection.Maximize;
            var ordered = direction == MetricDirection.Minimize
                ? rows.OrderBy(x => x.CombinedStats[key])
                : rows.OrderByDescending(x => x.CombinedStats[key]);
            foreach (var row in ordered.ThenBy(x => x.Item.Id, StringComparer.Ordinal).Take(perKey)) Add(row);
        }
        return selected.ToArray();
    }

    private static string[] ResolveParetoKeys(OptimizationProfile profile) =>
        profile.Metrics.Select(x => x.Key).Concat(new[]
        {
            "health", "offensiveAbility", "defensiveAbility", "armor", "attackSpeed",
            "fireResistance", "coldResistance", "lightningResistance", "aetherResistance", "chaosResistance"
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool IsCurrentVariant(
        LoadoutVariant variant,
        IReadOnlyDictionary<EquipmentSlot, string> currentLoadout) =>
        currentLoadout.Values.Contains(variant.Item.Id) &&
        MatchesCurrentAttachment(variant.Component, variant.Item.ComponentRecord, variant.Item.CompletionBonusRecord) &&
        MatchesCurrentAttachment(variant.Augment, variant.Item.AugmentRecord, string.Empty);

    private static bool MatchesCurrentAttachment(AttachmentDefinition? attachment, string recordId, string completionBonusRecord)
    {
        if (string.IsNullOrWhiteSpace(recordId)) return attachment is null;
        return attachment is not null &&
               string.Equals(GameDataSource.AttachmentRecordId(attachment), recordId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(attachment.CompletionBonusRecord, completionBonusRecord, StringComparison.OrdinalIgnoreCase);
    }

    private static double AttachmentProxyScore(AttachmentDefinition attachment, OptimizationProfile profile)
    {
        var score = 0d;
        foreach (var rule in profile.Metrics)
        {
            var sign = rule.Direction == MetricDirection.Maximize ? 1 : -1;
            score += sign * rule.Weight * attachment.Stats[rule.Key] * Math.Pow(100, Math.Max(0, 4 - rule.PriorityTier));
        }
        score += StatKeys.DamageTypes.Sum(x => attachment.Stats[StatKeys.DamageFlat(x)] + attachment.Stats[StatKeys.WeaponDamage(x)]) * 10;
        score += attachment.Stats["health"] + attachment.Stats["offensiveAbility"] * 3 + attachment.Stats["defensiveAbility"] * 2;
        return score;
    }

    private static bool Dominates(StatBlock left, StatBlock right, IReadOnlyList<string> keys)
    {
        var strictlyBetter = false;
        foreach (var key in keys)
        {
            if (left[key] + 1e-9 < right[key]) return false;
            if (left[key] > right[key] + 1e-9) strictlyBetter = true;
        }
        return strictlyBetter;
    }

    private static double ProxyScore(LoadoutVariant variant, OptimizationProfile profile)
    {
        var stats = variant.CombinedStats;
        var score = 0d;
        foreach (var rule in profile.Metrics)
        {
            var sign = rule.Direction == MetricDirection.Maximize ? 1 : -1;
            score += sign * rule.Weight * stats[rule.Key] * Math.Pow(100, Math.Max(0, 4 - rule.PriorityTier));
        }
        score += StatKeys.DamageTypes.Sum(x => stats[StatKeys.DamageFlat(x)] + stats[StatKeys.WeaponDamage(x)]) * 10;
        score += stats["health"] + stats["offensiveAbility"] * 3 + stats["defensiveAbility"] * 2;
        return score;
    }

    private static string InteractionSignature(LoadoutVariant variant)
    {
        var effects = string.Join(',', variant.Effects.Select(x => x.Id).Order(StringComparer.Ordinal));
        var conversions = string.Join(',', variant.CombinedStats.Values.Where(x => x.Key.StartsWith("conversion.", StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).Order(StringComparer.Ordinal));
        return $"{variant.Item.SetId}|{effects}|{conversions}|{variant.Item.IsTwoHanded}";
    }

    private static Dictionary<EquipmentSlot, LoadoutVariant> BuildCurrentLoadout(
        CharacterSnapshot character,
        IReadOnlyDictionary<EquipmentSlot, IReadOnlyList<LoadoutVariant>> variantsBySlot)
    {
        var result = new Dictionary<EquipmentSlot, LoadoutVariant>();
        foreach (var (slot, itemId) in character.CurrentLoadout)
        {
            var variant = variantsBySlot.GetValueOrDefault(slot, [])
                .FirstOrDefault(x => x.Item.Id == itemId &&
                                     MatchesCurrentAttachment(x.Component, x.Item.ComponentRecord, x.Item.CompletionBonusRecord) &&
                                     MatchesCurrentAttachment(x.Augment, x.Item.AugmentRecord, string.Empty))
                ?? variantsBySlot.GetValueOrDefault(slot, []).FirstOrDefault(x => x.Item.Id == itemId);
            if (variant is not null) result[slot] = variant;
        }
        return result;
    }

    private static Dictionary<EquipmentSlot, LoadoutVariant> CompleteLoadout(
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> partial,
        IReadOnlyDictionary<EquipmentSlot, IReadOnlyList<LoadoutVariant>> variantsBySlot,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> current)
    {
        var result = new Dictionary<EquipmentSlot, LoadoutVariant>(partial);
        foreach (var slot in TargetSlots)
        {
            if (slot == EquipmentSlot.OffHand &&
                result.TryGetValue(EquipmentSlot.MainHand, out var mainHand) && mainHand.Item.IsTwoHanded)
            {
                result.Remove(EquipmentSlot.OffHand);
                continue;
            }
            if (result.ContainsKey(slot)) continue;

            IEnumerable<LoadoutVariant> candidates = variantsBySlot.GetValueOrDefault(slot, []);
            if (current.TryGetValue(slot, out var equipped))
                candidates = candidates.OrderByDescending(x => x == equipped);

            var selected = candidates.FirstOrDefault(candidate =>
                result.Values.All(existing => existing.Item.Id != candidate.Item.Id));
            if (selected is null) continue;
            result[slot] = selected;
            if (slot == EquipmentSlot.MainHand && selected.Item.IsTwoHanded)
                result.Remove(EquipmentSlot.OffHand);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, double> ResolveFallbackSteps(
        OptimizationProfile profile,
        IReadOnlyDictionary<EquipmentSlot, IReadOnlyList<LoadoutVariant>> variants)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in profile.Metrics)
        {
            var values = variants.Values.SelectMany(x => x)
                .Select(x => Math.Abs(x.CombinedStats[rule.Key]))
                .Where(x => x > 1e-9)
                .Order()
                .ToArray();
            result[rule.Key] = values.Length == 0 ? 1 : values[values.Length / 2];
        }
        return result;
    }

    public static IReadOnlyDictionary<string, double> ExtractMetrics(CalculatedLoadout loadout, SimulationReport simulation)
    {
        var result = new Dictionary<string, double>(loadout.Stats.Values, StringComparer.OrdinalIgnoreCase)
        {
            ["dps"] = simulation.ExpectedDps,
            ["timeToKill"] = simulation.TimeToKillSeconds,
            ["effectiveHealth"] = simulation.EffectiveHealth,
            ["incomingDps"] = simulation.IncomingDps,
            ["sustain"] = simulation.SustainPerSecond,
            ["timeToDeath"] = simulation.TimeToDeathSeconds,
            ["hitChance"] = simulation.HitChance * 100,
            ["critChance"] = simulation.CritChance * 100
        };
        return result;
    }

    private static string Signature(IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots) =>
        string.Join(';', slots.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value.Item.Id}:{x.Value.Component?.Id}:{x.Value.Augment?.Id}"));

    private static int StableSeed(string profileName, string signature)
    {
        unchecked
        {
            var hash = 17;
            foreach (var ch in profileName + '|' + signature) hash = hash * 31 + ch;
            return hash;
        }
    }

    private sealed record BeamState(Dictionary<EquipmentSlot, LoadoutVariant> Slots, double Heuristic);
}
