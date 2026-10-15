using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Penghou.Zhinu.Declarative;

namespace Penghou.Zhinu.Tests;

public sealed class ActivityGroupManifestGoldenTests
{
    private const string DigestDomain = "penghou.zhinu.activity-groups.manifest.v1\n";

    [Fact]
    public void EmptyManifest_MatchesHandAuthoredCanonicalJsonAndDigest()
    {
        const string expectedJson = """{"contractVersion":1,"definitions":[]}""";
        const string expectedDigest = "dd513a06f1fe4b2ab2757e3ef63aff378647cecc896a3054eeb4db80f467f5af";

        var manifest = ActivityGroupManifest.Create(Array.Empty<ActivityGroupDefinition>());

        manifest.CanonicalJson.Should().Be(expectedJson);
        manifest.ManifestDigest.Should().Be(expectedDigest);
        manifest.ManifestDigest.Should().Be(IndependentDigest(expectedJson));
    }

    [Fact]
    public void UnicodeDisplay_MatchesHandAuthoredEscapingAndDigest()
    {
        const string expectedJson = """{"contractVersion":1,"definitions":[{"groupKey":"unicode","parentGroupKey":null,"kind":"Logical","displayName":"Caf\u00E9 cat \uD83D\uDC08","childOrdinal":0,"provenance":null}]}""";
        const string expectedDigest = "d9fe93d128d967f7ccadfb29df573e6f150e17a3ed8ee06182b03959ee4f81e0";

        var manifest = ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition("unicode", ActivityGroupKind.Logical, "Café cat 🐈")
        });

        manifest.CanonicalJson.Should().Be(expectedJson);
        manifest.ManifestDigest.Should().Be(expectedDigest);
        manifest.ManifestDigest.Should().Be(IndependentDigest(expectedJson));
    }

    [Fact]
    public void MacroWithOptionalMetadataNull_MatchesHandAuthoredCanonicalJsonAndDigest()
    {
        const string expectedJson = """{"contractVersion":1,"definitions":[{"groupKey":"macro","parentGroupKey":null,"kind":"Macro","displayName":"Macro","childOrdinal":2,"provenance":{"macroName":"unknown","macroDefinitionFingerprint":"sha256:abc","invocationKey":"call/1","macroVersion":null,"sourceReference":null}}]}""";
        const string expectedDigest = "068057fb92ae1975eaefa59e069432949025c95c6e2169a236d9382dde893afc";

        var manifest = ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition(
                "macro",
                ActivityGroupKind.Macro,
                "Macro",
                childOrdinal: 2,
                provenance: new ActivityGroupMacroProvenance(
                    "unknown",
                    "sha256:abc",
                    "call/1"))
        });

        manifest.CanonicalJson.Should().Be(expectedJson);
        manifest.ManifestDigest.Should().Be(expectedDigest);
        manifest.ManifestDigest.Should().Be(IndependentDigest(expectedJson));
    }

    [Fact]
    public void QuoteAndBackslashText_MatchesHandAuthoredEscapingAndDigest()
    {
        const string expectedJson = """{"contractVersion":1,"definitions":[{"groupKey":"quoted","parentGroupKey":null,"kind":"Logical","displayName":"a\"b\\c","childOrdinal":0,"provenance":null}]}""";
        const string expectedDigest = "6e3c8a29dc484895a79fc1a061038715af6f0821bc0c4159d9433541f351580b";

        var manifest = ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition("quoted", ActivityGroupKind.Logical, "a\"b\\c")
        });

        manifest.CanonicalJson.Should().Be(expectedJson);
        manifest.ManifestDigest.Should().Be(expectedDigest);
        manifest.ManifestDigest.Should().Be(IndependentDigest(expectedJson));
    }

    [Fact]
    public void CanonicalJson_SeparatesAdjacentFieldBoundaries()
    {
        var first = ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition("ab", ActivityGroupKind.Logical, "c")
        });
        var second = ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition("a", ActivityGroupKind.Logical, "bc")
        });

        first.CanonicalJson.Should().NotBe(second.CanonicalJson);
        first.ManifestDigest.Should().NotBe(second.ManifestDigest);
    }

    [Fact]
    public void CanonicalJson_IsInvariantAcrossCurrentCultures()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var invariant = CreateCultureFixture();

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = CreateCultureFixture();

            turkish.CanonicalJson.Should().Be(invariant.CanonicalJson);
            turkish.ManifestDigest.Should().Be(invariant.ManifestDigest);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void CanonicalByteLimit_AcceptsExactSizeAndRejectsOneByteLess()
    {
        const string expectedEmptyJson = """{"contractVersion":1,"definitions":[]}""";
        var exactSize = Encoding.UTF8.GetByteCount(expectedEmptyJson);

        var manifest = ActivityGroupManifest.Create(
            Array.Empty<ActivityGroupDefinition>(),
            new ActivityGroupManifestLimits(maximumCanonicalBytes: exactSize));
        var oneByteShort = () => ActivityGroupManifest.Create(
            Array.Empty<ActivityGroupDefinition>(),
            new ActivityGroupManifestLimits(maximumCanonicalBytes: exactSize - 1));

        manifest.CanonicalJson.Should().Be(expectedEmptyJson);
        Encoding.UTF8.GetByteCount(manifest.CanonicalJson).Should().Be(exactSize);
        oneByteShort.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TextLimits_AreMeasuredInUtf8Bytes()
    {
        var maximumKey = new string('é', 256);
        var manifest = ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition(maximumKey, ActivityGroupKind.Logical, "Name")
        });
        var tooLongKey = () => new ActivityGroupDefinition(
            new string('é', 257), ActivityGroupKind.Logical, "Name");

        Encoding.UTF8.GetByteCount(maximumKey).Should().Be(512);
        manifest.Definitions.Should().ContainSingle().Which.GroupKey.Should().Be(maximumKey);
        tooLongKey.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsNullDefinitionsUnknownVersionAndNegativeOrdinal()
    {
        var nullDefinition = () => ActivityGroupManifest.Create(
            new ActivityGroupDefinition[] { null! });
        nullDefinition.Should().Throw<ArgumentException>();

        var unknownVersion = () => ActivityGroupManifest.Create(
            Array.Empty<ActivityGroupDefinition>(),
            contractVersion: ActivityGroupManifest.CurrentContractVersion + 1);
        unknownVersion.Should().Throw<ArgumentOutOfRangeException>();

        var negativeOrdinal = () => new ActivityGroupDefinition(
            "group", ActivityGroupKind.Logical, "Group", childOrdinal: -1);
        negativeOrdinal.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_StopsEnumeratingAtFirstItemOverMaximumGroupCount()
    {
        const int maximumGroups = 2;
        var enumerated = 0;

        IEnumerable<ActivityGroupDefinition> BoundedDefinitions()
        {
            while (true)
            {
                enumerated++;
                if (enumerated > maximumGroups + 1)
                    throw new InvalidOperationException("The source was enumerated beyond the bounded probe.");
                yield return new ActivityGroupDefinition(
                    $"g{enumerated}", ActivityGroupKind.Logical, $"Group {enumerated}");
            }
        }

        var action = () => ActivityGroupManifest.Create(
            BoundedDefinitions(),
            new ActivityGroupManifestLimits(maximumGroups: maximumGroups));

        action.Should().Throw<ArgumentException>();
        enumerated.Should().Be(maximumGroups + 1);
    }

    [Fact]
    public void DeclarativeCanonicalFingerprint_RemainsAtItsLegacyGoldenValue()
    {
        const string expectedCanonical = """{"name":"legacy","version":"1","steps":[{"id":"step","activity":{"name":"echo","version":"1"},"dependsOn":[],"inputContract":"System.String, System.Private.CoreLib","outputContract":"System.String, System.Private.CoreLib"}]}""";
        const string expectedFingerprint = "93ac5105640f837a01d18527aa3ed42e3b2fe369143d2ac15c940ecfcc79a1ec";
        var catalogue = new ActivityCatalogue();
        catalogue.Register(
            new ActivityReference("echo", "1"),
            new EchoActivity());
        var definition = new DeclarativeWorkflowDefinition
        {
            Name = "legacy",
            Version = "1",
            Steps = new[]
            {
                new DeclarativeWorkflowStep
                {
                    Id = "step",
                    Activity = new ActivityReference("echo", "1")
                }
            }
        };

        var compiled = WorkflowCompiler.Compile(definition, catalogue).Compiled!;

        WorkflowCanonicalizer.Canonicalize(compiled).Should().Be(expectedCanonical);
        compiled.Fingerprint.Should().Be(expectedFingerprint);
        WorkflowFingerprint.Compute(compiled).Should().Be(expectedFingerprint);
    }

    private static ActivityGroupManifest CreateCultureFixture() =>
        ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition("I", ActivityGroupKind.Logical, "İ", childOrdinal: 12),
            new ActivityGroupDefinition("i", ActivityGroupKind.Logical, "i", childOrdinal: 3)
        });

    private static string IndependentDigest(string canonicalJson)
    {
        var input = Encoding.UTF8.GetBytes(DigestDomain + canonicalJson);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private sealed class EchoActivity : IActivity<string, string>
    {
        public Task<string> ExecuteAsync(string input, CancellationToken cancellationToken) => Task.FromResult(input);
    }
}
