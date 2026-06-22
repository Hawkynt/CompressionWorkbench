using System.Text;
using Compression.Registry;
using FileSystem.SysV;

namespace Compression.Tests.SysV;

/// <summary>
/// Verifies the System V (s5fs) creation-option schema: the published
/// VolumeLabel knob is real (it writes the superblock s_fname[6] field) and
/// files still round-trip through the descriptor's reader.
/// </summary>
[TestFixture]
public class SysVSchemaTests {

  // Superblock at file offset 512; s_fname[6] at +440.
  private const int FnameOffset = 512 + 440;

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var desc = (IFormatOptionsSchema)new SysVFormatDescriptor();
    Assert.That(desc.OptionsSchema.Select(o => o.Key), Does.Contain("VolumeLabel"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithVolumeLabel_WritesSFnameAndRoundTrips() {
    var desc = new SysVFormatDescriptor();
    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "ROOTFS" },
    };

    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("doc.txt", "sysv-label-payload"u8.ToArray())], opts);
    var image = ms.ToArray();

    var label = Encoding.ASCII.GetString(image, FnameOffset, 6).TrimEnd(' ', '\0');
    Assert.That(label, Is.EqualTo("ROOTFS"), "VolumeLabel must land in s_fname[6].");

    using var rs = new MemoryStream(image);
    var entries = desc.List(rs, null);
    Assert.That(entries.Any(e => e.Name == "doc.txt"), Is.True, "file must round-trip with the label set.");
  }
}
