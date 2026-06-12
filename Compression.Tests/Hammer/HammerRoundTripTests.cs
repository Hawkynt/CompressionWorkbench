using System.Text;
using FileSystem.Hammer;

namespace Compression.Tests.Hammer;

/// <summary>
/// Round-trips real file content through the HAMMER writer and reader:
/// <see cref="HammerWriter.AddFile"/> materialises regular files (inode + directory
/// entry + data records into the global B-Tree), and <see cref="HammerReader"/>
/// walks that B-Tree back to byte-exact names and contents.
/// </summary>
[TestFixture]
public class HammerRoundTripTests {

  // HAMMER's UNDO-FIFO floor forces a ~1 GB volume, larger than a MemoryStream can
  // hold (2^31-1). The image is overwhelmingly sparse, so write to a temp file
  // (with holes) and read the bytes back.
  private static byte[] WriteImage(params (string Name, byte[] Content)[] files) {
    var w = new HammerWriter { Label = "rt" };
    foreach (var (name, content) in files)
      w.AddFile(name, content);

    var path = Path.Combine(Path.GetTempPath(), "hammer_rt_" + Guid.NewGuid().ToString("N") + ".img");
    try {
      using (var fs = File.Create(path))
        w.WriteTo(fs);
      return File.ReadAllBytes(path);
    } finally {
      try { File.Delete(path); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void WriteThenRead_SingleSmallFile_RoundTripsExactBytes() {
    var content = "hello hammer\n"u8.ToArray();
    var image = WriteImage(("hello.txt", content));

    var reader = HammerReader.Open(image);
    Assert.That(reader.Valid, Is.True);

    var files = reader.ReadFiles().ToDictionary(f => f.Path, f => f.Content);
    Assert.That(files.Keys, Does.Contain("hello.txt"));
    Assert.That(files["hello.txt"], Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void WriteThenRead_MultipleFilesVariedSizes_AllRoundTrip() {
    var expected = new Dictionary<string, byte[]> {
      ["empty.bin"] = [],
      ["tiny"] = "x"u8.ToArray(),
      ["greeting.txt"] = "The quick brown fox jumps over the lazy dog.\n"u8.ToArray(),
      ["block.dat"] = Enumerable.Range(0, 200).Select(i => (byte)(i * 7)).ToArray(),
      ["bigger.dat"] = Enumerable.Range(0, 5000).Select(i => (byte)(i ^ (i >> 3))).ToArray(),
      // Spans multiple data records, exercising full 16 KB large-data blocks
      // (zone 10) plus a small-data tail (zone 11).
      ["multiblock.bin"] = Enumerable.Range(0, 40000).Select(i => (byte)(i * 37 + 11)).ToArray(),
    };

    var image = WriteImage(expected.Select(kv => (kv.Key, kv.Value)).ToArray());
    var reader = HammerReader.Open(image);
    var files = reader.ReadFiles().ToDictionary(f => f.Path, f => f.Content);

    foreach (var (name, content) in expected) {
      Assert.That(files.Keys, Does.Contain(name), $"missing {name}");
      Assert.That(files[name], Is.EqualTo(content), $"content mismatch for {name}");
    }
  }

  [Test, Category("HappyPath")]
  public void WriteThenRead_BinaryPayload_PreservesEveryByteValue() {
    var content = new byte[256];
    for (var i = 0; i < 256; ++i)
      content[i] = (byte)i;
    var image = WriteImage(("allbytes.bin", content));

    var reader = HammerReader.Open(image);
    var files = reader.ReadFiles().ToDictionary(f => f.Path, f => f.Content);
    Assert.That(files["allbytes.bin"], Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void WrittenImage_StillHasValidVolumeHeaderAndRootInode() {
    var image = WriteImage(("a.txt", "content a\n"u8.ToArray()));
    var hdr = HammerVolumeOndisk.TryParse(image);
    Assert.That(hdr.Valid, Is.True);
    Assert.That(hdr.VolLabel, Is.EqualTo("rt"));
    // root inode + at least one file inode counted.
    Assert.That(hdr.Vol0StatInodes, Is.GreaterThanOrEqualTo(2));
  }

  private static string Hex(byte[] b) => Convert.ToHexString(b);
}
