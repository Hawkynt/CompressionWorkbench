using System.Text;
using FileSystem.Hammer2;

namespace Compression.Tests.Hammer2;

/// <summary>
/// Round-trips real file content through the HAMMER2 writer and reader:
/// <see cref="Hammer2Writer.AddFile"/> materialises a regular file (embedded
/// direct data when the payload fits the inode's 512-byte union, or a
/// <c>HAMMER2_BREF_TYPE_DATA</c> block otherwise), and
/// <see cref="Hammer2Reader"/> walks the blockref tree back to the exact bytes.
/// </summary>
[TestFixture]
public class Hammer2RoundTripTests {

  private static byte[] WriteImage(params (string Name, byte[] Content)[] files) {
    var w = new Hammer2Writer { Label = "test" };
    foreach (var (name, content) in files)
      w.AddFile(name, content);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void WriteThenRead_EmbeddedSmallFile_ByteExact() {
    var payload = "hello hammer2\n"u8.ToArray();
    var img = WriteImage(("hello.txt", payload));

    var reader = new Hammer2Reader(img);
    var files = reader.ReadAllFiles();

    Assert.That(files.Keys, Does.Contain("hello.txt"));
    Assert.That(files["hello.txt"], Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void WriteThenRead_LargeFileViaDataBlock_ByteExact() {
    var payload = new byte[4000];
    for (var i = 0; i < payload.Length; ++i)
      payload[i] = (byte)(i * 31 + 7);
    var img = WriteImage(("big.bin", payload));

    var reader = new Hammer2Reader(img);
    var files = reader.ReadAllFiles();

    Assert.That(files["big.bin"], Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void WriteThenRead_MultipleMixedFiles_AllByteExact() {
    var small = Encoding.ASCII.GetBytes("tiny");
    var exactly512 = new byte[512];
    new Random(1).NextBytes(exactly512);
    var big = new byte[2049];
    new Random(2).NextBytes(big);

    var img = WriteImage(
      ("a.txt", small),
      ("b.dat", exactly512),
      ("c.bin", big),
      ("empty", []));

    var reader = new Hammer2Reader(img);
    var files = reader.ReadAllFiles();

    Assert.That(files["a.txt"], Is.EqualTo(small));
    Assert.That(files["b.dat"], Is.EqualTo(exactly512));
    Assert.That(files["c.bin"], Is.EqualTo(big));
    Assert.That(files["empty"], Is.EqualTo(Array.Empty<byte>()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListAndExtract_YieldRealFiles() {
    var payload = "round trip\n"u8.ToArray();
    var img = WriteImage(("doc.txt", payload));

    var d = new Hammer2FormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("doc.txt"));

    var outDir = Path.Combine(Path.GetTempPath(), "hammer2rt_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      ms.Position = 0;
      d.Extract(ms, outDir, null, ["doc.txt"]);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "doc.txt")), Is.EqualTo(payload));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }
}
