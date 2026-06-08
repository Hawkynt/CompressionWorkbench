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

  // ── player end-to-end ────────────────────────────────────────────────────────

  // Builds a runnable AY whose init routine programs AY tone A (period + volume + mixer) via
  // OUT to $FFFD/$BFFD, and whose interrupt routine is a bare RET.
  private static byte[] BuildRenderableAy() {
    // We place the Z80 code, the points block and the block list inside one file, then load
    // the code into RAM at $C000 via a single memory block.
    var b = new byte[0x100];
    "ZXAYEMUL"u8.CopyTo(b);
    b[0x08] = 0; b[0x09] = 0;
    WritePtr(b, 0x0A, 0x0A);
    WritePtr(b, 0x0C, 0x0C); // author empty
    WritePtr(b, 0x0E, 0x0E); // misc empty
    b[0x10] = 0; // 1 song
    b[0x11] = 0;
    WritePtr(b, 0x12, 0x14); // song table

    // song table entry 0
    WritePtr(b, 0x14, 0x18); // name → (we point into a small string)
    WritePtr(b, 0x16, 0x20); // pData → song-data struct at 0x20

    // a tiny name at 0x18
    Encoding.ASCII.GetBytes("T").CopyTo(b, 0x18);

    // song-data struct at 0x20 (14 bytes): +10 points, +12 addresses
    WritePtr(b, 0x20 + 10, 0x40); // pPoints → 0x40
    WritePtr(b, 0x20 + 12, 0x50); // pAddresses → 0x50

    // points block at 0x40: stack(2), init(2), interrupt(2) — all big-endian values
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x40), 0xFF00); // SP
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x42), 0xC000); // init addr
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x44), 0xC020); // interrupt addr (a RET)

    // Z80 code we will load at $C000.
    // init: select reg 0, write fine period; reg 1 coarse; reg 8 volume; reg 7 mixer; RET.
    var code = new List<byte>();
    void OutPsg(byte reg, byte value) {
      // LD A,reg ; LD BC,$FFFD ; OUT (C),A  (select) ; LD A,value ; LD BC,$BFFD ; OUT (C),A
      code.AddRange([0x3E, reg, 0x01, 0xFD, 0xFF, 0xED, 0x79]);
      code.AddRange([0x3E, value, 0x01, 0xFD, 0xBF, 0xED, 0x79]);
    }
    OutPsg(0x00, 0x00); // tone A fine
    OutPsg(0x01, 0x01); // tone A coarse → period $100
    OutPsg(0x08, 0x0F); // channel A full volume
    OutPsg(0x07, 0xFE); // mixer: tone A enabled
    code.Add(0xC9);     // RET (init done)
    // The interrupt routine sits at $C020: just RET. Pad to offset 0x20 within the block.
    while (code.Count < 0x20) code.Add(0x00);
    code.Add(0xC9);     // RET at $C020

    var codeArr = code.ToArray();

    // block list at 0x50: addr=$C000, len=code, pData→0x60
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x50), 0xC000);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x52), (ushort)codeArr.Length);
    WritePtr(b, 0x54, 0x60); // pData → code at 0x60
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x56), 0); // terminator addr
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x58), 0);

    var full = new byte[0x60 + codeArr.Length];
    Array.Copy(b, full, 0x60);
    codeArr.CopyTo(full, 0x60);
    return full;
  }

  [Test]
  public void Player_RendersStereoTone() {
    var blob = BuildRenderableAy();
    using var ms = new MemoryStream(blob);
    var entries = new AyFormatDescriptor().List(ms, null);

    var left = entries.FirstOrDefault(e => e.Name == "LEFT.wav");
    var right = entries.FirstOrDefault(e => e.Name == "RIGHT.wav");
    Assert.That(left, Is.Not.Null, "AY should surface a rendered LEFT.wav");
    Assert.That(right, Is.Not.Null, "AY should surface a rendered RIGHT.wav");
    Assert.That(left!.Kind, Is.EqualTo("Channel"));

    var wav = Bytes(blob, "LEFT.wav");
    Assert.That(wav.Length, Is.GreaterThan(44 + 10000));
    Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));

    // Tone A pans to the left in ABC mode; the left channel must carry audio.
    var peak = 0;
    for (var i = 44; i + 1 < wav.Length; i += 2)
      peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(i))));
    Assert.That(peak, Is.GreaterThan(0), "the rendered tone must be audible on the left channel");

    var ini = Meta(blob);
    Assert.That(ini, Does.Contain("rendered_chip=AY-3-8910"));
    Assert.That(ini, Does.Contain("rendered_stereo=ABC"));
  }

  // ── multi-song subtune surfacing ──────────────────────────────────────────

  // A runnable 2-song AY. Both songs share one code region loaded at $C000 but have distinct init
  // points: song 0's init programs tone-A period $0100, song 1's programs $0200, so the two
  // subtunes render audibly different tones. Each song's interrupt routine is a bare RET.
  private static byte[] BuildMultiSongAy() {
    var b = new byte[0x100];
    "ZXAYEMUL"u8.CopyTo(b);
    b[0x08] = 0; b[0x09] = 0;
    WritePtr(b, 0x0A, 0x0A);
    WritePtr(b, 0x0C, 0x0C);
    WritePtr(b, 0x0E, 0x0E);
    b[0x10] = 1; // numSongs-1 → 2 songs
    b[0x11] = 0;
    WritePtr(b, 0x12, 0x14); // song table (2 entries of 4 bytes: 0x14, 0x18)

    // song 0 entry: name @0x1C, data struct @0x30
    WritePtr(b, 0x14, 0x1C);
    WritePtr(b, 0x16, 0x30);
    // song 1 entry: name @0x1E, data struct @0x40
    WritePtr(b, 0x18, 0x1E);
    WritePtr(b, 0x1A, 0x40);

    Encoding.ASCII.GetBytes("A").CopyTo(b, 0x1C);
    Encoding.ASCII.GetBytes("B").CopyTo(b, 0x1E);

    // song-data struct 0 at 0x30 (+10 points, +12 addresses)
    WritePtr(b, 0x30 + 10, 0x50); // pPoints
    WritePtr(b, 0x30 + 12, 0x70); // pAddresses (shared block list)
    // song-data struct 1 at 0x40
    WritePtr(b, 0x40 + 10, 0x58); // pPoints
    WritePtr(b, 0x40 + 12, 0x70); // pAddresses (same loaded code)

    // points block for song 0 at 0x50: SP, init $C000, interrupt $C100 (RET)
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x50), 0xFF00);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x52), 0xC000);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x54), 0xC100);
    // points block for song 1 at 0x58: SP, init $C080, interrupt $C100 (RET)
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x58), 0xFF00);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x5A), 0xC080);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x5C), 0xC100);

    // Z80 code loaded at $C000. Each init routine is ~57 bytes, so songs sit 0x80 apart.
    var code = new List<byte>();
    void OutPsg(byte reg, byte value) {
      code.AddRange([0x3E, reg, 0x01, 0xFD, 0xFF, 0xED, 0x79]);
      code.AddRange([0x3E, value, 0x01, 0xFD, 0xBF, 0xED, 0x79]);
    }
    // Song 0 init at $C000: tone-A period $0100, full volume, mixer tone A on, RET.
    OutPsg(0x00, 0x00); OutPsg(0x01, 0x01);
    OutPsg(0x08, 0x0F); OutPsg(0x07, 0xFE);
    code.Add(0xC9);
    while (code.Count < 0x80) code.Add(0x00);
    // Song 1 init at $C080: tone-A period $0200 (different tone), full volume, mixer, RET.
    OutPsg(0x00, 0x00); OutPsg(0x01, 0x02);
    OutPsg(0x08, 0x0F); OutPsg(0x07, 0xFE);
    code.Add(0xC9);
    while (code.Count < 0x100) code.Add(0x00);
    code.Add(0xC9); // interrupt RET at $C100
    var codeArr = code.ToArray();

    // block list at 0x70: addr=$C000, len=code, pData→0x80
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x70), 0xC000);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x72), (ushort)codeArr.Length);
    WritePtr(b, 0x74, 0x80);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x76), 0);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x78), 0);

    var full = new byte[0x80 + codeArr.Length];
    Array.Copy(b, full, 0x80);
    codeArr.CopyTo(full, 0x80);
    return full;
  }

  [Test]
  public void MultiSong_SurfacesStereoTrackPairs_WithExactSizes() {
    var blob = BuildMultiSongAy();
    using var ms = new MemoryStream(blob);
    var entries = new AyFormatDescriptor().List(ms, null);

    var tracks = entries.Where(e => e.Kind == "Track").Select(e => e.Name).ToList();
    Assert.That(tracks, Is.EqualTo(new[] {
      "TRACK_01_LEFT.wav", "TRACK_01_RIGHT.wav",
      "TRACK_02_LEFT.wav", "TRACK_02_RIGHT.wav",
    }));

    var expected = 44L + 30L * 44100L * 2L;
    Assert.That(entries.First(e => e.Name == "TRACK_01_LEFT.wav").OriginalSize, Is.EqualTo(expected));
    Assert.That(Meta(blob), Does.Contain("total_tracks=2"));
  }

  [Test]
  public void MultiSong_TracksRenderExactBytes_AndSongsDiffer() {
    var blob = BuildMultiSongAy();
    using var ms = new MemoryStream(blob);
    var declared = new AyFormatDescriptor().List(ms, null)
      .First(e => e.Name == "TRACK_01_LEFT.wav").OriginalSize;

    var t1 = Bytes(blob, "TRACK_01_LEFT.wav");
    var t2 = Bytes(blob, "TRACK_02_LEFT.wav");

    Assert.That(Encoding.ASCII.GetString(t1, 0, 4), Is.EqualTo("RIFF"));
    Assert.That(t1.Length, Is.EqualTo(declared), "declared size must equal produced bytes exactly");
    Assert.That(t2, Is.Not.EqualTo(t1), "song 2 must render a distinct tone from song 1");
  }
}
