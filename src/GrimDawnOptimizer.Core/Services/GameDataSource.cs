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
                foreach (var (key, value) in _arz.Read(file, cancellationToken)) records[key] = value;
            else if (file.EndsWith(".arc", StringComparison.OrdinalIgnoreCase))
                foreach (var (key, value) in _arc.ReadTags(file, cancellationToken)) tags[key] = value;
            progress?.Report((i + 1d) / Math.Max(1, files.Length));
        }

        var catalog = new GameDataCatalog
        {
            Version = installation.Version,
            Records = records,
            Tags = tags,
            Attachments = BuildAttachments(records, tags),
            SetBonuses = BuildSetBonuses(records)
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

    private static bool IsRecoverableCacheFailure(Exception ex) => ex is
        SqliteException or JsonException or InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException;

    public IReadOnlyList<AttachmentDefinition> GetUnlockedAttachments(CharacterSnapshot character, GameDataCatalog catalog)
    {
        var unlocked = catalog.Attachments.Where(x =>
            x.LevelRequirement <= character.Level &&
              (string.IsNullOrWhiteSpace(x.RequiredFormula) || character.KnownFormulas.Contains(x.RequiredFormula)) &&
              (string.IsNullOrWhiteSpace(x.RequiredFaction) || character.FactionReputation.GetValueOrDefault(x.RequiredFaction) >= x.RequiredReputation));
        return unlocked.Concat(BuildOwnedAttachmentVariants(character, catalog))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
    }

    private static IReadOnlyList<AttachmentDefinition> BuildOwnedAttachmentVariants(
        CharacterSnapshot character,
        GameDataCatalog catalog)
    {
        var result = new List<AttachmentDefinition>();
        var owned = character.OwnedAttachments.Concat(character.Items.SelectMany(item => new[]
        {
            new OwnedAttachmentInstance(item.ComponentRecord, AttachmentKind.Component, item.CompletionBonusRecord),
            new OwnedAttachmentInstance(item.AugmentRecord, AttachmentKind.Augment)
        })).Where(x => !string.IsNullOrWhiteSpace(x.RecordId)).Distinct();
        foreach (var attachment in owned)
            AddOwned(attachment.RecordId, attachment.CompletionBonusRecord, attachment.Kind);
        return result;

        void AddOwned(string recordId, string completionBonusRecord, AttachmentKind kind)
        {
            if (string.IsNullOrWhiteSpace(recordId)) return;
            var source = catalog.Attachments.FirstOrDefault(x =>
                x.Kind == kind && string.Equals(AttachmentRecordId(x), recordId, StringComparison.OrdinalIgnoreCase));
            if (source is null) return;

            var stats = source.Stats.Clone();
            if (!string.IsNullOrWhiteSpace(completionBonusRecord) &&
                catalog.Records.TryGetValue(completionBonusRecord, out var completionBonus))
                stats.Add(GameStatTranslator.Translate([completionBonus]));

            var suffix = string.IsNullOrWhiteSpace(completionBonusRecord) ? "base" : completionBonusRecord;
            result.Add(new AttachmentDefinition
            {
                Id = $"{recordId}|owned:{suffix}",
                RecordId = recordId,
                CompletionBonusRecord = completionBonusRecord,
                IsOwned = true,
                Name = source.Name,
                Kind = source.Kind,
                CompatibleSlots = source.CompatibleSlots,
                LevelRequirement = source.LevelRequirement,
                RequiredFormula = source.RequiredFormula,
                RequiredFaction = source.RequiredFaction,
                RequiredReputation = source.RequiredReputation,
                Stats = stats,
                Effects = source.Effects,
                UnsupportedEffects = source.UnsupportedEffects
            });
        }
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

        foreach (var directory in new[] { "resources", @"gdx1\resources", @"gdx2\resources", @"gdx3\resources" })
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path)) continue;
            files.AddRange(Directory.GetFiles(path, "Text_EN.arc", SearchOption.TopDirectoryOnly));
            files.AddRange(Directory.GetFiles(path, "Text_CH.arc", SearchOption.TopDirectoryOnly));
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
        var allSlots = Enum.GetValues<EquipmentSlot>().Where(x => x is not (EquipmentSlot.Relic or EquipmentSlot.RingLeft or EquipmentSlot.RingRight)).ToHashSet();
        var result = new List<AttachmentDefinition>();
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
            var nameTag = record.Text(kind == AttachmentKind.Component ? "relicName" : "itemNameTag");
            var name = tags.GetValueOrDefault(nameTag, Path.GetFileNameWithoutExtension(record.RecordName));
            result.Add(new AttachmentDefinition
            {
                Id = record.RecordName,
                RecordId = record.RecordName,
                Name = name,
                Kind = kind.Value,
                CompatibleSlots = ResolveAttachmentSlots(record.RecordName, allSlots),
                LevelRequirement = (int)record.NumericValues["levelRequirement"],
                RequiredFormula = record.Text("artifactFormula"),
                RequiredFaction = record.Text("factionName"),
                RequiredReputation = record.NumericValues["factionValue"],
                Stats = GameStatTranslator.Translate([record])
            });
        }
        return result;
    }

    private static IReadOnlySet<EquipmentSlot> ResolveAttachmentSlots(string recordName, HashSet<EquipmentSlot> allSlots)
    {
        var path = recordName.ToLowerInvariant();
        if (path.Contains("weapon")) return new HashSet<EquipmentSlot> { EquipmentSlot.MainHand, EquipmentSlot.OffHand };
        if (path.Contains("jewelry") || path.Contains("ring") || path.Contains("amulet"))
            return new HashSet<EquipmentSlot> { EquipmentSlot.Ring, EquipmentSlot.Amulet, EquipmentSlot.Medal };
        if (path.Contains("shield") || path.Contains("offhand")) return new HashSet<EquipmentSlot> { EquipmentSlot.OffHand };
        if (path.Contains("armor")) return allSlots.Where(x => x is not (EquipmentSlot.MainHand or EquipmentSlot.OffHand or EquipmentSlot.Ring or EquipmentSlot.Amulet or EquipmentSlot.Medal)).ToHashSet();
        return allSlots;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<int, StatBlock>> BuildSetBonuses(IReadOnlyDictionary<string, GameRecord> records)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<int, StatBlock>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.Values.Where(x => x.RecordName.Contains("/itemsets/", StringComparison.OrdinalIgnoreCase)))
        {
            var bonuses = new Dictionary<int, StatBlock>();
            for (var pieces = 2; pieces <= 9; pieces++)
            {
                var bonusRecord = record.Text($"setBonus{pieces}");
                if (!string.IsNullOrWhiteSpace(bonusRecord) && records.TryGetValue(bonusRecord, out var bonus))
                    bonuses[pieces] = GameStatTranslator.Translate([bonus]);
            }
            if (bonuses.Count > 0) result[record.RecordName] = bonuses;
        }
        return result;
    }

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
        private static readonly byte[] PayloadHeader = "GDOC3"u8.ToArray();
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
                string legacyJson => JsonSerializer.Deserialize<GameDataCatalog>(legacyJson, JsonOptions),
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
