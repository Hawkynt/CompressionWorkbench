using System.Text;
using Compression.Registry;
using FileSystem.Cpm;

namespace Compression.Tests.Cpm;

[TestFixture]
public class CpmTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void BuildRead_SingleFile_RoundTrips() {
    var body = Encoding.ASCII.GetBytes("Hello CP/M World!");
    var files = new (string, byte[], byte)[] { ("HELLO.TXT", body, (byte)0) };
    var image = CpmWriter.Build(files);
    Assert.That(image, Has.Length.EqualTo(256_256));

    var v = CpmReader.Read(image);
    Assert.That(v.Files, Has.Count.EqualTo(1));
    Assert.That(v.Files[0].Name, Is.EqualTo("HELLO"));
    Assert.That(v.Files[0].Extension, Is.EqualTo("TXT"));
    // File data is padded to the nearest 128-byte record; verify content prefix.
    Assert.That(v.Files[0].Data.AsSpan(0, body.Length).ToArray(), Is.EqualTo(body).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void BuildRead_MultiExtentFile_Reassembles() {
    // 20 KB file — forces 2 extents (16 KB + 4 KB).
    var body = new byte[20 * 1024];
    for (var i = 0; i < body.Length; i++) body[i] = (byte)(i & 0xFF);

    var image = CpmWriter.Build([("BIG.DAT", body, 0)]);
    var v = CpmReader.Read(image);
    Assert.That(v.Files, Has.Count.EqualTo(1));
    var retrieved = v.Files[0].Data.AsSpan(0, body.Length).ToArray();
    Assert.That(retrieved, Is.EqualTo(body).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_List_Roundtrip() {
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
    File.WriteAllBytes(tmp, Encoding.ASCII.GetBytes("abc"));
    try {
      var desc = new CpmFormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new ArchiveInputInfo(tmp, "A.TXT", false)], new FormatCreateOptions());
      ms.Position = 0;
      var listed = desc.List(ms, null);
      Assert.That(listed, Has.Count.EqualTo(1));
      Assert.That(listed[0].Name, Is.EqualTo("A.TXT"));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("EdgeCase")]
  public void CanAccept_RejectsLongStem() {
    var desc = new CpmFormatDescriptor();
    var ok = desc.CanAccept(new ArchiveInputInfo("/tmp/a", "TOOLONGNAME.X", false), out var reason);
    Assert.That(ok, Is.False);
    Assert.That(reason, Does.Contain("8 characters"));
  }

  [Test, Category("EdgeCase")]
  public void Read_UndersizedImage_Throws() {
    Assert.That(() => CpmReader.Read(new byte[1024]), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Build_TooManyFiles_Throws() {
    // 65 entries > 64-entry directory capacity — the writer must reject.
    var files = Enumerable.Range(0, 65).Select(i => ($"F{i:D3}.TXT", new byte[10], (byte)0)).ToList();
    Assert.That(() => CpmWriter.Build(files), Throws.InstanceOf<InvalidOperationException>());
  }
}
