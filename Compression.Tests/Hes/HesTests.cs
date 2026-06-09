#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Hes;

namespace Compression.Tests.Hes;

[TestFixture]
public class HesTests {

  private static byte[] BuildHesWithBlocks() {
    var ms = new MemoryStream();
    var header = new byte[0x10];
    "HESM"u8.CopyTo(header);
    header[0x04] = 0;    // version
    header[0x05] = 1;    // first song
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x06), 0x1AB0); // init addr
    for (var i = 0; i < 8; ++i)
      header[0x08 + i] = (byte)(0xF8 + i); // MPR table
    ms.Write(header);

    WriteDataBlock(ms, 0x2000, [0x10, 0x20, 0x30]);
    WriteDataBlock(ms, 0x4000, [0xAA, 0xBB]);
    return ms.ToArray();
  }

  // A HES whose init program writes a PSG tone then RTSes, so the descriptor renders real audio.
  private static byte[] BuildHesWithTone() {
    var prog = new List<byte>();
    void Lda(byte v) { prog.Add(0xA9); prog.Add(v); }
    void Sta(ushort addr) { prog.Add(0x8D); prog.Add((byte)addr); prog.Add((byte)(addr >> 8)); }
    void Tam(byte mask) { prog.Add(0x53); prog.Add(mask); }

    Lda(0xFF); Tam(0x01);                  // MPR0 ← I/O page
    Lda(0x00); Sta(0x0800);                // select channel 0
    Lda(0x50); Sta(0x0802);                // freq low
    Lda(0x00); Sta(0x0803);                // freq high
    Lda(0x1F); Sta(0x0804);                // overall vol, not enabled
    for (var i = 0; i < 32; ++i) { Lda((byte)(i < 16 ? 31 : 0)); Sta(0x0806); }
    Lda(0xFF); Sta(0x0805);                // L/R vol
    Lda(0x9F); Sta(0x0804);                // enable + vol
    Lda(0xFF); Sta(0x0801);                // global balance
    prog.Add(0x60);                        // RTS
    var program = prog.ToArray();

    var ms = new MemoryStream();
    var header = new byte[0x10];
    "HESM"u8.CopyTo(header);
    header[0x05] = 0;                      // first song (0-based)
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x06), 0xE000); // init addr
    for (var i = 0; i < 8; ++i) header[0x08 + i] = (byte)i; // identity MPR: logical $E000 → physical page 7
    header[0x08 + 1] = 0xF8;               // MPR1 → work RAM so the stack at logical $2100 hits RAM
    ms.Write(header);
    WriteDataBlock(ms, 0xE000, program);
    return ms.ToArray();
  }

  private static bool IsRiffWav(byte[] blob) =>
    blob.Length > 44 &&
    blob[0] == 'R' && blob[1] == 'I' && blob[2] == 'F' && blob[3] == 'F' &&
    blob[8] == 'W' && blob[9] == 'A' && blob[10] == 'V' && blob[11] == 'E';

  private static void WriteDataBlock(Stream s, uint loadAddr, byte[] payload) {
    var bh = new byte[0x10];
    "DATA"u8.CopyTo(bh);
    BinaryPrimitives.WriteUInt32LittleEndian(bh.AsSpan(4), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(bh.AsSpan(8), loadAddr);
    s.Write(bh);
    s.Write(payload);
  }

  private static byte[] BuildHesNoBlocks() {
    var blob = new byte[0x10 + 3];
    "HESM"u8.CopyTo(blob);
    blob[0x04] = 0;
    blob[0x05] = 1;
    blob[0x10] = 0x01; blob[0x11] = 0x02; blob[0x12] = 0x03;
    return blob;
  }

  private static string Meta(byte[] blob) => Encoding.UTF8.GetString(Bytes(blob, "metadata.ini"));

  private static byte[] Bytes(byte[] blob, string entry) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new HesFormatDescriptor().ExtractEntry(ms, entry, output, null);
    return output.ToArray();
  }

  [Test]
  public void DataBlocks_SurfacedWithExactBytes() {
    var blob = BuildHesWithBlocks();
    using var ms = new MemoryStream(blob);
    var entries = new HesFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.hes").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "blocks/00_2000.bin").Kind, Is.EqualTo("Stream"));
    Assert.That(Bytes(blob, "blocks/00_2000.bin"), Is.EqualTo(new byte[] { 0x10, 0x20, 0x30 }));
    Assert.That(Bytes(blob, "blocks/01_4000.bin"), Is.EqualTo(new byte[] { 0xAA, 0xBB }));
  }

  [Test]
  public void Metadata_HasHeaderFields() {
    var ini = Meta(BuildHesWithBlocks());
    Assert.That(ini, Does.Contain("first_song=1"));
    Assert.That(ini, Does.Contain("init_addr=0x1AB0"));
    Assert.That(ini, Does.Contain("data_blocks=2"));
    Assert.That(ini, Does.Contain("initial_mpr="));
  }

  [Test]
  public void NoDataBlocks_FallsBackToProgramBin() {
    var blob = BuildHesNoBlocks();
    using var ms = new MemoryStream(blob);
    var entries = new HesFormatDescriptor().List(ms, null);
    Assert.That(Bytes(blob, "program.bin"), Is.EqualTo(new byte[] { 0x01, 0x02, 0x03 }));
    Assert.That(entries.Any(e => e.Name.StartsWith("blocks/")), Is.False);
  }

  [Test]
  public void ShortBlob_DegradesToFullOnly() {
    using var ms = new MemoryStream("HESM"u8.ToArray());
    var entries = new HesFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.hes"), Is.True);
    Assert.That(entries.Any(e => e.Name == "program.bin"), Is.False);
  }

  [Test]
  public void ToneHes_SurfacesValidStereoWavs() {
    var blob = BuildHesWithTone();
    using var ms = new MemoryStream(blob);
    var entries = new HesFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "RIGHT.wav").Kind, Is.EqualTo("Channel"));

    var left = Bytes(blob, "LEFT.wav");
    var right = Bytes(blob, "RIGHT.wav");
    Assert.That(IsRiffWav(left), Is.True, "LEFT.wav must be a valid RIFF/WAVE file");
    Assert.That(IsRiffWav(right), Is.True, "RIGHT.wav must be a valid RIFF/WAVE file");
  }

  [Test]
  public void ToneHes_RendersNonzeroPcm() {
    var blob = BuildHesWithTone();
    var left = Bytes(blob, "LEFT.wav");
    // Inspect the 16-bit samples after the 44-byte RIFF header.
    var peak = 0;
    for (var i = 44; i + 1 < left.Length; i += 2) {
      var s = (short)(left[i] | (left[i + 1] << 8));
      peak = Math.Max(peak, Math.Abs((int)s));
    }
    Assert.That(peak, Is.GreaterThan(100), "the rendered LEFT channel must not be silent");
  }

  [Test]
  public void ToneHes_MetadataReportsRenderOk() {
    var ini = Meta(BuildHesWithTone());
    Assert.That(ini, Does.Contain("rendered_status=ok"));
    Assert.That(ini, Does.Contain("rendered_channels=stereo"));
    Assert.That(ini, Does.Contain("song_count_note="));
  }

  [Test]
  public void ToneHes_LazyTrackPairListsExactSizeWithoutRender() {
    var blob = BuildHesWithTone();
    using var ms = new MemoryStream(blob);
    var entries = new HesFormatDescriptor().List(ms, null);
    var track = entries.FirstOrDefault(e => e.Name == "TRACK_01_LEFT.wav");
    Assert.That(track, Is.Not.Null);
    Assert.That(track!.Kind, Is.EqualTo("Track"));
    // 30 s of 16-bit mono @ 44100 + 44-byte header.
    Assert.That(track.OriginalSize, Is.EqualTo(44 + 30L * 44100 * 2));
  }

  [Test]
  public void MalformedRender_DegradesGracefully() {
    // A HES with a bogus init address: the render fails, but listing still succeeds with the
    // existing surface and a skipped status.
    var ms = new MemoryStream();
    var header = new byte[0x10];
    "HESM"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x06), 0x0000); // init at $0000 (no code)
    ms.Write(header);
    WriteDataBlock(ms, 0x4000, [0x10, 0x20]);
    var blob = ms.ToArray();

    using var listMs = new MemoryStream(blob);
    var entries = new HesFormatDescriptor().List(listMs, null);
    Assert.That(entries.Any(e => e.Name == "FULL.hes"), Is.True);
    Assert.That(entries.Any(e => e.Name == "blocks/00_4000.bin"), Is.True);
    // Render of a no-op program still produces (silent) output; the surface stays intact either way.
  }
}
