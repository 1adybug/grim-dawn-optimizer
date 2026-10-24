using System.Text;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;
using K4os.Compression.LZ4;

namespace GrimDawnOptimizer.Tests;

public sealed class AnimationSpeedResolverTests
{
    [Fact]
    public void ReadsLegacyAndVersionTwoAnmHeadersAndRejectsUnsupportedHeaders()
    {
        Assert.Equal((1, 19u, 30), AnimationSpeedResolver.ParseHeader(Header(19, modern: false)));
        Assert.Equal((2, 21u, 30), AnimationSpeedResolver.ParseHeader(Header(21)));
        var unsupported = Header(19);
        unsupported[3] = 3;
        Assert.Throws<InvalidDataException>(() => AnimationSpeedResolver.ParseHeader(unsupported));
        Assert.Throws<InvalidDataException>(() => AnimationSpeedResolver.ParseHeader(new byte[8]));
    }

    [Fact]
    public void ResolvesCompressedResourcesAndUsesArithmeticMeanRegardlessOfWeights()
    {
        using var fixture = new ArchiveFixture();
        fixture.Write("resources", ("pc/a.anm", Header(19)), ("pc/b.anm", Header(21)));
        var record = Record(new Dictionary<string, IReadOnlyList<string>>
        {
            ["unarmedAttackAnim1"] = ["creatures/pc/a.anm"],
            ["dualRangedAttackAnim1"] = ["creatures/pc/a.anm"],
            ["dualRangedAttackAnim2"] = ["creatures/pc/b.anm"]
        }, new Dictionary<string, double>
        {
            ["dualRangedAttackAnimSpeed2"] = 1.1f,
            ["dualRangedAttackAnimWeight1"] = 0,
            ["dualRangedAttackAnimWeight2"] = 100
        });

        var profile = new AnimationSpeedResolver().Resolve(fixture.Installation, record);

        Assert.Empty(profile.UnresolvedResources);
        var expected = ((19f / 30) + (21f / 30 / 1.1f)) / 2;
        Assert.Equal(expected, profile.AttackGroups["dualRanged"].MeanSeconds);
        Assert.Equal(2, profile.AttackGroups["dualRanged"].Entries.Count);
        Assert.Equal(1.5, profile.UnarmedPanelAttacksPerSecond!.Value, 6);
    }

    [Fact]
    public void ExpansionThreeOverridesBaseAndMissingResourceDoesNotProducePartialGroup()
    {
        using var fixture = new ArchiveFixture();
        fixture.Write("resources", ("pc/a.anm", Header(19)));
        fixture.Write("gdx3/resources", ("pc/a.anm", Header(30)));
        var record = Record(new Dictionary<string, IReadOnlyList<string>>
        {
            ["unarmedAttackAnim1"] = ["creatures/pc/a.anm"],
            ["dualRangedAttackAnim1"] = ["creatures/pc/a.anm"],
            ["dualRangedAttackAnim2"] = ["creatures/pc/missing.anm"],
            ["unarmedAttackAnim2"] = [""]
        });

        var profile = new AnimationSpeedResolver().Resolve(fixture.Installation, record);

        Assert.Equal(1, profile.UnarmedAttackSeconds);
        Assert.False(profile.AttackGroups.ContainsKey("dualRanged"));
        Assert.Single(profile.UnresolvedResources);
        Assert.Contains("missing.anm", profile.UnresolvedResources[0]);
        Assert.Contains("gdx3", profile.AttackGroups["unarmed"].Entries[0].ArchivePath);
    }

    private static GameRecord Record(IReadOnlyDictionary<string, IReadOnlyList<string>> fields,
        IReadOnlyDictionary<string, double>? numbers = null) => new()
    {
        RecordName = "records/creatures/pc/anm_test.dbr",
        TextValues = fields,
        NumericValues = new(numbers)
    };

    private static byte[] Header(uint frames, bool modern = true)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        if (modern) writer.Write(new byte[] { 65, 78, 77, 2 });
        writer.Write(1);
        writer.Write(frames);
        writer.Write(30);
        writer.Write(new byte[256]);
        return stream.ToArray();
    }

    private sealed class ArchiveFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "gd-animation-" + Guid.NewGuid().ToString("N"));
        public GameInstallation Installation => new(directory, "fixture", "test", []);

        public void Write(string folder, params (string Name, byte[] Data)[] files)
        {
            var destination = Path.Combine(directory, folder);
            Directory.CreateDirectory(destination);
            var compressed = files.Select(file =>
            {
                var bytes = new byte[LZ4Codec.MaximumOutputSize(file.Data.Length)];
                var size = LZ4Codec.Encode(file.Data, bytes);
                return bytes[..size];
            }).ToArray();
            var names = Encoding.ASCII.GetBytes(string.Join('\0', files.Select(file => file.Name)) + '\0');
            var partOffset = 28 + compressed.Sum(bytes => bytes.Length);
            using var stream = File.Create(Path.Combine(destination, "creatures.arc"));
            using var writer = new BinaryWriter(stream);
            foreach (var value in new[] { 0x435241, 3, files.Length, files.Length, files.Length * 12, names.Length, partOffset })
                writer.Write(value);
            foreach (var bytes in compressed) writer.Write(bytes);
            var offset = 28;
            for (var index = 0; index < files.Length; index++)
            {
                writer.Write(offset);
                writer.Write(compressed[index].Length);
                writer.Write(files[index].Data.Length);
                offset += compressed[index].Length;
            }
            writer.Write(names);
            var nameOffset = 0;
            for (var index = 0; index < files.Length; index++)
            {
                writer.Write(3);
                writer.Write(0);
                writer.Write(compressed[index].Length);
                writer.Write(files[index].Data.Length);
                writer.Write(0);
                writer.Write(0L);
                writer.Write(1);
                writer.Write(index);
                writer.Write(files[index].Name.Length + 1);
                writer.Write(nameOffset);
                nameOffset += files[index].Name.Length + 1;
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
