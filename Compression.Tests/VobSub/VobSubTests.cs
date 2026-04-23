using System.Text;
using FileFormat.VobSub;

namespace Compression.Tests.VobSub;

[TestFixture]
public class VobSubTests {

  private const string SampleIdx =
    "# VobSub index file, v7\n" +
    "# Created by test\n" +
    "size: 720x480\n" +
    "palette: 000000, ffffff, ff0000, 00ff00, 0000ff, ffff00\n" +
    "id: en, index: 0\n" +
    "timestamp: 00:00:01:000, filepos: 0000000000\n" +
    "timestamp: 00:00:03:500, filepos: 0000000020\n" +
    "timestamp: 00:00:06:250, filepos: 0000000040\n";

  private static byte[] BuildSubBytes() {
    // 64 bytes total: three 'frames' starting at offsets 0, 32, and 64-of-the-fact
    // (the slice helper takes whatever is between consecutive filepos values, with
    // the last one extending to EOF).
    var buf = new byte[64];
    for (var i = 0; i < buf.Length; i++) buf[i] = (byte)(i & 0xFF);
    return buf;
  }

  [Test, Category("HappyPath")]
  public void ReadIndex_ParsesAllFields() {
    var idx = VobSubReader.ReadIndex(SampleIdx);
    Assert.That(idx.Width, Is.EqualTo(720));
    Assert.That(idx.Height, Is.EqualTo(480));
    Assert.That(idx.Palette, Has.Count.EqualTo(6));
    Assert.That(idx.Language, Is.EqualTo("en"));
    Assert.That(idx.Entries, Has.Count.EqualTo(3));
    Assert.That(idx.Entries[0].Timestamp, Is.EqualTo(TimeSpan.FromMilliseconds(1000)));
    Assert.That(idx.Entries[1].FilePos, Is.EqualTo(0x20));
  }

  [Test, Category("HappyPath")]
  public void SliceFrames_RespectsBoundaries() {
    var idx = VobSubReader.ReadIndex(SampleIdx);
    var sub = BuildSubBytes();
    var frames = VobSubReader.SliceFrames(idx, sub);
    Assert.That(frames, Has.Count.EqualTo(3));
    Assert.That(frames[0], Has.Length.EqualTo(0x20));    // 0x00..0x20
    Assert.That(frames[1], Has.Length.EqualTo(0x20));    // 0x20..0x40
    Assert.That(frames[2], Has.Length.EqualTo(0));       // 0x40..0x40 (EOF)
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListPair_ReturnsMetadataAndFrames() {
    var idxBytes = Encoding.UTF8.GetBytes(SampleIdx);
    var subBytes = BuildSubBytes();
    var entries = new VobSubFormatDescriptor().ListPair(idxBytes, subBytes);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "index.idx"), Is.True);
    Assert.That(entries.Count(e => e.Name.StartsWith("subtitle_")), Is.EqualTo(3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_ExtractPair_WritesAllFiles() {
    var idxBytes = Encoding.UTF8.GetBytes(SampleIdx);
    var subBytes = BuildSubBytes();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      new VobSubFormatDescriptor().ExtractPair(idxBytes, subBytes, tmp, null);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "index.idx")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "subtitle_000.bin")), Is.True);
      Assert.That(new FileInfo(Path.Combine(tmp, "subtitle_000.bin")).Length, Is.EqualTo(0x20));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListFromMemoryStream_StillProducesMetadata() {
    // No sibling .sub available — descriptor should still surface index metadata.
    var idxBytes = Encoding.UTF8.GetBytes(SampleIdx);
    using var ms = new MemoryStream(idxBytes);
    var entries = new VobSubFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "index.idx"), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void ReadIndex_MissingHeader_Throws() {
    Assert.That(() => VobSubReader.ReadIndex("size: 720x480\n"),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void SliceFrames_FilePosBeyondEof_ClampsAtEof() {
    var idx = VobSubReader.ReadIndex(
      "# VobSub index file, v7\n" +
      "size: 1x1\n" +
      "timestamp: 00:00:00:000, filepos: 0000000000\n" +
      "timestamp: 00:00:01:000, filepos: 00000000FF\n"); // way past EOF
    var sub = new byte[16];
    var frames = VobSubReader.SliceFrames(idx, sub);
    Assert.That(frames, Has.Count.EqualTo(2));
    Assert.That(frames[0], Has.Length.EqualTo(16));
  }
}
