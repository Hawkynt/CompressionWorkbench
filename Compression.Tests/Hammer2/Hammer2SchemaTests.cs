using System.Text;
using Compression.Registry;
using FileSystem.Hammer2;

namespace Compression.Tests.Hammer2;

[TestFixture]
public class Hammer2SchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesLabelSchema() {
    var d = new Hammer2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "Label"), Is.True);
  }

  // HAMMER2's geometry floor forces a multi-MB (sparse) volume; write to a temp
  // file with holes and read the bytes back.
  [Test, Category("HappyPath")]
  public void Create_Label_NamesThePfsAndFilesRoundTrip() {
    var d = new Hammer2FormatDescriptor();
    var content = "payload"u8.ToArray();
    var path = Path.Combine(Path.GetTempPath(), "hammer2_schema_" + Guid.NewGuid().ToString("N") + ".img");
    try {
      using (var fs = File.Create(path))
        ((IArchiveCreatable)d).Create(
          fs, [ArchiveInputInfo.InMemory("hello.txt", content)],
          new FormatCreateOptions {
            FormatSpecific = new Dictionary<string, string> { ["Label"] = "MYPFS" },
          });

      var image = File.ReadAllBytes(path);

      // The labelled PFS inode carries its name inline (filename[] at +0x100);
      // the custom label must appear verbatim in the topology area.
      Assert.That(ContainsAscii(image, "MYPFS"), Is.True, "custom PFS label not written");

      // Files round-trip through the labelled (non-LOCAL) PFS.
      var files = new Hammer2Reader(image).ReadAllFiles();
      Assert.That(files.Keys, Does.Contain("hello.txt"));
      Assert.That(files["hello.txt"], Is.EqualTo(content));
    } finally {
      try { File.Delete(path); } catch { /* ignore */ }
    }
  }

  private static bool ContainsAscii(byte[] haystack, string needle) {
    var n = Encoding.ASCII.GetBytes(needle);
    for (var i = 0; i + n.Length <= haystack.Length; i++) {
      var match = true;
      for (var j = 0; j < n.Length; j++)
        if (haystack[i + j] != n[j]) { match = false; break; }
      if (match) return true;
    }
    return false;
  }
}
