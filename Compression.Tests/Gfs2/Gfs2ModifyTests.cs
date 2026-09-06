using Compression.Registry;
using FileSystem.Gfs2;

namespace Compression.Tests.Gfs2;

/// <summary>
/// Existing-image R/W coverage for the standalone GFS2 profile. Mutation is a
/// verified rebuild, but it must preserve the volume properties that are not
/// file payload: the caller's size floor and lock-table name.
/// </summary>
[TestFixture]
public class Gfs2ModifyTests {

  private const long ImageSize = 32L * 1024 * 1024;
  private const string LockTable = "cluster:workbench";

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 31 + seed * 17 + i / 4096);
    return data;
  }

  private static MemoryStream Create(params (string Name, byte[] Data)[] files) {
    var descriptor = new Gfs2FormatDescriptor();
    var image = new MemoryStream();
    descriptor.Create(image,
      files.Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data)).ToArray(),
      new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
          ["size"] = ImageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
          ["LockTable"] = LockTable,
        },
      });
    image.Position = 0;
    return image;
  }

  private static byte[] Read(Stream image, string name) {
    image.Position = 0;
    using var reader = new Gfs2Reader(image);
    var entry = reader.Entries.Single(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    return reader.Extract(entry);
  }

  private static void AssertProfile(Stream image) {
    Assert.That(image.Length, Is.EqualTo(ImageSize), "CRUD must preserve the existing image size when it still fits.");
    image.Position = 0;
    using var reader = new Gfs2Reader(image);
    Assert.Multiple(() => {
      Assert.That(reader.SuperblockValid, Is.True);
      Assert.That(reader.LockTable, Is.EqualTo(LockTable), "CRUD must preserve sb_locktable.");
    });
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AdvertisesRw() {
    var descriptor = new Gfs2FormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("RoundTrip")]
  public void AddReplaceRemove_PreservesPayloadsAndVolumeProfile() {
    var seed = Payload(1, 9_000); // indirect-tree file, not stuffed in the dinode
    var first = Payload(2, 6_000);
    var replacement = Payload(3, 17_000);
    using var image = Create(("SEED.BIN", seed));
    var descriptor = new Gfs2FormatDescriptor();
    var modifier = (IArchiveModifiable)descriptor;

    modifier.Add(image, [ArchiveInputInfo.InMemory("EXTRA.BIN", first)]);
    AssertProfile(image);
    Assert.That(Read(image, "SEED.BIN"), Is.EqualTo(seed));
    Assert.That(Read(image, "EXTRA.BIN"), Is.EqualTo(first));

    modifier.Add(image, [ArchiveInputInfo.InMemory("EXTRA.BIN", replacement)]);
    AssertProfile(image);
    Assert.That(Read(image, "SEED.BIN"), Is.EqualTo(seed));
    Assert.That(Read(image, "EXTRA.BIN"), Is.EqualTo(replacement));

    modifier.Remove(image, ["EXTRA.BIN"]);
    AssertProfile(image);
    image.Position = 0;
    using var reader = new Gfs2Reader(image);
    Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "SEED.BIN" }));
    Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(seed));
  }

  [Test, Category("Regression")]
  public void AddToEmptyVolume_DoesNotTurnDiagnosticEntriesIntoFiles() {
    using var image = Create();
    var descriptor = new Gfs2FormatDescriptor();
    var payload = Payload(4, 5_000);

    ((IArchiveModifiable)descriptor).Add(image,
      [ArchiveInputInfo.InMemory("REAL.BIN", payload)]);

    AssertProfile(image);
    image.Position = 0;
    var names = descriptor.List(image, null).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert.Multiple(() => {
      Assert.That(names, Is.EquivalentTo(new[] { "REAL.BIN" }));
      Assert.That(names, Does.Not.Contain("FULL.gfs2"));
      Assert.That(names, Does.Not.Contain("metadata.ini"));
      Assert.That(names, Does.Not.Contain("superblock.bin"));
    });
    Assert.That(Read(image, "REAL.BIN"), Is.EqualTo(payload));
  }
}
