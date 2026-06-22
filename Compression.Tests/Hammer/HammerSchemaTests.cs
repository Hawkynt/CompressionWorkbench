using Compression.Registry;
using FileSystem.Hammer;

namespace Compression.Tests.Hammer;

[TestFixture]
public class HammerSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesLabelSchema() {
    var d = new HammerFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "Label"), Is.True);
  }

  // The HAMMER UNDO-FIFO floor forces a ~1 GB (sparse) volume, too large for a
  // MemoryStream — write to a temp file with holes and read the bytes back.
  [Test, Category("HappyPath")]
  public void Create_Label_RoundTripsThroughVolumeHeader() {
    var d = new HammerFormatDescriptor();
    var content = "payload"u8.ToArray();
    var path = Path.Combine(Path.GetTempPath(), "hammer_schema_" + Guid.NewGuid().ToString("N") + ".img");
    try {
      using (var fs = File.Create(path))
        ((IArchiveCreatable)d).Create(
          fs, [ArchiveInputInfo.InMemory("hello.txt", content)],
          new FormatCreateOptions {
            FormatSpecific = new Dictionary<string, string> { ["Label"] = "customlbl" },
          });

      var image = File.ReadAllBytes(path);
      var hdr = HammerVolumeOndisk.TryParse(image);
      Assert.That(hdr.Valid, Is.True);
      Assert.That(hdr.VolLabel, Is.EqualTo("customlbl"));

      var files = HammerReader.Open(image).ReadFiles().ToDictionary(f => f.Path, f => f.Content);
      Assert.That(files.Keys, Does.Contain("hello.txt"));
      Assert.That(files["hello.txt"], Is.EqualTo(content));
    } finally {
      try { File.Delete(path); } catch { /* ignore */ }
    }
  }
}
