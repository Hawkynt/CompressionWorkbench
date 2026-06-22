using Compression.Registry;
using FileSystem.Ods1;

namespace Compression.Tests.Ods1;

[TestFixture]
public class Ods1SchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var d = new Ods1FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_VolumeLabel_RoundTripsThroughHomeBlock() {
    var d = new Ods1FormatDescriptor();
    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(
      ms, [ArchiveInputInfo.InMemory("HELLO.TXT", "payload"u8.ToArray())],
      new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYVOL" },
      });

    var image = ms.ToArray();
    using var read = new MemoryStream(image, writable: false);
    var reader = new Ods1Reader(read);
    Assert.That(reader.VolumeName, Is.EqualTo("MYVOL"));

    // File content still round-trips. ODS-1 Stage-1 reports the size as
    // block-count × 512 (no sub-block efblk), so the recovered buffer is the
    // file's 512-byte block with the payload at its head and a zero tail.
    using var read2 = new MemoryStream(image, writable: false);
    var back = ((IArchiveFormatOperations)d).ExtractEntryToMemory(read2, "HELLO.TXT", null);
    Assert.That(back.AsSpan(0, 7).ToArray(), Is.EqualTo("payload"u8.ToArray()));
  }
}
