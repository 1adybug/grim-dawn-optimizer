using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

internal static class ItemSetRules
{
    // Two physical copies of the same set ring/weapon are one distinct set
    // piece. Their ordinary stats still stack independently.
    internal static IReadOnlyDictionary<string, int> CountPieces(IEnumerable<ItemInstance> items) =>
        items.Where(item => !string.IsNullOrWhiteSpace(item.SetId))
            .GroupBy(item => item.SetId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.Select(item => item.BaseRecord).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                StringComparer.OrdinalIgnoreCase);
}
