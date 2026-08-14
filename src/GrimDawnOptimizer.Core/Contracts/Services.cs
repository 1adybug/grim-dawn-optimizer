using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Contracts;

public interface IGameDataSource
{
    Task<DiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<GameDataCatalog> LoadCatalogAsync(GameInstallation installation, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    IReadOnlyList<AttachmentDefinition> GetUnlockedAttachments(CharacterSnapshot character, GameDataCatalog catalog);
}

public interface ISaveReader
{
    Task<IReadOnlyList<CharacterSnapshot>> ReadCharactersAsync(SaveLocation location, GameDataCatalog catalog, CancellationToken cancellationToken = default);
}

public interface ICharacterStatEngine
{
    CalculatedLoadout Calculate(CharacterSnapshot character, IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots, GameDataCatalog catalog);
}

public interface ICombatSimulator
{
    SimulationReport Simulate(CharacterSnapshot character, CalculatedLoadout loadout, CombatScenario scenario, SkillRotationDefinition rotation);
    Task<SimulationReport> RunMonteCarloAsync(CharacterSnapshot character, CalculatedLoadout loadout, CombatScenario scenario, SkillRotationDefinition rotation, int trials, int seed, CancellationToken cancellationToken = default);
}

public interface ILoadoutOptimizer
{
    Task<OptimizationRun> OptimizeAsync(
        CharacterSnapshot character,
        IReadOnlyList<ItemInstance> itemPool,
        IReadOnlyList<AttachmentDefinition> attachments,
        GameDataCatalog catalog,
        OptimizationProfile profile,
        IProgress<SearchProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IProfileStore
{
    Task<IReadOnlyList<OptimizationProfile>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IEnumerable<OptimizationProfile> profiles, CancellationToken cancellationToken = default);
}

public interface ISourceHistoryStore
{
    Task<SourceHistorySettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SourceHistorySettings settings, CancellationToken cancellationToken = default);
}
