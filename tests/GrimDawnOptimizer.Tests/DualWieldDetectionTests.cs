using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class DualWieldDetectionTests
{
    [Fact]
    public void RangedExpertiseEnablesDualWieldEvenWhenRecordPathDoesNotSayDualWield()
    {
        const string recordName = "records/skills/playerclass07/passive01.dbr";
        var catalog = Catalog(recordName, "Skill_Passive", "tagGDX1Class07SkillName11A");
        var skill = new RawSkill(recordName, 1, true, false, 0, 0, 0, false, false, "", "");

        var result = SaveReader.ResolveCanDualWield(
            [skill], [], new Dictionary<EquipmentSlot, string>(), catalog);

        Assert.True(result);
    }

    [Fact]
    public void DedicatedDualWieldSkillClassEnablesDualWield()
    {
        const string recordName = "records/skills/itemskills/granted/custom_passive.dbr";
        var catalog = Catalog(recordName, "Skill_PassiveDualWieldWeapon", "tagCustomSkill");

        Assert.True(SaveReader.IsDualWieldSkill(recordName, catalog));
    }

    [Fact]
    public void LocalizedSkillDescriptionCanDeclareDualWield()
    {
        const string recordName = "records/skills/playerclass/custom.dbr";
        const string descriptionTag = "tagCustomSkillDescription";
        var record = new GameRecord
        {
            RecordName = recordName,
            RecordType = "Skill_Passive",
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Class"] = ["Skill_Passive"],
                ["skillDescription"] = [descriptionTag]
            }
        };
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord> { [recordName] = record },
            Tags = new Dictionary<string, string> { [descriptionTag] = "训练后可以双持远程武器。" }
        };

        Assert.True(SaveReader.IsDualWieldSkill(recordName, catalog));
    }

    private static GameDataCatalog Catalog(string recordName, string @class, string displayTag)
    {
        var record = new GameRecord
        {
            RecordName = recordName,
            RecordType = @class,
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Class"] = [@class],
                ["skillDisplayName"] = [displayTag]
            }
        };
        return new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord> { [recordName] = record }
        };
    }
}
