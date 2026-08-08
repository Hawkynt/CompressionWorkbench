using Compression.Registry;
using FileSystem.OpenVms;

namespace Compression.Tests.OpenVms;

[TestFixture]
public class OpenVmsSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var d = new OpenVmsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_VolumeLabel_RoundTripsThroughHomeBlock() {
    var d = new OpenVmsFormatDescriptor();
    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(
      ms, [ArchiveInputInfo.InMemory("HELLO.TXT", "payload"u8.ToArray())],
      new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYVOL" },
      });

    var image = ms.ToArray();
    // Home block at LBN 1 (offset 512); HM2$T_VOLNAME is 12 ASCII bytes at +0x1D8,
    // which is where the Files-11 home block puts it and where an ODS-2 reader
    // looks — 0x1F4 was this writer's own guess and landed inside the format string.
    const int homeBlockOffset = 512;
    const int volNameOffset = 0x1D8;
    var label = System.Text.Encoding.ASCII
      .GetString(image, homeBlockOffset + volNameOffset, 12)
      .TrimEnd('\0', ' ');
    Assert.That(label, Is.EqualTo("MYVOL"));

    // File content still round-trips.
    using var read = new MemoryStream(image, writable: false);
    var back = ((IArchiveFormatOperations)d).ExtractEntryToMemory(read, "HELLO.TXT", null);
    Assert.That(back, Is.EqualTo("payload"u8.ToArray()));
  }
}
