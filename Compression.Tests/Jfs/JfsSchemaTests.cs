using System.Text;
using Compression.Registry;
using FileSystem.Jfs;

namespace Compression.Tests.Jfs;

/// <summary>
/// Verifies the JFS creation-option schema: the published VolumeLabel knob is
/// real (it writes the superblock s_label[16] field at offset 152) and files
/// still round-trip through the descriptor's reader.
/// </summary>
[TestFixture]
public class JfsSchemaTests {

  // Primary superblock lives at file offset 0x8000; s_label[16] at +152.
  private const int LabelOffset = 0x8000 + 152;

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var desc = (IFormatOptionsSchema)new JfsFormatDescriptor();
    Assert.That(desc.OptionsSchema.Select(o => o.Key), Does.Contain("VolumeLabel"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithVolumeLabel_WritesSLabelAndRoundTrips() {
    var desc = new JfsFormatDescriptor();
    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYJFSLABEL" },
    };

    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("doc.txt", "jfs-label-payload"u8.ToArray())], opts);
    var image = ms.ToArray();

    var label = Encoding.ASCII.GetString(image, LabelOffset, 16).TrimEnd('\0');
    Assert.That(label, Is.EqualTo("MYJFSLABEL"), "VolumeLabel must land in s_label[16].");

    using var rs = new MemoryStream(image);
    var entries = desc.List(rs, null);
    Assert.That(entries.Any(e => e.Name == "doc.txt"), Is.True, "file must round-trip with the label set.");
  }
}
