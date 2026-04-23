using System.Text;
using Compression.Registry;
using FileSystem.Lif;

namespace Compression.Tests.Lif;

[TestFixture]
public class LifTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void BuildRead_RoundTripsFiles() {
    var files = new (string, byte[])[] {
      ("HELLO", Encoding.ASCII.GetBytes("Hello World!")),
      ("FOO", new byte[300]),
    };

    var image = LifWriter.Build(files, volumeLabel: "CWBTST");
    var v = LifReader.Read(image);
    Assert.That(v.Label, Is.EqualTo("CWBTST"));
    Assert.That(v.Files, Has.Count.EqualTo(2));
    Assert.That(v.Files[0].Name, Is.EqualTo("HELLO"));
    Assert.That(v.Files[1].Name, Is.EqualTo("FOO"));

    var hello = LifReader.Extract(v, v.Files[0]);
    Assert.That(hello.AsSpan(0, 12).ToArray(), Is.EqualTo(files[0].Item2).AsCollection);

    var foo = LifReader.Extract(v, v.Files[1]);
    // FOO is 300 bytes → rounded up to 2 sectors = 512 bytes. The first 300 bytes are zeros.
    Assert.That(foo, Has.Length.EqualTo(512));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsEntries() {
    var files = new (string, byte[])[] { ("ABC", Encoding.ASCII.GetBytes("x")) };
    var image = LifWriter.Build(files);
    using var ms = new MemoryStream(image);
    var entries = new LifFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("ABC"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughReader() {
    var tmpIn1 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + "x");
    var tmpIn2 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + "y");
    File.WriteAllText(tmpIn1, "one");
    File.WriteAllText(tmpIn2, "two");
    try {
      var desc = new LifFormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [
        new ArchiveInputInfo(tmpIn1, "FILEA", false),
        new ArchiveInputInfo(tmpIn2, "FILEB", false),
      ], new FormatCreateOptions());
      ms.Position = 0;
      var listed = desc.List(ms, null);
      Assert.That(listed, Has.Count.EqualTo(2));
      Assert.That(listed.Select(e => e.Name), Is.EquivalentTo(new[] { "FILEA", "FILEB" }));
    } finally {
      File.Delete(tmpIn1);
      File.Delete(tmpIn2);
    }
  }

  [Test, Category("EdgeCase")]
  public void CanAccept_RejectsLongFilenames() {
    var desc = new LifFormatDescriptor();
    var ok = desc.CanAccept(new ArchiveInputInfo("/tmp/a", "NAME_OVER_LEN", false), out var reason);
    Assert.That(ok, Is.False);
    Assert.That(reason, Does.Contain("10 characters"));
  }

  [Test, Category("EdgeCase")]
  public void Read_BadMagic_Throws() {
    var buf = new byte[256];
    Assert.That(() => LifReader.Read(buf), Throws.InstanceOf<InvalidDataException>());
  }
}
