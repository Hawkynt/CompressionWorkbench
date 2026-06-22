using Compression.Registry;
using FileSystem.RomFs;

namespace Compression.Tests.RomFs;

[TestFixture]
public class RomFsSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var d = new RomFsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_VolumeLabel_RoundTripsThroughSuperblock() {
    var d = new RomFsFormatDescriptor();
    var content = "payload"u8.ToArray();
    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(
      ms, [ArchiveInputInfo.InMemory("hello.txt", content)],
      new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYVOL" },
      });

    var image = ms.ToArray();
    using var read = new MemoryStream(image, writable: false);
    var reader = new RomFsReader(read);
    Assert.That(reader.VolumeName, Is.EqualTo("MYVOL"));

    var back = ((IArchiveFormatOperations)d).ExtractEntryToMemory(
      new MemoryStream(image, writable: false), "hello.txt", null);
    Assert.That(back, Is.EqualTo(content));
  }
}
