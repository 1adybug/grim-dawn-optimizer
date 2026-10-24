using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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
        // Weapons define the largest DPS/conversion/WPS interactions. Searching
        // them first prevents a beam dominated by armor-only partial scores from
        // discarding the best weapon core before it is ever evaluated.
        EquipmentSlot.MainHand, EquipmentSlot.OffHand,
        EquipmentSlot.Head, EquipmentSlot.Shoulders, EquipmentSlot.Chest, EquipmentSlot.Hands,
        EquipmentSlot.Legs, EquipmentSlot.Feet, EquipmentSlot.Waist, EquipmentSlot.Amulet,
        EquipmentSlot.Medal, EquipmentSlot.Relic, EquipmentSlot.RingLeft, EquipmentSlot.RingRight
    ];

    private readonly ICharacterStatEngine _statEngine;
    private readonly ICombatSimulator _simulator;
    private readonly int _maxDegreeOfParallelism;

    public BeamSearchOptimizer(
        ICharacterStatEngine statEngine,
        ICombatSimulator simulator,
        int? maxDegreeOfParallelism = null)
    {
        _statEngine = statEngine;
        _simulator = simulator;
        _maxDegreeOfParallelism = Math.Clamp(maxDegreeOfParallelism ?? Environment.ProcessorCount, 1, 16);
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
        var budget = TimeSpan.FromSeconds(Math.Clamp(profile.SearchBudgetSeconds, 1, 300));
        var resultCount = Math.Clamp(profile.ResultCount, 1, 50);
        var evaluated = 0;
        var iteration = 0;
        DifficultyRules.Apply(catalog, profile.Scenario);

        var variantsBySlot = GenerateVariants(character, itemPool, attachments, profile, cancellationToken)
            .ToDictionary(x => x.Key, x => ParetoPrune(x.Value, profile, character.CurrentLoadout));

        var currentSlots = BuildCurrentLoadout(character, variantsBySlot);
        var currentCalculated = _statEngine.Calculate(character, currentSlots, catalog);
        profile.RuntimePanelCorrections = PanelStatCalibration.Create(currentCalculated.Stats, profile);
        currentCalculated = PanelStatCalibration.Apply(currentCalculated, profile);
        var currentRotation = ResolveRotation(character, catalog, profile, currentSlots);
        var currentSimulation = _simulator.Simulate(character, currentCalculated, profile.Scenario, currentRotation);
        var currentWeaponSimulation = _simulator.Simulate(
            character,
            currentCalculated,
            profile.Scenario,
            ResolveWeaponAttackRotation(character, catalog, currentSlots));
        ConfigureDpsCalibration(
            profile,
            currentRotation,
            currentSimulation.UnmitigatedDps,
            currentWeaponSimulation.UnmitigatedDps);
        currentSimulation = ApplyDpsCalibration(currentSimulation, currentWeaponSimulation, profile);
        var currentMetrics = ExtractMetrics(currentCalculated, currentSimulation, profile.Scenario);
        var benchmarks = BossMetricBenchmarkResolver.Resolve(profile.Metrics, profile.Scenario, currentMetrics);
        var scoring = new ScoringEngine(profile.Metrics, benchmarks);
        var searchStart = watch.Elapsed;
        var remainingTicks = Math.Max(0, budget.Ticks - searchStart.Ticks);
        var beamDeadline = searchStart + TimeSpan.FromTicks((long)(remainingTicks * 0.65));
        var localDeadline = searchStart + TimeSpan.FromTicks((long)(remainingTicks * 0.88));

        var beamWidth = Math.Clamp(profile.SearchBudgetSeconds * 220, 128, 2400);
        var beam = new List<BeamState> { new(new Dictionary<EquipmentSlot, LoadoutVariant>(), 0) };
        var bestScore = double.NegativeInfinity;

        foreach (var targetSlot in TargetSlots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watch.Elapsed >= beamDeadline) break;
            iteration++;
            // Reserve a fair slice of the beam phase for every slot. Without
            // this, the main/off-hand Cartesian product can consume the entire
            // budget before armor, rings and set interactions are ever visited.
            var slotDeadline = searchStart + TimeSpan.FromTicks(
                (beamDeadline - searchStart).Ticks * iteration / TargetSlots.Length);
            var next = new ConcurrentBag<BeamState>();
            var candidates = variantsBySlot.GetValueOrDefault(targetSlot, []);
            var expansionCandidates = DiversifiedLocalCandidates(candidates, 192);

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

            var slotEvaluated = 0;
            Parallel.ForEach(beam, CreateParallelOptions(cancellationToken), (state, loopState) =>
            {
                if (watch.Elapsed >= slotDeadline)
                {
                    loopState.Stop();
                    return;
                }
                foreach (var variant in expansionCandidates)
                {
                    if (watch.Elapsed >= slotDeadline)
                    {
                        loopState.Stop();
                        break;
                    }
                    if (state.Slots.Values.Any(x => x.Item.Id == variant.Item.Id)) continue;
                    if (targetSlot == EquipmentSlot.OffHand &&
                        state.Slots.TryGetValue(EquipmentSlot.MainHand, out var weapon) && weapon.Item.IsTwoHanded) continue;

                    var slots = new Dictionary<EquipmentSlot, LoadoutVariant>(state.Slots) { [targetSlot] = variant };
                    var result = Evaluate(character, slots, catalog, profile, scoring);
                    Interlocked.Increment(ref slotEvaluated);
                    var heuristic = scoring.PartialHeuristic(result);
                    next.Add(new BeamState(slots, heuristic));
                }
            });
            evaluated += slotEvaluated;

            if (next.Count == 0) break;

            var nextStates = next.ToArray();
            bestScore = Math.Max(bestScore, nextStates.Max(x => x.Heuristic));
            beam = SelectDiverseBeam(nextStates, beamWidth);

            progress?.Report(new SearchProgress(
                "BeamSearch", evaluated, iteration, bestScore, watch.Elapsed,
                $"已完成 {targetSlot}，保留 {beam.Count:N0} 个候选 · {_maxDegreeOfParallelism} 线程"));
        }

        var completedSlots = beam
            .Select(state => CompleteLoadout(state.Slots, variantsBySlot, currentSlots))
            .ToArray();
        var complete = ParallelMap(
            completedSlots,
            slots => Evaluate(character, slots, catalog, profile, scoring),
            cancellationToken).ToList();
        evaluated += complete.Count;

        if (currentSlots.Count > 0)
            complete.Add(Evaluate(character, currentSlots, catalog, profile, scoring));

        var finalists = complete
            .Order(LoadoutResultComparer.Instance)
            .GroupBy(x => EquipmentSignature(x.Loadout.Slots))
            .Select(x => x.First())
            .Take(Math.Max(resultCount * 3, 20))
            .ToList();
        LocalImprove(character, finalists, variantsBySlot, catalog, profile, scoring, localDeadline, watch, ref evaluated, progress, cancellationToken);
        MultiStartImprove(character, finalists, variantsBySlot, currentSlots, catalog, profile, scoring,
            budget, watch, ref evaluated, progress, cancellationToken);
        var searchReachedBudget = watch.Elapsed >= budget;

        // A local-search seed can temporarily lose its off-hand when a two-handed
        // weapon is tested, or remain partial when the time budget expires between
        // slots. Complete every plan again before it reaches the UI so a real
        // one-handed/armor candidate is never shown as “no available equipment”.
        var completedFinalists = finalists.Select(result =>
        {
            var slots = CompleteLoadout(result.Loadout.Slots, variantsBySlot, currentSlots);
            if (Signature(slots) == Signature(result.Loadout.Slots)) return result;
            evaluated++;
            return Evaluate(character, slots, catalog, profile, scoring);
        }).ToList();

        var results = completedFinalists
            // Invalid combinations must never be presented as actionable plans. Partial
            // beam states remain eligible because later slots may supply the missing
            // attributes or requirement reduction; filtering is intentionally final-only.
            .Where(x => x.Loadout.IsValid)
            .Order(LoadoutResultComparer.Instance)
            // A result slot represents an equipment plan, not another spelling of the
            // same plan with different attachment ids. Keep the best attachment setup
            // for each physical equipment combination so the candidate list is useful.
            .GroupBy(x => EquipmentSignature(x.Loadout.Slots))
            .Select(x => x.First())
            .Take(resultCount)
            .ToList();

        // Keep optimizing the complete equipment + attachment plan, but expose a
        // target-free comparison that strips every component and augment. This lets
        // the player equip the physical items first and compare the game sheet before
        // committing attachments.
        Parallel.ForEach(results, CreateParallelOptions(cancellationToken), result =>
        {
            var equipmentOnlySlots = result.Loadout.Slots.ToDictionary(
                x => x.Key,
                x => new LoadoutVariant(x.Value.Item, null, null));
            var equipmentOnlyLoadout = _statEngine.Calculate(character, equipmentOnlySlots, catalog);
            equipmentOnlyLoadout = PanelStatCalibration.Apply(equipmentOnlyLoadout, profile);
            var equipmentOnlyRotation = ResolveRotation(character, catalog, profile, equipmentOnlySlots);
            var equipmentOnlyRaw = _simulator.Simulate(
                character, equipmentOnlyLoadout, profile.Scenario, equipmentOnlyRotation);
            var equipmentOnlyWeaponRaw = _simulator.Simulate(
                character,
                equipmentOnlyLoadout,
                profile.Scenario,
                ResolveWeaponAttackRotation(character, catalog, equipmentOnlySlots));
            var equipmentOnlySimulation = ApplyDpsCalibration(
                equipmentOnlyRaw,
                equipmentOnlyWeaponRaw,
                profile);
            result.EquipmentOnlyUnmitigatedDps = equipmentOnlySimulation.UnmitigatedDps;
            result.SingleItemComparisons = BuildSingleItemComparisons(
                character, currentSlots, currentCalculated, currentSimulation,
                result, attachments, catalog, profile, scoring, cancellationToken);
        });

        for (var i = 0; i < results.Count; i++)
            results[i].Rank = i + 1;

        var reviewed = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, results.Count),
            CreateParallelOptions(cancellationToken),
            async (i, token) =>
        {
            var result = results[i];
            var seed = StableSeed(profile.Name, Signature(result.Loadout.Slots));
            var finalRotation = ResolveRotation(character, catalog, profile, result.Loadout.Slots);
            result.Simulation = await _simulator.RunMonteCarloAsync(
                character, result.Loadout, profile.Scenario, finalRotation,
                Math.Clamp(profile.MonteCarloTrials, 500, 10_000), seed, token);
            var finalWeaponSimulation = _simulator.Simulate(
                character,
                result.Loadout,
                profile.Scenario,
                ResolveWeaponAttackRotation(character, catalog, result.Loadout.Slots));
            result.Simulation = ApplyDpsCalibration(result.Simulation, finalWeaponSimulation, profile);
            var completed = Interlocked.Increment(ref reviewed);
            progress?.Report(new SearchProgress(
                "MonteCarlo", evaluated, completed, bestScore, watch.Elapsed,
                $"并行复核候选 {completed}/{results.Count} · {_maxDegreeOfParallelism} 线程"));
        });

        watch.Stop();
        return new OptimizationRun
        {
            Results = results,
            EvaluatedCandidates = evaluated,
            Iterations = iteration,
            Elapsed = watch.Elapsed,
            StopReason = (searchReachedBudget
                ? "达到搜索时间预算；已返回当前最佳候选"
                : "候选空间已完成近似搜索") +
                $"；并行 {_maxDegreeOfParallelism} 线程"
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
        calculated = PanelStatCalibration.Apply(calculated, profile);
        calculated = ApplyAttachmentSourceValidation(calculated);
        return EvaluateCalculated(character, slots, calculated, catalog, profile, scoring);
    }

    private IReadOnlyList<ItemSwapComparison> BuildSingleItemComparisons(
        CharacterSnapshot character,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> currentSlots,
        CalculatedLoadout currentCalculated,
        SimulationReport currentSimulation,
        LoadoutResult candidate,
        IReadOnlyList<AttachmentDefinition> attachments,
        GameDataCatalog catalog, OptimizationProfile profile, ScoringEngine scoring,
        CancellationToken cancellationToken)
    {
        var before = PanelStatCalibration.SheetValues(currentCalculated, profile.Scenario, currentSimulation.UnmitigatedDps);
        var comparisons = new List<ItemSwapComparison>();
        foreach (var (slot, variant) in candidate.Loadout.Slots.OrderBy(x => x.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (currentSlots.TryGetValue(slot, out var equipped) && equipped.Item.Id == variant.Item.Id) continue;
            var item = variant.Item;
            var component = attachments.FirstOrDefault(x => x.Kind == AttachmentKind.Component &&
                MatchesCurrentAttachment(x, item.ComponentRecord, item.CompletionBonusRecord));
            var augment = attachments.FirstOrDefault(x => x.Kind == AttachmentKind.Augment &&
                MatchesCurrentAttachment(x, item.AugmentRecord, string.Empty));
            var missingAttachment = (!string.IsNullOrWhiteSpace(item.ComponentRecord) && component is null) ||
                                    (!string.IsNullOrWhiteSpace(item.AugmentRecord) && augment is null);
            var slots = new Dictionary<EquipmentSlot, LoadoutVariant>(currentSlots)
            { [slot] = new LoadoutVariant(item, component, augment) };
            var swap = Evaluate(character, slots, catalog, profile, scoring);
            var errors = swap.Loadout.ValidationErrors.ToList();
            if (missingAttachment) errors.Add("当前镶嵌附件未解析，无法可靠对照");
            if (slots.Values.GroupBy(x => x.Item.Id).Any(x => x.Count() > 1))
                errors.Add("该物品已在其他槽位穿戴，不能只替换此槽位");
            if (slots.TryGetValue(EquipmentSlot.MainHand, out var main) && main.Item.IsTwoHanded && slots.ContainsKey(EquipmentSlot.OffHand))
                errors.Add("双手武器需要同时卸下副手");
            var valid = errors.Count == 0;
            comparisons.Add(new ItemSwapComparison(slot, item.Name, valid,
                valid ? "只换此件，其余维持当前穿戴；使用物品现有组件和增幅。" : string.Join("；", errors.Distinct()),
                before, valid ? PanelStatCalibration.SheetValues(swap.Loadout, profile.Scenario, swap.Simulation.UnmitigatedDps)
                    : new Dictionary<string, double>()));
        }
        return comparisons;
    }

    private static CalculatedLoadout ApplyAttachmentSourceValidation(CalculatedLoadout loadout)
    {
        var errors = AttachmentSourceErrors(loadout.Slots).ToArray();
        if (errors.Length == 0) return loadout;
        return new CalculatedLoadout
        {
            Slots = loadout.Slots,
            Stats = loadout.Stats,
            Effects = loadout.Effects,
            UnsupportedEffects = loadout.UnsupportedEffects,
            ValidationErrors = loadout.ValidationErrors.Concat(errors).Distinct().ToArray()
        };
    }

    internal static bool HasActionableAttachmentSources(
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots) =>
        !AttachmentSourceErrors(slots).Any();

    private static IEnumerable<string> AttachmentSourceErrors(
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots)
    {
        var planItemIds = slots.Values.Select(x => x.Item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in slots.Values)
        {
            if (!HasActionableAttachmentSource(variant.Component, variant.Item, planItemIds))
                yield return $"{variant.Item.Name} 的组件没有方案外可用来源";
            if (!HasActionableAttachmentSource(variant.Augment, variant.Item, planItemIds))
                yield return $"{variant.Item.Name} 的增幅没有方案外可用来源";
        }
    }

    private static bool HasActionableAttachmentSource(
        AttachmentDefinition? attachment,
        ItemInstance targetItem,
        IReadOnlySet<string> planItemIds)
    {
        if (attachment is null || IsAlreadyInstalled(attachment, targetItem)) return true;
        // Direct optimizer callers may provide synthetic/legacy definitions without
        // acquisition metadata. Production definitions have already been filtered by
        // GameDataSource, so only explicit installed sources need plan-aware exclusion.
        if (attachment.AcquisitionSources.Count == 0) return true;
        return attachment.AcquisitionSources.Any(source =>
            source.Kind != AttachmentAcquisitionKind.Installed ||
            string.IsNullOrWhiteSpace(source.HostItemId) ||
            !planItemIds.Contains(source.HostItemId));
    }

    private static bool IsAlreadyInstalled(AttachmentDefinition attachment, ItemInstance item) =>
        attachment.Kind switch
        {
            AttachmentKind.Component => MatchesCurrentAttachment(
                attachment, item.ComponentRecord, item.CompletionBonusRecord),
            AttachmentKind.Augment => MatchesCurrentAttachment(
                attachment, item.AugmentRecord, string.Empty),
            _ => false
        };

    private LoadoutResult EvaluateCalculated(
        CharacterSnapshot character,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots,
        CalculatedLoadout calculated,
        GameDataCatalog catalog,
        OptimizationProfile profile,
        ScoringEngine scoring)
    {
        var rotation = ResolveRotation(character, catalog, profile, slots);
        var rawSimulation = _simulator.Simulate(character, calculated, profile.Scenario, rotation);
        var rawWeaponSimulation = _simulator.Simulate(
            character,
            calculated,
            profile.Scenario,
            ResolveWeaponAttackRotation(character, catalog, slots));
        var simulation = ApplyDpsCalibration(
            rawSimulation,
            rawWeaponSimulation,
            profile);
        var metrics = ExtractMetrics(calculated, simulation, profile.Scenario);
        var (tiers, contributions) = scoring.Score(metrics);
        var confidence = calculated.UnsupportedEffects.Count switch
        {
            0 when calculated.Slots.Values.All(x => x.Item.Seed == 0 || x.Item.SeedStatsResolved) => ConfidenceLevel.High,
            0 => ConfidenceLevel.Medium,
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
            ScoreContributions = contributions,
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
                foreach (var variant in DiversifiedLocalCandidates(variantsBySlot.GetValueOrDefault(slot, []), 80))
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

    private void MultiStartImprove(
        CharacterSnapshot character,
        List<LoadoutResult> finalists,
        IReadOnlyDictionary<EquipmentSlot, IReadOnlyList<LoadoutVariant>> variantsBySlot,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> currentSlots,
        GameDataCatalog catalog,
        OptimizationProfile profile,
        ScoringEngine scoring,
        TimeSpan budget,
        Stopwatch watch,
        ref int evaluated,
        IProgress<SearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (finalists.Count == 0 || watch.Elapsed >= budget) return;
        var rng = new Random(StableSeed(profile.Name, "multi-start"));
        var restarts = 0;
        while (watch.Elapsed < budget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seedPool = finalists.Order(LoadoutResultComparer.Instance)
                .Take(Math.Min(40, finalists.Count)).ToArray();
            var seed = seedPool[rng.Next(seedPool.Length)];
            var slots = new Dictionary<EquipmentSlot, LoadoutVariant>(seed.Loadout.Slots);

            var perturbations = 2 + rng.Next(3);
            for (var step = 0; step < perturbations; step++)
                TryRandomMutation(slots, variantsBySlot, rng);
            slots = CompleteLoadout(slots, variantsBySlot, currentSlots);
            var best = Evaluate(character, slots, catalog, profile, scoring);
            evaluated++;

            // Random-order hill climbing samples the full candidate list, including
            // variants outside the proxy-score top slice used by deterministic local search.
            for (var step = 0; step < 24 && watch.Elapsed < budget; step++)
            {
                var neighborSlots = new Dictionary<EquipmentSlot, LoadoutVariant>(best.Loadout.Slots);
                if (!TryRandomMutation(neighborSlots, variantsBySlot, rng)) continue;
                neighborSlots = CompleteLoadout(neighborSlots, variantsBySlot, currentSlots);
                var neighbor = Evaluate(character, neighborSlots, catalog, profile, scoring);
                evaluated++;
                if (LoadoutResultComparer.Instance.Compare(neighbor, best) < 0) best = neighbor;
            }

            finalists.Add(best);
            restarts++;
        }

        progress?.Report(new SearchProgress("MultiStart", evaluated, restarts, 0, watch.Elapsed,
            $"完成 {restarts} 次多起点扰动复核"));
    }

    private static bool TryRandomMutation(
        Dictionary<EquipmentSlot, LoadoutVariant> slots,
        IReadOnlyDictionary<EquipmentSlot, IReadOnlyList<LoadoutVariant>> variantsBySlot,
        Random rng)
    {
        var targetSlot = TargetSlots[rng.Next(TargetSlots.Length)];
        var candidates = variantsBySlot.GetValueOrDefault(targetSlot, []);
        if (candidates.Count == 0) return false;
        if (targetSlot == EquipmentSlot.OffHand &&
            slots.TryGetValue(EquipmentSlot.MainHand, out var currentMain) && currentMain.Item.IsTwoHanded)
            return false;

        for (var attempt = 0; attempt < Math.Min(12, candidates.Count); attempt++)
        {
            var variant = candidates[rng.Next(candidates.Count)];
            if (slots.Any(x => x.Key != targetSlot && x.Value.Item.Id == variant.Item.Id)) continue;
            slots[targetSlot] = variant;
            if (targetSlot == EquipmentSlot.MainHand && variant.Item.IsTwoHanded)
                slots.Remove(EquipmentSlot.OffHand);
            return true;
        }
        return false;
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
            .GroupBy(AttachmentMechanicsSignature, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(x =>
                    string.Equals(GameDataSource.AttachmentRecordId(x), installedId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.CompletionBonusRecord, installedCompletionBonus, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.IsOwned || ownedAttachmentRecords.Contains(GameDataSource.AttachmentRecordId(x)))
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .First())
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

    internal static bool IsAttachmentCompatible(
        AttachmentDefinition attachment,
        ItemInstance item,
        CharacterSnapshot character,
        IReadOnlySet<string> ownedAttachmentRecords)
    {
        var isOwned = attachment.IsOwned || ownedAttachmentRecords.Contains(GameDataSource.AttachmentRecordId(attachment));
        if (!isOwned && attachment.LevelRequirement > character.Level) return false;
        if (!attachment.CompatibleSlots.Contains(item.Slot) &&
            !(item.Slot == EquipmentSlot.Ring && (attachment.CompatibleSlots.Contains(EquipmentSlot.RingLeft) || attachment.CompatibleSlots.Contains(EquipmentSlot.RingRight)))) return false;
        if (attachment.CompatibleItemQualifiers.Count > 0 &&
            (string.IsNullOrWhiteSpace(item.AttachmentQualifier) ||
             !attachment.CompatibleItemQualifiers.Contains(item.AttachmentQualifier))) return false;
        var hasAlwaysAvailableFormula = attachment.AcquisitionSources.Any(source =>
            source.Kind == AttachmentAcquisitionKind.Blacksmith && source.IsAlwaysAvailable);
        if (!isOwned &&
            !hasAlwaysAvailableFormula &&
            !string.IsNullOrWhiteSpace(attachment.RequiredFormula) &&
            !character.KnownFormulas.Contains(attachment.RequiredFormula)) return false;
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
                // The exact equipped item+attachment variant is the calibration
                // anchor and must survive even when another attachment dominates it.
                if (IsCurrentVariant(rows[i], currentLoadout))
                {
                    retained.Add(rows[i]);
                    continue;
                }
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
            .OrderByDescending(x => IsCurrentVariant(x, currentLoadout))
            .ThenByDescending(x => ProxyScore(x, profile))
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
            "allSkills"
        }).Concat(StatKeys.DamageTypes.SelectMany(x => new[]
        {
            StatKeys.DamageFlat(x), StatKeys.WeaponDamage(x), StatKeys.DamagePercent(x),
            StatKeys.DamageDot(x), StatKeys.DamageDotPercent(x), StatKeys.Resistance(x)
        })).Concat(new[]
        {
            "physique", "cunning", "spirit",
            "requirementReduction.global", "requirementReduction.armorPhysique",
            "requirementReduction.rangedCunning", "requirementReduction.jewelrySpirit",
            "requirementReduction.meleePhysique", "requirementReduction.meleeCunning",
            "requirementReduction.meleeSpirit", "requirementReduction.shieldPhysique",
            "requirementReduction.weaponPhysique", "requirementReduction.weaponSpirit"
        }).Concat(string.IsNullOrWhiteSpace(profile.Rotation.MainSkillRecord)
            ? []
            : new[] { $"skillLevel.{profile.Rotation.MainSkillRecord}", $"skillModifier.{profile.Rotation.MainSkillRecord}" })
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

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
            var benchmark = BossMetricBenchmarkResolver.Resolve(rule.Key, profile.Scenario);
            var value = rule.Key.Equals("resistanceCoverage", StringComparison.OrdinalIgnoreCase)
                ? BossMetricBenchmarkResolver.ResistanceCoverage(attachment.Stats)
                : attachment.Stats[rule.Key];
            score += sign * rule.Weight * value / Math.Max(1e-9, benchmark);
        }
        score += StatKeys.DamageTypes.Sum(x => attachment.Stats[StatKeys.DamageFlat(x)] + attachment.Stats[StatKeys.WeaponDamage(x)]) * 10;
        score += RetaliationProxyDamage(attachment.Stats, profile.Scenario) * 10;
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
            var benchmark = BossMetricBenchmarkResolver.Resolve(rule.Key, profile.Scenario);
            var value = rule.Key.Equals("resistanceCoverage", StringComparison.OrdinalIgnoreCase)
                ? BossMetricBenchmarkResolver.ResistanceCoverage(stats)
                : stats[rule.Key];
            score += sign * rule.Weight * value / Math.Max(1e-9, benchmark);
        }
        score += StatKeys.DamageTypes.Sum(x => stats[StatKeys.DamageFlat(x)] + stats[StatKeys.WeaponDamage(x)]) * 10;
        score += RetaliationProxyDamage(stats, profile.Scenario) * 10;
        score += stats["health"] + stats["offensiveAbility"] * 3 + stats["defensiveAbility"] * 2;
        return score;
    }

    private static double RetaliationProxyDamage(StatBlock stats, CombatScenario scenario) =>
        StatKeys.DamageTypes.Sum(type =>
            stats[StatKeys.RetaliationFlat(type)] + stats[StatKeys.RetaliationDotTotal(type)]) *
        Math.Max(0, scenario.IncomingDamage.HitsPerSecond);

    private static string InteractionSignature(LoadoutVariant variant)
    {
        var effects = string.Join(',', variant.Effects.Select(x => x.Id).Order(StringComparer.Ordinal));
        var interactions = string.Join(',', variant.CombinedStats.Values.Where(x =>
                x.Key.StartsWith("conversion.", StringComparison.OrdinalIgnoreCase) ||
                x.Key.StartsWith("skillLevel.", StringComparison.OrdinalIgnoreCase) ||
                x.Key.StartsWith("skillModifier.", StringComparison.OrdinalIgnoreCase) ||
                x.Key.Equals("retaliation.flatProxy", StringComparison.OrdinalIgnoreCase) ||
                x.Key.Equals("retaliation.percentProxy", StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.Key}:{x.Value.ToString("R", CultureInfo.InvariantCulture)}").Order(StringComparer.Ordinal));
        var itemMechanics = $"{variant.Item.BaseRecord}|{variant.Item.PrefixRecord}|{variant.Item.SuffixRecord}|{variant.Item.Seed}";
        return $"{itemMechanics}|{variant.Item.SetId}|{effects}|{interactions}|{variant.Item.IsTwoHanded}";
    }

    private static string AttachmentMechanicsSignature(AttachmentDefinition attachment)
    {
        var effects = string.Join(',', attachment.Effects
            .Select(EffectMechanicsSignature)
            .Order(StringComparer.Ordinal));
        var unsupported = string.Join(',', attachment.UnsupportedEffects
            .Select(x => $"{x.Record}:{x.Description}")
            .Order(StringComparer.Ordinal));
        return $"{StatSignature(attachment.Stats)}|{effects}|{unsupported}";
    }

    private static string EffectMechanicsSignature(TriggeredEffect effect) =>
        $"{effect.Trigger}:{effect.Chance.ToString("R", CultureInfo.InvariantCulture)}:" +
        $"{effect.CooldownSeconds.ToString("R", CultureInfo.InvariantCulture)}:" +
        $"{effect.DurationSeconds.ToString("R", CultureInfo.InvariantCulture)}:{effect.IsDefensive}:" +
        StatSignature(effect.Damage);

    private static string StatSignature(StatBlock stats) => string.Join(',', stats.Values
        .Where(x => Math.Abs(x.Value) > 1e-12)
        .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
        .Select(x => $"{x.Key.ToLowerInvariant()}={x.Value.ToString("R", CultureInfo.InvariantCulture)}"));

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

    public static IReadOnlyDictionary<string, double> ExtractMetrics(CalculatedLoadout loadout, SimulationReport simulation, CombatScenario? scenario = null)
    {
        var result = new Dictionary<string, double>(loadout.Stats.Values, StringComparer.OrdinalIgnoreCase)
        {
            ["dps"] = simulation.ExpectedDps,
            ["resistanceCoverage"] = BossMetricBenchmarkResolver.ResistanceCoverage(loadout.Stats),
            ["timeToKill"] = simulation.TimeToKillSeconds,
            ["effectiveHealth"] = simulation.EffectiveHealth,
            ["incomingDps"] = simulation.IncomingDps,
            ["sustain"] = simulation.SustainPerSecond,
            ["timeToDeath"] = simulation.TimeToDeathSeconds,
            ["hitChance"] = simulation.HitChance * 100,
            ["critChance"] = simulation.CritChance * 100
        };
        if (scenario is not null)
            foreach (var type in StatKeys.DamageTypes)
                result[StatKeys.Resistance(type)] = DifficultyRules.EffectiveResistance(loadout.Stats, scenario, type);
        return result;
    }

    public static bool RequiresWeaponDpsCalibration(SkillRotationDefinition rotation) =>
        !rotation.IsFallback && rotation.WeaponDamagePercent > 0;

    private static void ConfigureDpsCalibration(
        OptimizationProfile profile,
        SkillRotationDefinition rotation,
        double currentSkillDps,
        double currentWeaponDps)
    {
        profile.RuntimeDpsCalibrationFactor = 1;
        profile.RuntimeWeaponDpsCalibrationFactor = 1;
        profile.RuntimeSkillAddedDpsCalibrationFactor = 1;
        profile.RuntimeDpsCalibrationMode = DpsCalibrationMode.None;

        if (!profile.UsePanelDpsCalibration) return;

        // A retaliation-only or otherwise non-active setup has no sheet-DPS
        // anchor. Leave it uncalibrated instead of blocking the optimizer.
        if (!double.IsFinite(currentSkillDps) || currentSkillDps <= 0) return;

        ValidatePanelDps(profile.CurrentPanelDps, "当前主攻技能的游戏面板 DPS");
        var singleFactor = profile.CurrentPanelDps / currentSkillDps;
        profile.RuntimeDpsCalibrationFactor = singleFactor;

        if (!RequiresWeaponDpsCalibration(rotation) ||
            !double.IsFinite(currentWeaponDps) || currentWeaponDps <= 0)
        {
            UseSingleAnchor(profile, singleFactor, DpsCalibrationMode.SingleAnchor);
            return;
        }

        ValidatePanelDps(profile.CurrentWeaponAttackDps, "当前武器攻击的游戏面板 DPS");

        var rawSkillAddedDps = currentSkillDps - currentWeaponDps;
        var panelSkillAddedDps = profile.CurrentPanelDps - profile.CurrentWeaponAttackDps;
        var stableDifferenceFloor = Math.Max(1, Math.Abs(currentSkillDps) * 0.05);
        if (Math.Abs(rawSkillAddedDps) < stableDifferenceFloor)
        {
            UseSingleAnchor(profile, singleFactor, DpsCalibrationMode.SingleAnchorFallback);
            return;
        }

        if (rawSkillAddedDps * panelSkillAddedDps <= 0)
            throw new InvalidOperationException("主攻技能 DPS 与武器攻击 DPS 的关系不符合当前技能，请核对两个输入值和所选主攻技能。");

        profile.RuntimeWeaponDpsCalibrationFactor = profile.CurrentWeaponAttackDps / currentWeaponDps;
        profile.RuntimeSkillAddedDpsCalibrationFactor = panelSkillAddedDps / rawSkillAddedDps;
        profile.RuntimeDpsCalibrationMode = DpsCalibrationMode.DualAnchor;
    }

    private static void UseSingleAnchor(
        OptimizationProfile profile,
        double factor,
        DpsCalibrationMode mode)
    {
        profile.RuntimeWeaponDpsCalibrationFactor = factor;
        profile.RuntimeSkillAddedDpsCalibrationFactor = factor;
        profile.RuntimeDpsCalibrationMode = mode;
    }

    private static void ValidatePanelDps(double value, string fieldName)
    {
        if (!double.IsFinite(value) || value <= 0 || value > 10_000_000)
            throw new InvalidOperationException($"必须输入有效的{fieldName}（大于 0，且不超过 10,000,000）。");
    }

    private static SimulationReport ApplyDpsCalibration(
        SimulationReport report,
        SimulationReport weaponReport,
        OptimizationProfile profile)
    {
        if (report.UnmitigatedDps <= 0) return report;
        var calibratedSheetDps =
            weaponReport.UnmitigatedDps * profile.RuntimeWeaponDpsCalibrationFactor +
            (report.UnmitigatedDps - weaponReport.UnmitigatedDps) * profile.RuntimeSkillAddedDpsCalibrationFactor;
        var factor = Math.Max(0, calibratedSheetDps) / report.UnmitigatedDps;
        return ScaleDps(report, factor);
    }

    private static SimulationReport ScaleDps(SimulationReport report, double factor)
    {
        if (Math.Abs(factor - 1) < 1e-9) return report;
        var activeDps = Math.Max(0, report.ExpectedDps - report.RetaliationDps);
        var calibratedActiveDps = activeDps * factor;
        var expectedDps = calibratedActiveDps + report.RetaliationDps;
        var lifeStealEligibleDps = report.LifeStealEligibleDps * factor;
        var sustain = report.HealthRegenPerSecond + lifeStealEligibleDps * report.EffectiveLifeSteal;
        var netIncomingDps = report.IncomingDps - sustain;
        var effectiveHealth = netIncomingDps <= 1e-9
            ? double.PositiveInfinity
            : report.Health * report.RawIncomingDps / netIncomingDps;
        var timeToDeath = netIncomingDps <= 1e-9
            ? double.PositiveInfinity
            : report.Health / netIncomingDps;
        var timeToKill = expectedDps <= 1e-9 || !double.IsFinite(report.TimeToKillSeconds)
            ? report.TimeToKillSeconds
            : report.TimeToKillSeconds * report.ExpectedDps / expectedDps;
        static double CalibrateSample(double sample, double retaliationDps, double calibration) =>
            Math.Max(0, sample - retaliationDps) * calibration + retaliationDps;
        return new SimulationReport
        {
            ExpectedDps = expectedDps,
            RetaliationDps = report.RetaliationDps,
            UnmitigatedDps = report.UnmitigatedDps * factor,
            TimeToKillSeconds = timeToKill,
            EffectiveHealth = effectiveHealth,
            IncomingDps = report.IncomingDps,
            SustainPerSecond = sustain,
            Health = report.Health,
            RawIncomingDps = report.RawIncomingDps,
            HealthRegenPerSecond = report.HealthRegenPerSecond,
            EffectiveLifeSteal = report.EffectiveLifeSteal,
            LifeStealEligibleDps = lifeStealEligibleDps,
            TimeToDeathSeconds = timeToDeath,
            HitChance = report.HitChance,
            CritChance = report.CritChance,
            MonteCarloDpsMean = CalibrateSample(report.MonteCarloDpsMean, report.RetaliationDps, factor),
            MonteCarloDpsLow95 = CalibrateSample(report.MonteCarloDpsLow95, report.RetaliationDps, factor),
            MonteCarloDpsHigh95 = CalibrateSample(report.MonteCarloDpsHigh95, report.RetaliationDps, factor),
            MonteCarloSurvivalMean = report.MonteCarloSurvivalMean,
            UnsupportedEffects = report.UnsupportedEffects
        };
    }

    private static string Signature(IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots) =>
        string.Join(';', slots.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value.Item.Id}:{x.Value.Component?.Id}:{x.Value.Augment?.Id}"));

    private ParallelOptions CreateParallelOptions(CancellationToken cancellationToken) => new()
    {
        MaxDegreeOfParallelism = _maxDegreeOfParallelism,
        CancellationToken = cancellationToken
    };

    private TResult[] ParallelMap<TSource, TResult>(
        IReadOnlyList<TSource> source,
        Func<TSource, TResult> selector,
        CancellationToken cancellationToken)
    {
        var result = new TResult[source.Count];
        Parallel.For(0, source.Count, CreateParallelOptions(cancellationToken), index =>
            result[index] = selector(source[index]));
        return result;
    }

    private static string EquipmentSignature(IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots) =>
        string.Join(';', slots.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value.Item.Id}"));

    private static int StableSeed(string profileName, string signature)
    {
        unchecked
        {
            var hash = 17;
            foreach (var ch in profileName + '|' + signature) hash = hash * 31 + ch;
            return hash;
        }
    }

    private static SkillRotationDefinition ResolveRotation(
        CharacterSnapshot character,
        GameDataCatalog catalog,
        OptimizationProfile profile,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots)
    {
        var selected = character.Skills.FirstOrDefault(x =>
            x.Record.Equals(profile.Rotation.MainSkillRecord, StringComparison.OrdinalIgnoreCase));
        return SkillRotationFactory.Build(character, catalog, selected, slots);
    }

    private static SkillRotationDefinition ResolveWeaponAttackRotation(
        CharacterSnapshot character,
        GameDataCatalog catalog,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots) =>
        SkillRotationFactory.Build(character, catalog, null, slots);

    private static List<BeamState> SelectDiverseBeam(IReadOnlyList<BeamState> states, int beamWidth)
    {
        var ordered = states.OrderByDescending(x => x.Heuristic)
            .ThenBy(x => Signature(x.Slots), StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length <= beamWidth) return ordered.ToList();

        var eliteCount = Math.Max(1, (int)Math.Round(beamWidth * 0.7));
        var selected = ordered.Take(eliteCount).ToList();
        var signatures = selected.Select(x => Signature(x.Slots)).ToHashSet(StringComparer.Ordinal);
        foreach (var state in ordered.Skip(eliteCount)
                     .GroupBy(x => DiversitySignature(x.Slots), StringComparer.Ordinal)
                     .Select(x => x.First())
                     .OrderByDescending(x => x.Heuristic))
        {
            if (selected.Count >= beamWidth) break;
            if (signatures.Add(Signature(state.Slots))) selected.Add(state);
        }
        foreach (var state in ordered)
        {
            if (selected.Count >= beamWidth) break;
            if (signatures.Add(Signature(state.Slots))) selected.Add(state);
        }
        return selected;
    }

    private static string DiversitySignature(IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots)
    {
        var weapons = string.Join(',', new[] { EquipmentSlot.MainHand, EquipmentSlot.OffHand }
            .Where(slots.ContainsKey).Select(x => slots[x].Item.Id));
        var sets = string.Join(',', slots.Values.Where(x => !string.IsNullOrWhiteSpace(x.Item.SetId))
            .GroupBy(x => x.Item.SetId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key}:{x.Count()}"));
        var interactions = string.Join(',', slots.Values.Select(InteractionSignature)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return $"{weapons}|{sets}|{interactions}";
    }

    private static IReadOnlyList<LoadoutVariant> DiversifiedLocalCandidates(
        IReadOnlyList<LoadoutVariant> variants,
        int limit)
    {
        if (variants.Count <= limit) return variants;
        var selected = variants.Take(limit / 2).ToList();
        var seen = selected.ToHashSet();
        var remaining = limit - selected.Count;
        for (var index = 0; index < remaining; index++)
        {
            var position = (int)Math.Round(index * (variants.Count - 1d) / Math.Max(1, remaining - 1));
            if (seen.Add(variants[position])) selected.Add(variants[position]);
        }
        return selected;
    }

    private sealed record BeamState(Dictionary<EquipmentSlot, LoadoutVariant> Slots, double Heuristic);
}
