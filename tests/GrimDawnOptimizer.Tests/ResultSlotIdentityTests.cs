using GrimDawnOptimizer.App.ViewModels;
using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Tests;

public sealed class ResultSlotIdentityTests
{
    private const string ComponentRecord = "records/items/materia/component.dbr";
    private const string CompletionRecord = "records/items/materia/completion.dbr";
    private const string AugmentRecord = "records/items/enchants/augment.dbr";

    [Fact]
    public void DifferentInstancesWithTheSameNameAreShownAsReplacement()
    {
        var equipped = Item("equipped-instance");
        var candidate = Item("backpack-instance", sourceKind: ItemSourceKind.Backpack);

        var view = Result(equipped, new LoadoutVariant(candidate, null, null));

        var row = Assert.Single(view.SlotRows, x => x.Changed);
        Assert.Equal(row.CurrentItem, row.CandidateItem);
        Assert.Equal("头盔", row.Slot);
        Assert.Equal("替换 1 个槽位", view.ChangeSummary);
    }

    [Theory]
    [InlineData(AttachmentKind.Component)]
    [InlineData(AttachmentKind.Augment)]
    public void ChangingAnAttachmentOnTheSameItemIsShownAsReplacement(AttachmentKind kind)
    {
        var equipped = Item("equipped-instance", ComponentRecord, CompletionRecord, AugmentRecord);
        var component = Attachment(AttachmentKind.Component, ComponentRecord, CompletionRecord);
        var augment = Attachment(AttachmentKind.Augment, AugmentRecord);
        var replacement = Attachment(kind, "records/items/replacement.dbr",
            kind == AttachmentKind.Component ? CompletionRecord : string.Empty);
        var variant = kind == AttachmentKind.Component
            ? new LoadoutVariant(equipped, replacement, augment)
            : new LoadoutVariant(equipped, component, replacement);

        var view = Result(equipped, variant);

        var row = Assert.Single(view.SlotRows, x => x.Changed);
        Assert.Equal(row.CurrentItem, row.CandidateItem);
        Assert.Equal("头盔", row.Slot);
        Assert.Equal("替换 1 个槽位", view.ChangeSummary);
    }

    [Fact]
    public void ChangingOnlyTheComponentCompletionBonusIsShownAsReplacement()
    {
        var equipped = Item("equipped-instance", ComponentRecord, CompletionRecord);
        var component = Attachment(AttachmentKind.Component, ComponentRecord,
            "records/items/materia/other_completion.dbr");

        var view = Result(equipped, new LoadoutVariant(equipped, component, null));

        Assert.Equal("头盔", Assert.Single(view.SlotRows, x => x.Changed).Slot);
        Assert.Equal("替换 1 个槽位", view.ChangeSummary);
    }

    [Theory]
    [InlineData(AttachmentKind.Component, false)]
    [InlineData(AttachmentKind.Component, true)]
    [InlineData(AttachmentKind.Augment, false)]
    [InlineData(AttachmentKind.Augment, true)]
    public void AddingOrRemovingAnAttachmentIsShownAsReplacement(AttachmentKind kind, bool remove)
    {
        var equipped = Item("equipped-instance",
            remove && kind == AttachmentKind.Component ? ComponentRecord : string.Empty,
            remove && kind == AttachmentKind.Component ? CompletionRecord : string.Empty,
            remove && kind == AttachmentKind.Augment ? AugmentRecord : string.Empty);
        var component = !remove && kind == AttachmentKind.Component
            ? Attachment(kind, ComponentRecord, CompletionRecord) : null;
        var augment = !remove && kind == AttachmentKind.Augment
            ? Attachment(kind, AugmentRecord) : null;

        var view = Result(equipped, new LoadoutVariant(equipped, component, augment));

        Assert.Equal("头盔", Assert.Single(view.SlotRows, x => x.Changed).Slot);
        Assert.Equal("替换 1 个槽位", view.ChangeSummary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingRecordAndCompletionIdentityKeepsTheCurrentLoadout(bool legacyIdentifier)
    {
        var equipped = Item("equipped-instance", ComponentRecord, CompletionRecord, AugmentRecord);
        var component = Attachment(AttachmentKind.Component, ComponentRecord.ToUpperInvariant(),
            CompletionRecord.ToUpperInvariant(), legacyIdentifier);
        var augment = Attachment(AttachmentKind.Augment, AugmentRecord.ToUpperInvariant(),
            legacyIdentifier: legacyIdentifier);

        var view = Result(equipped, new LoadoutVariant(equipped, component, augment));

        Assert.All(view.SlotRows, row => Assert.False(row.Changed));
        Assert.Equal("保持当前配装", view.ChangeSummary);
        var head = Assert.Single(view.SlotRows, x => x.Slot == "头盔");
        Assert.Contains("已镶嵌", head.Component);
        Assert.Contains("已镶嵌", head.Augment);
    }

    private static ItemInstance Item(
        string id, string component = "", string completion = "", string augment = "",
        ItemSourceKind sourceKind = ItemSourceKind.Equipped) => new()
    {
        Id = id,
        Name = "同名头盔",
        Slot = EquipmentSlot.Head,
        BaseRecord = "records/items/gearhead/helmet.dbr",
        ComponentRecord = component,
        CompletionBonusRecord = completion,
        AugmentRecord = augment,
        Source = new ItemSourceLocation(sourceKind, "player.gdc")
    };

    private static AttachmentDefinition Attachment(
        AttachmentKind kind, string record, string completion = "", bool legacyIdentifier = false) => new()
    {
        Id = legacyIdentifier ? record : $"catalog:{kind}:{record}:{completion}",
        RecordId = legacyIdentifier ? string.Empty : record,
        CompletionBonusRecord = completion,
        Name = "同名附件",
        Kind = kind
    };

    private static ResultViewModel Result(ItemInstance equipped, LoadoutVariant candidate)
    {
        var character = new CharacterSnapshot
        {
            Name = "测试角色",
            SaveFilePath = "player.gdc",
            Items = equipped.Id == candidate.Item.Id ? [equipped] : [equipped, candidate.Item],
            CurrentLoadout = new Dictionary<EquipmentSlot, string> { [EquipmentSlot.Head] = equipped.Id }
        };
        var result = new LoadoutResult
        {
            Rank = 1,
            Loadout = new CalculatedLoadout
            {
                Slots = new Dictionary<EquipmentSlot, LoadoutVariant> { [EquipmentSlot.Head] = candidate },
                Stats = new StatBlock()
            },
            Simulation = new SimulationReport(),
            Metrics = new Dictionary<string, double>(),
            TierScores = [],
            Confidence = ConfidenceLevel.High
        };
        return new ResultViewModel(result, character, []);
    }
}
