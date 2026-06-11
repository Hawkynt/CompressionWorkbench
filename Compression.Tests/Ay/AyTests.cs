#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Ay;

namespace Compression.Tests.Ay;

[TestFixture]
public class AyTests {

  // Builds a minimal ZXAYEMUL file with 1 song. All pointers are big-endian,
  // signed-16, relative to their own offset.
  private static byte[] MakeSyntheticAy() {
    // Layout:
    //   0x00 "ZXAYEMUL" + version bytes + pointer fields (through 0x14)
    //   0x14 author string
    //   0x20 misc string
    //   0x2C song structure (1 entry, 4 bytes)
    //   0x30 song name string
    //   0x3C song data block
    var buf = new byte[0x50];
    "ZXAYEMUL"u8.ToArray().CopyTo(buf.AsSpan(0, 8));
    buf[0x08] = 0; // file version
    buf[0x09] = 1; // player version

    // SpecialPlayer @0x0A (none), Author @0x0C, Misc @0x0E.
    WriteRel(buf, 0x0C, 0x14); // author at 0x14
    WriteRel(buf, 0x0E, 0x20); // misc at 0x20
    buf[0x10] = 1; // num songs
    buf[0x11] = 0; // first song
    WriteRel(buf, 0x12, 0x2C); // song structure at 0x2C

    WriteAscii(buf, 0x14, "TheAuthor");
    WriteAscii(buf, 0x20, "MiscInfo");

    // Song structure entry: name ptr @0x2C, data ptr @0x2E.
    WriteRel(buf, 0x2C, 0x30); // name at 0x30
    WriteRel(buf, 0x2E, 0x3C); // data at 0x3C

    WriteAscii(buf, 0x30, "FirstSong");
    for (var i = 0; i < 8; ++i) buf[0x3C + i] = (byte)(0xA0 + i);
    return buf;
  }

  private static void WriteRel(byte[] buf, int off, int target) {
    var rel = (short)(target - off);
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(off, 2), (ushort)rel);
  }

  private static void WriteAscii(byte[] buf, int off, string s) {
    var a = Encoding.ASCII.GetBytes(s);
    Buffer.BlockCopy(a, 0, buf, off, a.Length);
  }

  [Test]
  public void List_ExposesFullMetadataAndSong() {
    using var ms = new MemoryStream(MakeSyntheticAy());
    var entries = new AyFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.ay"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("songs/01_")), Is.True);
  }

  [Test]
  public void Extract_FullByteIdentical_PointersResolved() {
    var blob = MakeSyntheticAy();
    var tmp = Path.Combine(Path.GetTempPath(), "ay_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new AyFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.ay")), Is.EqualTo(blob));
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("author = TheAuthor"));
      Assert.That(meta, Does.Contain("misc = MiscInfo"));
      Assert.That(meta, Does.Contain("num_songs = 1"));
      Assert.That(meta, Does.Contain("song_01_name = FirstSong"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_Malformed_DoesNotThrow() {
    var buf = "ZXAYEMUL"u8.ToArray().Concat(new byte[8]).ToArray();
    // Corrupt pointers to be wildly out of range.
    buf[0x0C] = 0x7F; buf[0x0D] = 0xFF;
    using var ms = new MemoryStream(buf);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new AyFormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "FULL.ay"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_Magic() {
    var d = new AyFormatDescriptor();
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("ZXAYEMUL"u8.ToArray()));
    Assert.That(d.Extensions, Does.Contain(".ay"));
  }
}
