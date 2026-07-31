using System.Text;
using Compression.Registry;
using FileSystem.Adfs;

namespace Compression.Tests.Adfs;

/// <summary>
/// Schema-wiring coverage for <see cref="AdfsFormatDescriptor"/>: verifies the
/// published VolumeLabel knob and that Create() writes it as the 19-byte disc
/// title in the root directory tail, with file round-trip.
/// </summary>
[TestFixture]
public class AdfsSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesVolumeLabelSchema() {
    var desc = new AdfsFormatDescriptor();
    Assert.That(desc, Is.AssignableTo<IFormatOptionsSchema>());
    var keys = ((IFormatOptionsSchema)desc).OptionsSchema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("VolumeLabel"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithNonDefaultVolumeLabel_StampsDiscTitleAndRoundTrips() {
    var desc = new AdfsFormatDescriptor();
    var payload = "adfs payload"u8.ToArray();

    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYDISC" },
    };
    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("DATA", payload)], opts);
    var image = ms.ToArray();

    // A new-map volume keeps the title in the disc record's disc_name field at
    // sector 0 + 4 + 22, and again in the root directory's tail.
    var titleSpan = image.AsSpan(4 + 22, 10);
    var end = titleSpan.IndexOf((byte)0x00);
    var title = Encoding.ASCII.GetString(titleSpan[..(end < 0 ? 10 : end)]);
    Assert.That(title, Is.EqualTo("MYDISC"), "non-default volume label must reach the disc title.");

    using var rs = new MemoryStream(image);
    var back = desc.ExtractEntryToMemory(rs, "DATA", null);
    Assert.That(back, Is.EqualTo(payload));
  }
}
