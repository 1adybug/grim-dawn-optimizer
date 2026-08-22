using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GrimDawnOptimizer.Core.Contracts;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace GrimDawnOptimizer.Core.Services;

public sealed class GameDataSource : IGameDataSource
{
    private readonly string _appDataRoot;
    private readonly ArzArchiveReader _arz = new();
    private readonly ArcArchiveReader _arc = new();

    public GameDataSource(string? appDataRoot = null)
    {
        _appDataRoot = appDataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrimDawnOptimizer");
    }

    public Task<DiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var installations = DiscoverInstallations();
        var saves = DiscoverSaveLocations();
        return new DiscoveryResult(installations, saves);
    }, cancellationToken);

    public async Task<GameDataCatalog> LoadCatalogAsync(
        GameInstallation installation,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = installation.DataFiles.Where(File.Exists).ToArray();
        var sourceHash = await HashFilesAsync(files, cancellationToken);
        var cache = new CatalogCache(Path.Combine(_appDataRoot, "catalog.db"));
        GameDataCatalog? cached = null;
        try
        {
            cached = await cache.TryGetAsync(installation.RootPath, sourceHash, cancellationToken);
        }
        catch (Exception ex) when (IsRecoverableCacheFailure(ex))
        {
            // The catalog cache is disposable. A stale schema, corrupt payload, locked file or
            // failed deserialization must never prevent the read-only game import itself.
        }
        if (cached is not null) return cached;

        var records = new Dictionary<string, GameRecord>(StringComparer.OrdinalIgnoreCase);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            if (file.EndsWith(".arz", StringComparison.OrdinalIgnoreCase))
                foreach (var (key, value) in _arz.Read(file, cancellationToken))
                    records[key] = records.TryGetValue(key, out var inherited)
                        ? MergeRecord(inherited, value)
                        : value;
            else if (file.EndsWith(".arc", StringComparison.OrdinalIgnoreCase))
                foreach (var (key, value) in _arc.ReadTags(file, cancellationToken)) tags[key] = value;
            progress?.Report((i + 1d) / Math.Max(1, files.Length));
        }

        var setBonuses = BuildSetBonuses(records, tags);
        var catalog = new GameDataCatalog
        {
            Version = installation.Version,
            Records = records,
            Tags = tags,
            Attachments = BuildAttachments(records, tags),
            SetBonuses = setBonuses.Stats,
            SetBonusEffects = setBonuses.Effects,
            SetBonusUnsupportedEffects = setBonuses.UnsupportedEffects
        };
        try
        {
            await cache.PutAsync(installation.RootPath, sourceHash, catalog, cancellationToken);
        }
        catch (Exception ex) when (IsRecoverableCacheFailure(ex))
        {
            // Parsing succeeded, so return the in-memory catalog even if caching is unavailable.
        }
        return catalog;
    }

    private static GameRecord MergeRecord(GameRecord inherited, GameRecord overlay)
    {
        var numeric = inherited.NumericValues.Clone();
        foreach (var (key, value) in overlay.NumericValues.Values) numeric.Set(key, value);

        var series = new Dictionary<string, IReadOnlyList<double>>(
            inherited.NumericSeries, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in overlay.NumericSeries) series[key] = values;

        var text = new Dictionary<string, IReadOnlyList<string>>(
            inherited.TextValues, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in overlay.TextValues) text[key] = values;

        return new GameRecord
        {
            RecordName = overlay.RecordName,
            RecordType = string.IsNullOrWhiteSpace(overlay.RecordType) ? inherited.RecordType : overlay.RecordType,
            NumericValues = numeric,
            NumericSeries = series,
            TextValues = text
        };
    }

    private static bool IsRecoverableCacheFailure(Exception ex) => ex is
        SqliteException or JsonException or InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException;

    public IReadOnlyList<AttachmentDefinition> GetUnlockedAttachments(CharacterSnapshot character, GameDataCatalog catalog)
    {
        var owned = CollectOwnedAttachments(character);
        var result = new List<AttachmentDefinition>();
        foreach (var definition in catalog.Attachments)
        {
            var recordId = AttachmentRecordId(definition);
            var matchingOwned = owned.Where(x =>
                x.Kind == definition.Kind &&
                string.Equals(x.RecordId, recordId, StringComparison.OrdinalIgnoreCase) &&
                x.Source is not null).ToArray();
            var baseOwned = matchingOwned.Where(x =>
                definition.Kind == AttachmentKind.Augment || string.IsNullOrWhiteSpace(x.CompletionBonusRecord)).ToArray();
            var unlockedStatic = definition.LevelRequirement <= character.Level
                ? definition.AcquisitionSources.Where(x => IsStaticSourceUnlocked(x, definition.Kind, character)).ToArray()
                : [];
            var baseSources = baseOwned.Select(x => x.Source!).Concat(unlockedStatic).Distinct().ToArray();
            if (baseSources.Length > 0)
                result.Add(CloneAvailableAttachment(definition, definition.Id, string.Empty, baseOwned.Length > 0, baseSources, catalog));

            if (definition.Kind != AttachmentKind.Component) continue;
            foreach (var completionGroup in matchingOwned
                         .Where(x => !string.IsNullOrWhiteSpace(x.CompletionBonusRecord))
                         .GroupBy(x => x.CompletionBonusRecord, StringComparer.OrdinalIgnoreCase))
            {
                var sources = completionGroup.Select(x => x.Source!).Distinct().ToArray();
                result.Add(CloneAvailableAttachment(
                    definition,
                    $"{recordId}|owned:{completionGroup.Key}",
                    completionGroup.Key,
                    true,
                    sources,
                    catalog));
            }
        }
        return result;
    }

    private static IReadOnlyList<OwnedAttachmentInstance> CollectOwnedAttachments(CharacterSnapshot character)
    {
        var currentSlots = character.CurrentLoadout.ToDictionary(x => x.Value, x => x.Key, StringComparer.OrdinalIgnoreCase);
        var fallback = character.Items.SelectMany(item =>
        {
            EquipmentSlot? equippedSlot = currentSlots.TryGetValue(item.Id, out var currentSlot) ? currentSlot : null;
            var source = new AttachmentAcquisitionSource(
                AttachmentAcquisitionKind.Installed,
                item.Source,
                item.Id,
                item.Name,
                equippedSlot);
            return new[]
            {
                new OwnedAttachmentInstance(item.ComponentRecord, AttachmentKind.Component, item.CompletionBonusRecord, source),
                new OwnedAttachmentInstance(item.AugmentRecord, AttachmentKind.Augment, Source: source)
            };
        }).Where(x => !string.IsNullOrWhiteSpace(x.RecordId) && !character.OwnedAttachments.Any(owned =>
            owned.Kind == x.Kind &&
            string.Equals(owned.RecordId, x.RecordId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(owned.CompletionBonusRecord, x.CompletionBonusRecord, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(owned.Source?.HostItemId, x.Source?.HostItemId, StringComparison.OrdinalIgnoreCase)));
        return character.OwnedAttachments.Concat(fallback)
            .Where(x => !string.IsNullOrWhiteSpace(x.RecordId))
            .Distinct()
            .ToArray();
    }

    private static bool IsStaticSourceUnlocked(
        AttachmentAcquisitionSource source,
        AttachmentKind kind,
        CharacterSnapshot character) => source.Kind switch
        {
            AttachmentAcquisitionKind.Blacksmith =>
                kind == AttachmentKind.Component &&
                !string.IsNullOrWhiteSpace(source.FormulaRecord) &&
                (source.IsAlwaysAvailable || character.KnownFormulas.Contains(source.FormulaRecord)),
            AttachmentAcquisitionKind.FactionVendor =>
                kind == AttachmentKind.Augment &&
                !string.IsNullOrWhiteSpace(source.FactionReputationKey) &&
                character.FactionReputation.GetValueOrDefault(source.FactionReputationKey) >= source.RequiredReputation,
            _ => false
        };

    private static AttachmentDefinition CloneAvailableAttachment(
        AttachmentDefinition source,
        string id,
        string completionBonusRecord,
        bool isOwned,
        IReadOnlyList<AttachmentAcquisitionSource> acquisitionSources,
        GameDataCatalog catalog)
    {
        var stats = source.Stats.Clone();
        var effects = source.Effects.ToList();
        var unsupportedEffects = source.UnsupportedEffects.ToList();
        if (!string.IsNullOrWhiteSpace(completionBonusRecord) &&
            catalog.Records.TryGetValue(completionBonusRecord, out var completionBonus))
        {
            stats.Add(GameStatTranslator.Translate([completionBonus]));
            effects.AddRange(SaveReader.BuildTriggeredEffects([completionBonus], catalog));
            unsupportedEffects.AddRange(BuildUnsupportedEffects(
                [completionBonus], catalog, "未建模完成奖励"));
        }

        return new AttachmentDefinition
        {
            Id = id,
            RecordId = AttachmentRecordId(source),
            CompletionBonusRecord = completionBonusRecord,
            IsOwned = isOwned,
            Name = source.Name,
            Kind = source.Kind,
            CompatibleSlots = source.CompatibleSlots,
            CompatibleItemQualifiers = source.CompatibleItemQualifiers,
            LevelRequirement = source.LevelRequirement,
            RequiredFormula = source.RequiredFormula,
            RequiredFaction = source.RequiredFaction,
            RequiredReputation = source.RequiredReputation,
            AcquisitionSources = acquisitionSources,
            Stats = stats,
            Effects = effects,
            UnsupportedEffects = unsupportedEffects
        };
    }

    internal static string AttachmentRecordId(AttachmentDefinition attachment) =>
        string.IsNullOrWhiteSpace(attachment.RecordId) ? attachment.Id : attachment.RecordId;

    private static IReadOnlyList<GameInstallation> DiscoverInstallations()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString();
        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            candidates.Add(Path.Combine(steamPath, "steamapps", "common", "Grim Dawn"));
            var libraries = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraries))
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\""))
                    candidates.Add(Path.Combine(match.Groups["path"].Value.Replace("\\\\", "\\"), "steamapps", "common", "Grim Dawn"));
            }
        }

        foreach (var keyPath in new[]
        {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\Games\1449651388",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\GOG.com\Games\1449651388"
        })
        {
            var gog = Registry.GetValue(keyPath, "path", null)?.ToString();
            if (!string.IsNullOrWhiteSpace(gog)) candidates.Add(gog);
        }

        return candidates.Where(Directory.Exists).Select(CreateInstallation).ToArray();
    }

    public static GameInstallation CreateInstallation(string root)
    {
        var files = new List<string>();
        foreach (var relative in new[]
        {
            @"database\database.arz", @"gdx1\database\GDX1.arz", @"gdx2\database\GDX2.arz", @"gdx3\database\GDX3.arz"
        })
        {
            var path = Path.Combine(root, relative);
            if (File.Exists(path)) files.Add(path);
        }

        var resourceDirectories = new[] { "resources", @"gdx1\resources", @"gdx2\resources", @"gdx3\resources" }
            .Select(x => Path.Combine(root, x))
            .Where(Directory.Exists)
            .ToArray();
        // Load every English table first as a fallback, then let Chinese tables
        // override matching tags. Current Grim Dawn builds use Text_ZH.arc;
        // Text_CH.arc is retained for older/community layouts.
        foreach (var archiveName in new[] { "Text_EN.arc", "Text_CH.arc", "Text_ZH.arc" })
        {
            foreach (var directory in resourceDirectories)
                files.AddRange(Directory.GetFiles(directory, archiveName, SearchOption.TopDirectoryOnly));
        }
        var version = File.Exists(Path.Combine(root, "Grim Dawn.exe"))
            ? System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(root, "Grim Dawn.exe")).FileVersion ?? "unknown"
            : "unknown";
        return new GameInstallation(Path.GetFullPath(root), "自动/手动", version, files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IReadOnlyList<SaveLocation> DiscoverSaveLocations()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Grim Dawn", "save")
        };
        var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString();
        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            var userData = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(userData))
                foreach (var user in Directory.EnumerateDirectories(userData))
                    roots.Add(Path.Combine(user, "219990", "remote", "save"));
        }

        return roots.Where(Directory.Exists).Select(root =>
        {
            var chars = Directory.EnumerateFiles(root, "player.gdc", SearchOption.AllDirectories)
                .Where(x => x.Contains($"{Path.DirectorySeparatorChar}main{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var gst = Path.Combine(root, "transfer.gst");
            var gsh = Path.Combine(root, "transfer.gsh");
            return new SaveLocation(
                root,
                root.Contains("userdata", StringComparison.OrdinalIgnoreCase) ? "Steam Cloud" : "本地文档",
                chars,
                File.Exists(gst) ? gst : File.Exists(gsh) ? gsh : null,
                File.Exists(Path.Combine(root, "formulas.gst")) ? Path.Combine(root, "formulas.gst") : null);
        }).ToArray();
    }

    private static IReadOnlyList<AttachmentDefinition> BuildAttachments(
        IReadOnlyDictionary<string, GameRecord> records,
        IReadOnlyDictionary<string, string> tags)
    {
        var result = new List<AttachmentDefinition>();
        var catalogView = new GameDataCatalog { Records = records, Tags = tags };
        var acquisitionSources = BuildStaticAttachmentSources(records, tags);
        foreach (var record in records.Values)
        {
            var @class = record.Text("Class");
            var kind = @class switch
            {
                "ItemRelic" => AttachmentKind.Component,
                "ItemEnchantment" => AttachmentKind.Augment,
                _ => (AttachmentKind?)null
            };
            if (kind is null) continue;
            var nameTag = FirstNonEmpty(
                record.Text(kind == AttachmentKind.Component ? "relicName" : "itemNameTag"),
                record.Text("description"));
            var cleanedNameTag = nameTag.Replace("^k", string.Empty, StringComparison.OrdinalIgnoreCase);
            var name = tags.GetValueOrDefault(cleanedNameTag,
                string.IsNullOrWhiteSpace(cleanedNameTag)
                    ? Path.GetFileNameWithoutExtension(record.RecordName)
                    : cleanedNameTag);
            var qualifiers = ResolveAttachmentQualifiers(record);
            var sources = acquisitionSources.GetValueOrDefault(record.RecordName, []);
            var formula = sources.FirstOrDefault(x => x.Kind == AttachmentAcquisitionKind.Blacksmith)?.FormulaRecord ?? string.Empty;
            var faction = sources.FirstOrDefault(x => x.Kind == AttachmentAcquisitionKind.FactionVendor);
            result.Add(new AttachmentDefinition
            {
                Id = record.RecordName,
                RecordId = record.RecordName,
                Name = name,
                Kind = kind.Value,
                CompatibleSlots = ResolveAttachmentSlots(qualifiers),
                CompatibleItemQualifiers = qualifiers,
                LevelRequirement = (int)record.NumericValues["levelRequirement"],
                RequiredFormula = formula,
                RequiredFaction = faction?.FactionReputationKey ?? string.Empty,
                RequiredReputation = faction?.RequiredReputation ?? 0,
                AcquisitionSources = sources,
                Stats = GameStatTranslator.Translate([record]),
                Effects = SaveReader.BuildTriggeredEffects([record], catalogView),
                UnsupportedEffects = BuildUnsupportedEffects([record], catalogView, "未建模附件")
            });
        }
        return result;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<AttachmentAcquisitionSource>> BuildStaticAttachmentSources(
        IReadOnlyDictionary<string, GameRecord> records,
        IReadOnlyDictionary<string, string> tags)
    {
        var result = new Dictionary<string, List<AttachmentAcquisitionSource>>(StringComparer.OrdinalIgnoreCase);
        void Add(string attachmentRecord, AttachmentAcquisitionSource source)
        {
            if (string.IsNullOrWhiteSpace(attachmentRecord)) return;
            if (!result.TryGetValue(attachmentRecord, out var sources))
                result[attachmentRecord] = sources = [];
            if (!sources.Contains(source)) sources.Add(source);
        }

        foreach (var formula in records.Values)
        foreach (var artifact in formula.TextValues.GetValueOrDefault("artifactName", []))
            Add(artifact, new AttachmentAcquisitionSource(
                AttachmentAcquisitionKind.Blacksmith,
                FormulaRecord: formula.RecordName,
                IsAlwaysAvailable: formula.RecordName.Contains(
                    "/component/craft_component_base_", StringComparison.OrdinalIgnoreCase)));

        var factionIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (records.TryGetValue("records/game/gamefactions.dbr", out var gameFactions))
        {
            var factionRecords = gameFactions.TextValues.GetValueOrDefault("factions", []);
            for (var index = 0; index < factionRecords.Count; index++)
                if (!string.IsNullOrWhiteSpace(factionRecords[index])) factionIndices[factionRecords[index]] = index;
        }

        var tiers = new[]
        {
            new FactionMarketTier("friendlyNormalTable", 1_500, "tagFactionStateFriendly", "友好"),
            new FactionMarketTier("respectedNormalTable", 5_000, "tagFactionStateTrusted", "尊敬"),
            new FactionMarketTier("honoredNormalTable", 10_000, "tagFactionStateHonored", "崇敬"),
            new FactionMarketTier("reveredNormalTable", 25_000, "tagFactionStateRevered", "崇拜")
        };
        foreach (var merchant in records.Values)
        {
            var factionRecord = FirstNonEmpty(merchant.Text("myFaction"), merchant.Text("factions"));
            var marketRecord = merchant.Text("marketFileName");
            if (string.IsNullOrWhiteSpace(factionRecord) || string.IsNullOrWhiteSpace(marketRecord) ||
                !records.TryGetValue(marketRecord, out var market)) continue;

            var factionName = ResolveFactionName(factionRecord, records, tags);
            var reputationKey = factionIndices.TryGetValue(factionRecord, out var factionIndex)
                ? $"faction:{factionIndex}"
                : ResolveFactionReputationKey(factionRecord, records);
            foreach (var tier in tiers)
            foreach (var tableRecord in market.TextValues.GetValueOrDefault(tier.TableField, []))
            {
                if (!records.TryGetValue(tableRecord, out var table)) continue;
                var reputationName = tags.GetValueOrDefault(tier.DisplayTag, tier.FallbackName);
                foreach (var itemRecord in table.TextValues.GetValueOrDefault("marketStaticItems", []))
                    Add(itemRecord, new AttachmentAcquisitionSource(
                        AttachmentAcquisitionKind.FactionVendor,
                        FactionRecord: factionRecord,
                        FactionName: factionName,
                        FactionReputationKey: reputationKey,
                        ReputationName: reputationName,
                        RequiredReputation: tier.RequiredReputation));
            }
        }

        return result.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<AttachmentAcquisitionSource>)x.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveFactionName(
        string factionRecord,
        IReadOnlyDictionary<string, GameRecord> records,
        IReadOnlyDictionary<string, string> tags)
    {
        if (!records.TryGetValue(factionRecord, out var faction))
            return Path.GetFileNameWithoutExtension(factionRecord);
        var factionTag = FirstNonEmpty(faction.Text("factionTag"), faction.Text("myFaction"));
        foreach (var candidate in new[]
                 {
                     factionTag,
                     factionTag.StartsWith("tag", StringComparison.OrdinalIgnoreCase) ? factionTag : $"tagFaction{factionTag}"
                 }.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (tags.TryGetValue(candidate, out var name) && !string.IsNullOrWhiteSpace(name)) return name;
        return string.IsNullOrWhiteSpace(factionTag)
            ? Path.GetFileNameWithoutExtension(factionRecord)
            : factionTag;
    }

    private static string ResolveFactionReputationKey(
        string factionRecord,
        IReadOnlyDictionary<string, GameRecord> records)
    {
        if (!records.TryGetValue(factionRecord, out var faction)) return string.Empty;
        var factionId = FirstNonEmpty(faction.Text("myFaction"), faction.Text("factionTag"));
        if (factionId.StartsWith("User", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(factionId[4..], out var userIndex))
            return $"faction:{userIndex + 6}";
        var builtInIndex = factionId.ToLowerInvariant() switch
        {
            "player" => 0,
            "survivors" => 1,
            "aetherials" => 2,
            "cthonians" => 3,
            "outlaws" => 4,
            "beasts" => 5,
            _ => -1
        };
        return builtInIndex < 0 ? string.Empty : $"faction:{builtInIndex}";
    }

    private sealed record FactionMarketTier(
        string TableField,
        double RequiredReputation,
        string DisplayTag,
        string FallbackName);

    private static IReadOnlyList<UnsupportedEffect> BuildUnsupportedEffects(
        IReadOnlyList<GameRecord> records,
        GameDataCatalog catalog,
        string descriptionPrefix)
    {
        var result = new List<UnsupportedEffect>();
        foreach (var record in records)
        {
            var fields = GameStatTranslator.FindUnsupportedCombatFields(record);
            if (fields.Count == 0) continue;
            var display = string.Join(", ", fields.Take(6));
            if (fields.Count > 6) display += "…";
            result.Add(new UnsupportedEffect(record.RecordName,
                $"{descriptionPrefix}词条：{display}", ["dps", "effectiveHealth"]));
        }
        result.AddRange(SaveReader.FindUnsupportedItemSkillEffects(records, catalog,
            $"{descriptionPrefix}技能"));
        return result;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    internal static IReadOnlySet<string> ResolveAttachmentQualifiers(GameRecord record) =>
        AttachmentQualifierKeys.Where(x => record.NumericValues[x] > 0.5)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlySet<EquipmentSlot> ResolveAttachmentSlots(IReadOnlySet<string> qualifiers)
    {
        var result = new HashSet<EquipmentSlot>();
        foreach (var qualifier in qualifiers)
        {
            var slot = qualifier.ToLowerInvariant() switch
            {
                "head" => EquipmentSlot.Head, "shoulders" => EquipmentSlot.Shoulders,
                "hands" => EquipmentSlot.Hands, "chest" => EquipmentSlot.Chest,
                "waist" => EquipmentSlot.Waist, "legs" => EquipmentSlot.Legs,
                "feet" => EquipmentSlot.Feet, "amulet" => EquipmentSlot.Amulet,
                "bracelet" or "ring" => EquipmentSlot.Ring, "medal" => EquipmentSlot.Medal,
                "shield" or "offhand" => EquipmentSlot.OffHand,
                _ when WeaponAttachmentQualifiers.Contains(qualifier) => EquipmentSlot.MainHand,
                _ => (EquipmentSlot?)null
            };
            if (slot is not null) result.Add(slot.Value);
        }
        return result;
    }

    internal static readonly string[] AttachmentQualifierKeys =
    [
        "amulet", "bracelet", "ring", "medal", "head", "shoulders", "hands", "chest",
        "waist", "legs", "feet", "shield", "offhand", "spear", "staff", "axe", "mace",
        "sword", "dagger", "scepter", "ranged1h", "ranged2h", "axe2h", "mace2h",
        "sword2h", "spear2h"
    ];

    private static readonly HashSet<string> WeaponAttachmentQualifiers = new(
        ["spear", "staff", "axe", "mace", "sword", "dagger", "scepter", "ranged1h", "ranged2h", "axe2h", "mace2h", "sword2h", "spear2h"],
        StringComparer.OrdinalIgnoreCase);

    private static SetBonusData BuildSetBonuses(
        IReadOnlyDictionary<string, GameRecord> records,
        IReadOnlyDictionary<string, string> tags)
    {
        var stats = new Dictionary<string, IReadOnlyDictionary<int, StatBlock>>(StringComparer.OrdinalIgnoreCase);
        var effects = new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyList<TriggeredEffect>>>(StringComparer.OrdinalIgnoreCase);
        var unsupported = new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyList<UnsupportedEffect>>>(StringComparer.OrdinalIgnoreCase);
        var catalogView = new GameDataCatalog { Records = records, Tags = tags };
        foreach (var record in records.Values.Where(x => x.RecordName.Contains("/itemsets/", StringComparison.OrdinalIgnoreCase)))
        {
            var bonuses = new Dictionary<int, StatBlock>();
            var bonusEffects = new Dictionary<int, IReadOnlyList<TriggeredEffect>>();
            var bonusUnsupported = new Dictionary<int, IReadOnlyList<UnsupportedEffect>>();
            for (var pieces = 2; pieces <= 9; pieces++)
            {
                var bonusRecord = record.Text($"setBonus{pieces}");
                if (!string.IsNullOrWhiteSpace(bonusRecord) && records.TryGetValue(bonusRecord, out var bonus))
                {
                    bonuses[pieces] = GameStatTranslator.Translate([bonus]);
                    bonusEffects[pieces] = SaveReader.BuildTriggeredEffects([bonus], catalogView);
                    bonusUnsupported[pieces] = BuildUnsupportedEffects(
                        [bonus], catalogView, $"未建模套装 {pieces} 件奖励");
                }
            }
            if (bonuses.Count == 0) continue;
            stats[record.RecordName] = bonuses;
            effects[record.RecordName] = bonusEffects;
            unsupported[record.RecordName] = bonusUnsupported;
        }
        return new SetBonusData(stats, effects, unsupported);
    }

    private sealed record SetBonusData(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, StatBlock>> Stats,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyList<TriggeredEffect>>> Effects,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyList<UnsupportedEffect>>> UnsupportedEffects);

    private static async Task<string> HashFilesAsync(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFullPath(file)));
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, useAsync: true);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class CatalogCache(string path)
    {
        // GDOC23 additionally retains blueprint and faction-market relationships used
        // to turn every recommended attachment into an actionable acquisition source.
        private static readonly byte[] PayloadHeader = "GDOC23"u8.ToArray();
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public async Task<GameDataCatalog?> TryGetAsync(string key, string hash, CancellationToken cancellationToken)
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM catalog_cache WHERE cache_key=$key AND source_hash=$hash";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$hash", hash);
            var payload = await command.ExecuteScalarAsync(cancellationToken);
            return payload switch
            {
                null or DBNull => null,
                byte[] compressed => DeserializeCompressed(compressed),
                string => throw new InvalidDataException("旧版目录缓存需要重建"),
                _ => null
            };
        }

        public async Task PutAsync(string key, string hash, GameDataCatalog catalog, CancellationToken cancellationToken)
        {
            await using var connection = await OpenAsync(cancellationToken);
            var payload = SerializeCompressed(catalog);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO catalog_cache(cache_key,source_hash,payload,created_at)
                VALUES($key,$hash,$payload,$now)
                ON CONFLICT(cache_key) DO UPDATE SET
                    source_hash=excluded.source_hash,
                    payload=excluded.payload,
                    created_at=excluded.created_at
                """;
            command.Parameters.Add("$key", SqliteType.Text).Value = key;
            command.Parameters.Add("$hash", SqliteType.Text).Value = hash;
            command.Parameters.Add("$payload", SqliteType.Blob).Value = payload;
            command.Parameters.Add("$now", SqliteType.Integer).Value = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static byte[] SerializeCompressed(GameDataCatalog catalog)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions);
            using var output = new MemoryStream();
            output.Write(PayloadHeader);
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(json);
            return output.ToArray();
        }

        private static GameDataCatalog? DeserializeCompressed(byte[] payload)
        {
            if (payload.Length <= PayloadHeader.Length || !payload.AsSpan(0, PayloadHeader.Length).SequenceEqual(PayloadHeader))
                throw new InvalidDataException("不支持的目录缓存载荷版本");
            using var input = new MemoryStream(payload, PayloadHeader.Length, payload.Length - PayloadHeader.Length, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<GameDataCatalog>(gzip, JsonOptions);
        }

        private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();
            var connection = new SqliteConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE IF NOT EXISTS catalog_cache(cache_key TEXT PRIMARY KEY, source_hash TEXT NOT NULL, payload TEXT NOT NULL, created_at INTEGER NOT NULL)";
                await command.ExecuteNonQueryAsync(cancellationToken);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }
}
