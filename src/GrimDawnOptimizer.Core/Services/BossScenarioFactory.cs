using System.Globalization;
using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

public static class BossScenarioFactory
{
    private static readonly IReadOnlyDictionary<string, string> ResistanceFields =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["physical"] = "defensivePhysical",
            ["pierce"] = "defensivePierce",
            ["fire"] = "defensiveFire",
            ["cold"] = "defensiveCold",
            ["lightning"] = "defensiveLightning",
            ["acid"] = "defensivePoison",
            ["vitality"] = "defensiveLife",
            ["aether"] = "defensiveAether",
            ["chaos"] = "defensiveChaos"
        };

    public static CombatScenario ApplyAverageBoss(
        CharacterSnapshot character,
        GameDataCatalog catalog,
        CombatScenario scenario)
    {
        var level = Math.Clamp(character.Level, 1, 250);
        var samples = catalog.Records.Values
            .Where(IsBossMonster)
            .Where(x => IsAvailableAtLevel(x, level))
            .GroupBy(BossSignature, StringComparer.OrdinalIgnoreCase)
            .Select(x => BuildSample(x.First(), level, catalog))
            .Where(x => x.Health > 0)
            .ToArray();

        if (samples.Length == 0)
            samples = [BuildFallback(level)];

        scenario.EnemyLevel = level;
        scenario.EnemyHealth = samples.Average(x => x.Health);
        scenario.EnemyArmor = samples.Average(x => x.Armor);
        var combatAbilityFloor = 2400 + level * 5;
        scenario.EnemyOffensiveAbility = Math.Max(combatAbilityFloor, samples.Average(x => x.OffensiveAbility));
        scenario.EnemyDefensiveAbility = Math.Max(combatAbilityFloor, samples.Average(x => x.DefensiveAbility));
        scenario.EnemyLifeLeechResistance = samples.Average(x => x.LifeLeechResistance);
        scenario.BossSampleCount = samples.Length == 1 && samples[0].IsFallback ? 0 : samples.Length;
        scenario.Name = scenario.BossSampleCount > 0
            ? $"Lv.{level} BOSS 平均值（{scenario.BossSampleCount} 类）"
            : $"Lv.{level} BOSS 估算基准";
        foreach (var type in StatKeys.DamageTypes)
            scenario.EnemyResistances[type] = samples.Average(x => x.Resistances.GetValueOrDefault(type));
        return scenario;
    }

    private static bool IsBossMonster(GameRecord record)
    {
        if (!record.RecordName.StartsWith("records/creatures/enemies/", StringComparison.OrdinalIgnoreCase)) return false;
        var type = string.IsNullOrWhiteSpace(record.Text("Class")) ? record.RecordType : record.Text("Class");
        if (!type.Equals("Monster", StringComparison.OrdinalIgnoreCase)) return false;
        var classification = record.Text("monsterClassification");
        return classification is "Boss" or "Quest" or "Nemesis" ||
               record.RecordName.Contains("/boss&quest/", StringComparison.OrdinalIgnoreCase) ||
               record.RecordName.Contains("/boss/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAvailableAtLevel(GameRecord record, int level)
    {
        var minimum = record.NumericValues["minLevel"];
        var maximum = record.NumericValues["maxLevel"];
        return (minimum <= 0 || level >= minimum) && (maximum <= 0 || level <= maximum);
    }

    private static string BossSignature(GameRecord record)
    {
        var skills = Enumerable.Range(1, 32)
            .Select(x => record.Text($"skillName{x}"))
            .Where(x => !string.IsNullOrWhiteSpace(x));
        return $"{record.Text("characterAttributeEquations")}|{string.Join('|', skills)}|" +
               string.Join('|', ResistanceFields.Values.Select(x => record.NumericValues[x].ToString("0.###", CultureInfo.InvariantCulture)));
    }

    private static BossSample BuildSample(GameRecord monster, int level, GameDataCatalog catalog)
    {
        catalog.Records.TryGetValue(monster.Text("characterAttributeEquations"), out var equations);
        var health = ResolveEquationOrValue(equations, monster, "characterLife", level);
        var oa = ResolveEquationOrValue(equations, monster, "characterOffensiveAbility", level);
        var da = ResolveEquationOrValue(equations, monster, "characterDefensiveAbility", level);
        var armor = ValueAtLevel(monster, "defensiveProtection", level);
        var healthModifier = ValueAtLevel(monster, "characterLifeModifier", level);
        var oaModifier = ValueAtLevel(monster, "characterOffensiveAbilityModifier", level);
        var daModifier = ValueAtLevel(monster, "characterDefensiveAbilityModifier", level);
        var lifeLeechResistance = ValueAtLevel(monster, "defensiveSlowLifeLeach", level);
        var resistances = ResistanceFields.ToDictionary(
            x => x.Key,
            x => ValueAtLevel(monster, x.Value, level),
            StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index <= 32; index++)
        {
            var skillRecord = monster.Text($"skillName{index}");
            if (string.IsNullOrWhiteSpace(skillRecord) || !catalog.Records.TryGetValue(skillRecord, out var skill)) continue;
            var skillLevel = ResolveLevel(monster.Text($"skillLevel{index}"), level);
            armor += ValueAtLevel(skill, "defensiveProtection", skillLevel);
            healthModifier += ValueAtLevel(skill, "characterLifeModifier", skillLevel);
            oaModifier += ValueAtLevel(skill, "characterOffensiveAbilityModifier", skillLevel);
            daModifier += ValueAtLevel(skill, "characterDefensiveAbilityModifier", skillLevel);
            lifeLeechResistance += ValueAtLevel(skill, "defensiveSlowLifeLeach", skillLevel);
            foreach (var (type, field) in ResistanceFields)
                resistances[type] += ValueAtLevel(skill, field, skillLevel);
            var elemental = ValueAtLevel(skill, "defensiveElementalResistance", skillLevel);
            resistances["fire"] += elemental;
            resistances["cold"] += elemental;
            resistances["lightning"] += elemental;
        }

        health = Math.Max(1, health * Math.Max(0, 1 + healthModifier / 100d));
        oa = Math.Max(1, oa * Math.Max(0, 1 + oaModifier / 100d));
        da = Math.Max(1, da * Math.Max(0, 1 + daModifier / 100d));
        foreach (var type in StatKeys.DamageTypes)
            resistances[type] = Math.Clamp(resistances[type], -100, 100);

        return new BossSample(health, Math.Max(0, armor), oa, da,
            Math.Clamp(lifeLeechResistance, 0, 100), resistances, false);
    }

    private static double ResolveEquationOrValue(GameRecord? equations, GameRecord monster, string field, int level)
    {
        var formula = equations?.Text(field);
        if (!string.IsNullOrWhiteSpace(formula) && FormulaEvaluator.TryEvaluate(formula, level, out var result))
            return result;
        return ValueAtLevel(monster, field, level);
    }

    private static int ResolveLevel(string formula, int characterLevel)
    {
        if (string.IsNullOrWhiteSpace(formula)) return 1;
        return FormulaEvaluator.TryEvaluate(formula, characterLevel, out var result)
            ? Math.Max(1, (int)Math.Floor(result))
            : 1;
    }

    private static double ValueAtLevel(GameRecord record, string field, int level)
    {
        if (record.NumericSeries.TryGetValue(field, out var values) && values.Count > 0)
            return values[Math.Clamp(level - 1, 0, values.Count - 1)];
        return record.NumericValues[field];
    }

    private static BossSample BuildFallback(int level)
    {
        var health = Math.Pow(level * 30d, 1.53);
        var resistances = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["physical"] = 18, ["pierce"] = 18, ["fire"] = 25, ["cold"] = 25,
            ["lightning"] = 25, ["acid"] = 25, ["vitality"] = 25,
            ["aether"] = 25, ["chaos"] = 25
        };
        return new BossSample(health, Math.Max(50, level * 22), 2400 + level * 5,
            2400 + level * 5, 60, resistances, true);
    }

    private sealed record BossSample(
        double Health,
        double Armor,
        double OffensiveAbility,
        double DefensiveAbility,
        double LifeLeechResistance,
        Dictionary<string, double> Resistances,
        bool IsFallback);
}

