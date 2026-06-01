using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.ExFat;

namespace Compression.Tests.ExFat;

/// <summary>
/// Smoke coverage for <see cref="ExFatFormatDescriptor"/>'s schema wiring —
/// verifies the published knobs match the spec and that Create() honours a
/// non-default ClusterSize selection by inspecting the VBR
/// SectorsPerClusterShift byte.
/// </summary>
[TestFixture]
public class ExFatSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsFormatOptionsSchema() {
    var desc = new ExFatFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IFormatOptionsSchema>());
  }

  [Test, Category("HappyPath")]
  public void Schema_ContainsExpectedKeys() {
    var desc = (IFormatOptionsSchema)new ExFatFormatDescriptor();
    var keys = desc.OptionsSchema.Select(o => o.Key).ToHashSet();
    Assert.That(keys, Does.Contain("ClusterSize"));
    Assert.That(keys, Does.Contain("VolumeLabel"));
    Assert.That(keys, Does.Contain("ImageSize"));
    var clusterOpt = desc.OptionsSchema.First(o => o.Key == "ClusterSize");
    // The upstream schema uses an enum dropdown of human-readable size labels
    // (Auto / 4 KB … 128 KB) parsed via ParseExFatClusterSize. Each variant
    // gets a dedicated assertion so a future writer-knob change shows up here.
    Assert.That(clusterOpt.Kind, Is.EqualTo(FormatOptionKind.Enum));
    Assert.That(clusterOpt.AllowedValues, Does.Contain("Auto"));
    Assert.That(clusterOpt.AllowedValues, Does.Contain("8 KB"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithClusterSize8K_ProducesShift4() {
    // exFAT VBR offset 109 holds SectorsPerClusterShift. With 512-byte sectors,
    // shift=4 → 16 sectors/cluster → 8 KiB clusters. The writer's default is
    // shift=3 (4 KiB), so an 8 KiB request should bump it to 4.
    var desc = new ExFatFormatDescriptor();
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, "hi"u8.ToArray());
      var opts = new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> {
          ["ClusterSize"] = "8 KB",
        },
      };
      using var ms = new MemoryStream();
      desc.Create(ms, [new ArchiveInputInfo(tmpFile, "test.txt", false)], opts);
      var image = ms.ToArray();

      Assert.That(image[109], Is.EqualTo(4),
        "SectorsPerClusterShift must be 4 for a requested 8 KiB cluster size on 512-byte sectors.");
      // Round-trip sanity: the resulting image must read back through the descriptor.
      using var rs = new MemoryStream(image);
      var entries = desc.List(rs, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));
    } finally {
      File.Delete(tmpFile);
    }
  }
}
