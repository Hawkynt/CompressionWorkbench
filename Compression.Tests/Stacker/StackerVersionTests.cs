using System.Text;
using Compression.Registry;
using Compression.Tests.Cvf;
using Compression.Tests.Support;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

/// <summary>
/// The Stacker <c>Version</c> option writes exactly the version asked for — 3 stamps 300 into the
/// superblock, 4 stamps 410 — which dmsdos detects as that version; edits keep it, and anything else
/// is refused rather than silently written as another version.
/// </summary>
[TestFixture]
public sealed class StackerVersionTests {

  private static (string Name, byte[] Data)[] Files() => [
    ("TEXT.TXT", CvfSamples.Get("text-32k")[..20000]),
    ("TINY.TXT", "tiny"u8.ToArray()),
  ];

  private static byte[] Create(string? version, string? method = null) {
    var options = new Dictionary<string, string>();
    if (version is not null) options["Version"] = version;
    if (method is not null) options["Method"] = method;
    using var ms = new MemoryStream();
    new StackerFormatDescriptor().Create(ms, [.. Files().Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data))],
      new FormatCreateOptions { FormatSpecific = options });
    return ms.ToArray();
  }

  private static int SuperblockVersion(byte[] image) {
    using var reader = new GenuineStackerReader(new MemoryStream(image));
    return reader.Version;
  }

  [Test, Category("HappyPath")]
  public void GivenNoVersion_WhenCreated_ThenTheVolumeIsStacker3()
    => Assert.That(SuperblockVersion(Create(null)), Is.EqualTo(300));

  [Test, Category("HappyPath")]
  public void GivenEachVersion_WhenCreated_ThenTheSuperblockCarriesIt([Values("3", "4")] string version)
    => Assert.That(SuperblockVersion(Create(version)), Is.EqualTo(version == "3" ? 300 : 410));

  [Test, Category("HappyPath")]
  public void GivenAStacker4Volume_WhenEdited_ThenItStaysStacker4() {
    using var image = new MemoryStream();
    image.Write(Create("4"));
    var descriptor = new StackerFormatDescriptor();

    descriptor.Add(image, [ArchiveInputInfo.InMemory("ADDED.TXT", "added"u8.ToArray())]);
    descriptor.Defragment(image);

    Assert.That(SuperblockVersion(image.ToArray()), Is.EqualTo(410));
  }

  [Test, Category("Sad")]
  public void GivenAnUnsupportedVersion_WhenCreated_ThenItIsRefused([Values("2", "5", "410")] string version)
    => Assert.That(() => Create(version), Throws.InstanceOf<ArgumentException>());

  [Test, Category("Sad")]
  public void GivenAnUnsupportedVersion_WhenTheWriterBuilds_ThenItIsRefused()
    => Assert.That(() => new GenuineStackerWriter { Version = 410 }.Build(), Throws.InstanceOf<ArgumentOutOfRangeException>());

  [Test, Category("DriverProof")]
  public void GivenEachVersion_WhenReadByDmsdos_ThenItIsDetectedAsThatVersionAndReadByteExact(
      [Values("3", "4")] string version, [Values("Stored", "SD3", "SD4", "Auto")] string method) {
    DmsdosOracle.Require();
    var path = Path.Combine(Path.GetTempPath(), $"cwb-stac-v{version}-{Guid.NewGuid():N}.cvf");
    try {
      File.WriteAllBytes(path, Create(version, method));

      Assert.That(DmsdosOracle.Detect(path), Does.Contain($"stacker version {version} CVF"));
      foreach (var (name, expected) in Files()) {
        var (exit, bytes) = DmsdosOracle.ReadFile(path, name);
        Assert.That(exit, Is.Zero, $"dcread failed for {name}");
        Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(expected.Length), name);
        Assert.That(bytes[..expected.Length], Is.EqualTo(expected), name);
      }
    } finally {
      File.Delete(path);
    }
  }
}