internal sealed class FormulaEvaluator
{
    private readonly string _formula;
    private readonly double _characterLevel;
    private int _position;

    private FormulaEvaluator(string formula, double characterLevel)
    {
        _formula = formula;
        _characterLevel = characterLevel;
    }

    public static bool TryEvaluate(string formula, double characterLevel, out double value)
    {
        try
        {
            var parser = new FormulaEvaluator(formula, characterLevel);
            value = parser.ParseExpression();
            parser.SkipWhitespace();
            return parser._position == parser._formula.Length && double.IsFinite(value);
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private double ParseExpression()
    {
        var value = ParseTerm();
        while (true)
        {
            if (Take('+')) value += ParseTerm();
            else if (Take('-')) value -= ParseTerm();
            else return value;
        }
    }

    private double ParseTerm()
    {
        var value = ParsePower();
        while (true)
        {
            if (Take('*')) value *= ParsePower();
            else if (Take('/')) value /= ParsePower();
            else return value;
        }
    }

    private double ParsePower()
    {
        var value = ParseUnary();
        return Take('^') ? Math.Pow(value, ParsePower()) : value;
    }

    private double ParseUnary()
    {
        if (Take('+')) return ParseUnary();
        if (Take('-')) return -ParseUnary();
        return ParsePrimary();
    }

    private double ParsePrimary()
    {
        if (Take('('))
        {
            var value = ParseExpression();
            if (!Take(')')) throw new FormatException();
            return value;
        }

        SkipWhitespace();
        if (_position < _formula.Length && (char.IsLetter(_formula[_position]) || _formula[_position] == '_'))
        {
            var start = _position++;
            while (_position < _formula.Length && (char.IsLetterOrDigit(_formula[_position]) || _formula[_position] == '_')) _position++;
            var identifier = _formula[start.._position];
            return identifier.Equals("charLevel", StringComparison.OrdinalIgnoreCase) ? _characterLevel : 0;
        }

        var numberStart = _position;
        while (_position < _formula.Length && (char.IsDigit(_formula[_position]) || _formula[_position] is '.' or 'e' or 'E')) _position++;
        if (numberStart == _position || !double.TryParse(_formula[numberStart.._position], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw new FormatException();
        return number;
    }

    private bool Take(char expected)
    {
        SkipWhitespace();
        if (_position >= _formula.Length || _formula[_position] != expected) return false;
        _position++;
        return true;
    }

    private void SkipWhitespace()
    {
        while (_position < _formula.Length && char.IsWhiteSpace(_formula[_position])) _position++;
    }
}
