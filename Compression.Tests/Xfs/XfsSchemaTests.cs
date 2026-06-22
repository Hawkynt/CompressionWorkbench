using System.Text;
using Compression.Registry;
using FileSystem.Xfs;

namespace Compression.Tests.Xfs;

/// <summary>
/// Verifies the XFS creation-option schema: the published VolumeLabel knob is
/// real (it writes the superblock sb_fname[12] field) and files still
/// round-trip through the descriptor's reader.
/// </summary>
[TestFixture]
public class XfsSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var desc = (IFormatOptionsSchema)new XfsFormatDescriptor();
    Assert.That(desc.OptionsSchema.Select(o => o.Key), Does.Contain("VolumeLabel"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithVolumeLabel_WritesSbFnameAndRoundTrips() {
    var desc = new XfsFormatDescriptor();
    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYXFSVOL" },
    };

    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("doc.txt", "xfs-label-payload"u8.ToArray())], opts);
    var image = ms.ToArray();

    // sb_fname[12] lives at superblock offset 108 in AG 0 (image offset 108).
    var label = Encoding.ASCII.GetString(image, 108, 12).TrimEnd('\0');
    Assert.That(label, Is.EqualTo("MYXFSVOL"), "VolumeLabel must land in sb_fname[12].");

    using var rs = new MemoryStream(image);
    var entries = desc.List(rs, null);
    Assert.That(entries.Any(e => e.Name == "doc.txt"), Is.True, "file must round-trip with the label set.");
  }

  [Test, Category("HappyPath")]
  public void Create_DefaultLeavesSbFnameZero() {
    var desc = new XfsFormatDescriptor();
    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("a.txt", "x"u8.ToArray())], new FormatCreateOptions());
    var image = ms.ToArray();
    for (var i = 108; i < 120; i++)
      Assert.That(image[i], Is.EqualTo(0), "default (empty) label must leave sb_fname zero.");
  }
}
