using FluentAssertions;

namespace Penghou.Zhinu.Tests;

public sealed class ActivityGroupSelectionEpochTests
{
    [Theory]
    [InlineData(ActivityGroupFanOutIdentityKind.Keyed)]
    [InlineData(ActivityGroupFanOutIdentityKind.Positional)]
    public void ExplicitDurableScope_DistinguishesIdenticalItemsAcrossEpochsWithoutNewEncoding(ActivityGroupFanOutIdentityKind kind)
    {
        var run = Guid.Parse("34ed94d4-879a-4aad-8bd4-b37ec4093d81");
        var manifest = ActivityGroupManifest.Create([new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work")]);
        var token = kind == ActivityGroupFanOutIdentityKind.Keyed ? "B" : "1";
        ActivityGroupFanOutPlan Plan(int epoch) => ActivityGroupFanOutPlan.Create(run, manifest, $"items.{epoch}",
            new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), $"select.{epoch}"), kind,
            "input", "output", null, null, "none", [$"select.{epoch}"], [(token, new string('a', 64))]);
        var old = Plan(1);
        var next = Plan(2);
        next.Items[0].InputHash.Should().Be(old.Items[0].InputHash);
        next.Items[0].ItemKey.Should().Be(old.Items[0].ItemKey);
        next.Items[0].StepKey.Should().NotBe(old.Items[0].StepKey);
        next.Items[0].Group.Occurrence.GroupOccurrenceId.Should().NotBe(old.Items[0].Group.Occurrence.GroupOccurrenceId);
        next.Items[0].Group.Occurrence.ExecutionCoordinate.Should().NotBe(old.Items[0].Group.Occurrence.ExecutionCoordinate);
        Plan(1).CanonicalJson.Should().Be(old.CanonicalJson);
        Plan(1).Items[0].Group.Occurrence.GroupOccurrenceId.Should().Be(old.Items[0].Group.Occurrence.GroupOccurrenceId);
        ActivityGroupOccurrence.ReadCoordinate(next.Items[0].Group.Occurrence.ExecutionCoordinate)
            .Should().ContainSingle().Which.Scope.Should().Be("items.2");
    }
}
