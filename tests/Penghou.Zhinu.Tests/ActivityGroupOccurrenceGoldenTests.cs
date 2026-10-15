using FluentAssertions;

namespace Penghou.Zhinu.Tests;

public sealed class ActivityGroupOccurrenceGoldenTests
{
    [Fact]
    public void OccurrenceV1_MatchesIndependentLengthDelimitedSha256Vectors()
    {
        // Independently encoded with PowerShell MemoryStream/BitConverter/SHA256,
        // including an explicit FFFFFFFF null tag and big-endian UTF-8 lengths.
        var manifest = ActivityGroupManifest.Create([
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
            new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work", "root")]);
        var run = Guid.Parse("ac09fa13-1206-402f-8657-fb318bf3926e");
        var root = ActivityGroupOccurrence.Create(run, manifest, "root", null, "[]");
        var child = ActivityGroupOccurrence.Create(run, manifest, "work", root,
            "[{\"loop\":\"review\",\"iteration\":2}]");

        root.GroupOccurrenceId.Should().Be("fa9e60f647bdfc20142cec8bd019d4dbf4ad6601f3cc3ca02335e3504aca2d18");
        child.GroupOccurrenceId.Should().Be("f421f8e8cbf937c65c7fbf15832014662cc0da9766774b6450d48bf4071b0191");
    }
}
