using System.Text;
using Compression.Registry;
using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// Schema-wiring coverage for <see cref="ApfsFormatDescriptor"/>: verifies the
/// published VolumeLabel knob and that Create() writes it into the APSB
/// apfs_volname field, with file round-trip.
/// </summary>
[TestFixture]
public class ApfsSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesVolumeLabelSchema() {
    var desc = new ApfsFormatDescriptor();
    Assert.That(desc, Is.AssignableTo<IFormatOptionsSchema>());
    var keys = ((IFormatOptionsSchema)desc).OptionsSchema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("VolumeLabel"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithNonDefaultVolumeLabel_StampsApsbAndRoundTrips() {
    var desc = new ApfsFormatDescriptor();
    var payload = "apfs payload"u8.ToArray();

    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MyVolume" },
    };
    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("data.txt", payload)], opts);
    var image = ms.ToArray();

    // APSB volume superblock is block 5 (4 KiB blocks); apfs_volname at +0x2C0.
    const int apsbBlock = 5;
    const int blockSize = 4096;
    var volnameSpan = image.AsSpan(apsbBlock * blockSize + 0x2C0, 256);
    var nul = volnameSpan.IndexOf((byte)0);
    var volname = Encoding.UTF8.GetString(volnameSpan[..(nul < 0 ? 256 : nul)]);
    Assert.That(volname, Is.EqualTo("MyVolume"), "non-default volume label must reach apfs_volname.");

    using var rs = new MemoryStream(image);
    var back = desc.ExtractEntryToMemory(rs, "data.txt", null);
    Assert.That(back, Is.EqualTo(payload));
  }
}
