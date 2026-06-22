using Compression.Registry;
using FileSystem.AmigaPfs;

namespace Compression.Tests.AmigaPfs;

/// <summary>
/// Schema-wiring coverage for <see cref="AmigaPfsFormatDescriptor"/>: verifies the
/// published VolumeLabel knob and that Create() writes it as the BCPL disk name,
/// read back through the reader's DiskName, with file round-trip.
/// </summary>
[TestFixture]
public class AmigaPfsSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesVolumeLabelSchema() {
    var desc = new AmigaPfsFormatDescriptor();
    Assert.That(desc, Is.AssignableTo<IFormatOptionsSchema>());
    var keys = ((IFormatOptionsSchema)desc).OptionsSchema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("VolumeLabel"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithNonDefaultVolumeLabel_SetsDiskNameAndRoundTrips() {
    var desc = new AmigaPfsFormatDescriptor();
    var payload = "amiga pfs payload"u8.ToArray();

    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "WorkDisk" },
    };
    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("data.txt", payload)], opts);
    var image = ms.ToArray();

    using var rs = new MemoryStream(image);
    var reader = new AmigaPfsReader(rs);
    Assert.That(reader.DiskName, Is.EqualTo("WorkDisk"), "non-default volume label must surface as DiskName.");

    using var rs2 = new MemoryStream(image);
    var back = desc.ExtractEntryToMemory(rs2, "data.txt", null);
    Assert.That(back, Is.EqualTo(payload));
  }
}
