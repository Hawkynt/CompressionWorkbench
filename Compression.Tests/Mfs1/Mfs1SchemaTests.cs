using Compression.Registry;
using FileSystem.Mfs1;

namespace Compression.Tests.Mfs1;

/// <summary>
/// Schema-wiring coverage for <see cref="Mfs1FormatDescriptor"/>: verifies the
/// published VolumeLabel knob and that Create() writes it as the DFS-tier disk
/// title, read back through the reader's DiskTitle, with file round-trip.
/// </summary>
[TestFixture]
public class Mfs1SchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesVolumeLabelSchema() {
    var desc = new Mfs1FormatDescriptor();
    Assert.That(desc, Is.AssignableTo<IFormatOptionsSchema>());
    var keys = ((IFormatOptionsSchema)desc).OptionsSchema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("VolumeLabel"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithNonDefaultVolumeLabel_SetsDiskTitleAndRoundTrips() {
    var desc = new Mfs1FormatDescriptor();
    var payload = "mfs1 payload"u8.ToArray();

    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYDISK" },
    };
    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("DATA", payload)], opts);
    var image = ms.ToArray();

    var reader = new Mfs1Reader(image);
    Assert.That(reader.DiskTitle, Is.EqualTo("MYDISK"), "non-default volume label must surface as DiskTitle.");

    using var rs = new MemoryStream(image);
    var back = desc.ExtractEntryToMemory(rs, "DATA", null);
    Assert.That(back, Is.EqualTo(payload));
  }
}
