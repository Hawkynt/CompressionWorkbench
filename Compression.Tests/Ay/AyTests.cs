#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Ay;

namespace Compression.Tests.Ay;

[TestFixture]
public class AyTests {

  // Writes a big-endian signed self-relative pointer at `fieldPos` targeting absolute `target`.
  private static void WritePtr(byte[] b, int fieldPos, int target) {
    var rel = (short)(target - fieldPos);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(fieldPos), (ushort)rel);
  }

  // Builds a one-song AY with an author string and one memory block.
  private static byte[] BuildAy(out byte[] blockData, bool corruptBlockPointer = false) {
    // Layout we lay out by hand:
    //  0x00 ZXAYEMUL
    //  0x08 fileVer, 0x09 playerVer
    //  0x0A pSpecialPlayer, 0x0C pAuthor, 0x0E pMisc, 0x10 numSongs-1, 0x11 firstSong-1, 0x12 pSongs
    //  0x14 song table (1 entry: pName @0x14, pData @0x16)
    //  0x18 song-data struct (14 bytes): regs(4) noise(2) pPoints(2) ... pAddresses @ 0x18+12 = 0x24
    //  0x26 block list entry: addr(2) len(2) pData(2) @0x2A
    //  0x2C terminator (addr=0,len=0)
    //  0x30 author string "Author A\0"
    //  0x3A song name "Song One\0"
    //  0x44 block payload (4 bytes)
    blockData = [0xDE, 0xAD, 0xBE, 0xEF];
    var b = new byte[0x60];
    "ZXAYEMUL"u8.CopyTo(b);
    b[0x08] = 0; // file version
    b[0x09] = 0; // player version
    WritePtr(b, 0x0A, 0x0A); // pSpecialPlayer → self (effectively unused here)
    WritePtr(b, 0x0C, 0x30); // pAuthor → "Author A"
    WritePtr(b, 0x0E, 0x0E); // pMisc → self/empty
    b[0x10] = 0; // numSongs-1 → 1 song
    b[0x11] = 0; // firstSong-1
    WritePtr(b, 0x12, 0x14); // pSongs → song table

    // song table entry 0
    WritePtr(b, 0x14, 0x3A); // pName → "Song One"
    WritePtr(b, 0x16, 0x18); // pData → song-data struct

    // song-data struct at 0x18 (regs/noise/pPoints not interpreted)
    // pAddresses is at 0x18 + 12 = 0x24
    if (corruptBlockPointer)
      BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x24), 0x7FFF); // huge positive → out of range
    else
      WritePtr(b, 0x24, 0x26); // pAddresses → block list

    // block list entry at 0x26
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x26), 0x8000); // address
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x28), (ushort)blockData.Length); // length
    WritePtr(b, 0x2A, 0x44); // pData → payload
    // terminator
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x2C), 0); // addr=0
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x2E), 0); // len=0

    Encoding.ASCII.GetBytes("Author A").CopyTo(b, 0x30);
    Encoding.ASCII.GetBytes("Song One").CopyTo(b, 0x3A);
    blockData.CopyTo(b, 0x44);
    return b;
  }

  private static string Meta(byte[] blob) => Encoding.UTF8.GetString(Bytes(blob, "metadata.ini"));

  private static byte[] Bytes(byte[] blob, string entry) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AyFormatDescriptor().ExtractEntry(ms, entry, output, null);
    return output.ToArray();
  }

  [Test]
  public void PointerChase_SurfacesAuthorSongNameAndBlock() {
    var blob = BuildAy(out var blockData);
    using var ms = new MemoryStream(blob);
    var entries = new AyFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.ay").Kind, Is.EqualTo("Container"));

    var ini = Meta(blob);
    Assert.That(ini, Does.Contain("author=Author A"));
    Assert.That(ini, Does.Contain("num_songs=1"));
    Assert.That(ini, Does.Contain("song0_name=Song One"));

    var block = entries.First(e => e.Kind == "Stream");
    Assert.That(block.Name, Does.StartWith("songs/00_SongOne_8000"));
    Assert.That(Bytes(blob, block.Name), Is.EqualTo(blockData));
  }

  [Test]
  public void OutOfBoundsPointer_DegradesGracefully() {
    var blob = BuildAy(out _, corruptBlockPointer: true);
    using var ms = new MemoryStream(blob);
    var entries = new AyFormatDescriptor().List(ms, null);

    // Header still parses (author/song name), but no block is surfaced.
    Assert.That(entries.Any(e => e.Name == "FULL.ay"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Stream"), Is.False, "out-of-range block pointer yields no block");
    var ini = Meta(blob);
    Assert.That(ini, Does.Contain("author=Author A"));
    Assert.That(ini, Does.Contain("song0_name=Song One"));
  }

  [Test]
  public void ShortBlob_DegradesToFullOnly() {
    using var ms = new MemoryStream("ZXAYEMUL"u8.ToArray());
    var entries = new AyFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.ay"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Stream"), Is.False);
  }
}
