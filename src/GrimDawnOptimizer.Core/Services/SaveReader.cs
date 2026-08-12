using GrimDawnOptimizer.Core.Contracts;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;

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
                var current = converted.Where(x => x.Source.Kind == ItemSourceKind.Equipped)
                    .Select(x => (Item: x, Slot: raw.Items.FirstOrDefault(r => r.Seed == x.Seed && r.BaseRecord == x.BaseRecord)?.EquippedSlot))
                    .Where(x => x.Slot is not null)
                    .ToDictionary(x => x.Slot!.Value, x => x.Item.Id);

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
                    CurrentLoadout = current,
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

    private static StatBlock BuildBaseline(RawCharacter raw, GameDataCatalog catalog)
    {
        var baseline = new StatBlock();
        baseline.Set("health", Math.Max(1, raw.Health));
        baseline.Set("energy", Math.Max(0, raw.Energy));
        baseline.Set("physique", raw.Physique);
        baseline.Set("cunning", raw.Cunning);
        baseline.Set("spirit", raw.Spirit);
        baseline.Set("offensiveAbility", 300 + raw.Cunning * 0.5);
        baseline.Set("defensiveAbility", 300 + raw.Physique * 0.5);
        baseline.Set("armorAbsorption", 70);

        foreach (var skill in raw.Skills.Where(x => x.Enabled && x.Level > 0))
            if (catalog.Records.TryGetValue(skill.Record, out var record))
                baseline.Add(GameStatTranslator.TranslateAtLevel(record, skill.Level));
        return baseline;
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

        var records = new[]
        {
            raw.BaseRecord, raw.PrefixRecord, raw.SuffixRecord, raw.ModifierRecord,
            raw.TransmuteRecord, raw.AscendantPrefixRecord, raw.AscendantSuffixRecord,
            raw.CompletionBonusRecord
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => catalog.Records.GetValueOrDefault(x)).Where(x => x is not null).Cast<GameRecord>().ToArray();
        var stats = GameStatTranslator.Translate(records);
        var source = new ItemSourceLocation(
            raw.SourceKind, filePath,
            raw.SourceKind == ItemSourceKind.Equipped && raw.EquippedSlot is not null ? (int)raw.EquippedSlot.Value : raw.ContainerIndex,
            raw.X, raw.Y);
        var itemName = ResolveItemName(baseRecord, raw, catalog);
        var effects = BuildTriggeredEffects(records, catalog);
        var unsupported = new List<UnsupportedEffect>();
        foreach (var record in records)
        {
            var skill = record.Text("itemSkillName");
            if (!string.IsNullOrWhiteSpace(skill) && effects.All(x => x.Id != skill))
                unsupported.Add(new UnsupportedEffect(skill, "尚未识别的装备触发技能", ["dps", "effectiveHealth"]));
        }

        if (records.Length < new[]
            {
                raw.BaseRecord, raw.PrefixRecord, raw.SuffixRecord, raw.ModifierRecord,
                raw.TransmuteRecord, raw.AscendantPrefixRecord, raw.AscendantSuffixRecord,
                raw.CompletionBonusRecord
            }.Count(x => !string.IsNullOrWhiteSpace(x)))
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
            SetId = baseRecord.Text("itemSetName"),
            Stats = stats,
            Effects = effects,
            UnsupportedEffects = unsupported,
            Source = source
        };
    }

    private static IReadOnlyList<TriggeredEffect> BuildTriggeredEffects(IReadOnlyList<GameRecord> records, GameDataCatalog catalog)
    {
        var result = new List<TriggeredEffect>();
        foreach (var record in records)
        {
            var skillName = record.Text("itemSkillName");
            if (string.IsNullOrWhiteSpace(skillName) || !catalog.Records.TryGetValue(skillName, out var skill)) continue;
            var controllerName = skill.Text("skillAutoController");
            catalog.Records.TryGetValue(controllerName, out var controller);
            var chance = controller?.NumericValues["triggerEventChance"] ?? skill.NumericValues["skillChance"];
            if (chance > 1) chance /= 100d;
            if (chance <= 0) chance = 1;
            var cooldown = controller?.NumericValues["triggerCooldown"] ?? skill.NumericValues["skillCooldownTime"];
            result.Add(new TriggeredEffect(
                skillName,
                catalog.Tags.GetValueOrDefault(skill.Text("skillDisplayName"), Path.GetFileNameWithoutExtension(skillName)),
                chance,
                cooldown,
                GameStatTranslator.TranslateAtLevel(skill, Math.Max(1, (int)record.NumericValues["itemSkillLevelEq"])),
                controller?.Text("triggerType") ?? "OnAttack"));
        }
        return result;
    }

    private static string ResolveItemName(GameRecord record, RawItem raw, GameDataCatalog catalog)
    {
        var baseName = catalog.Tags.GetValueOrDefault(record.Text("itemNameTag"), Path.GetFileNameWithoutExtension(record.RecordName));
        var prefix = catalog.Records.TryGetValue(raw.PrefixRecord, out var prefixRecord)
            ? catalog.Tags.GetValueOrDefault(prefixRecord.Text("lootRandomizerName"), string.Empty) : string.Empty;
        var suffix = catalog.Records.TryGetValue(raw.SuffixRecord, out var suffixRecord)
            ? catalog.Tags.GetValueOrDefault(suffixRecord.Text("lootRandomizerName"), string.Empty) : string.Empty;
        return string.Join(' ', new[] { prefix, baseName, suffix }.Where(x => !string.IsNullOrWhiteSpace(x)));
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
