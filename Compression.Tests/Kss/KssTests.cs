#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Kss;

namespace Compression.Tests.Kss;

[TestFixture]
public class KssTests {

  private static byte[] BuildKscc(byte deviceFlags = 0x02) {
    var blob = new byte[0x10 + 4];
    blob[0] = 0x4B; blob[1] = 0x53; blob[2] = 0x43; blob[3] = 0x43; // "KSCC"
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x04), 0x0000); // load
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x06), 0x1234); // dataLen
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x08), 0x0010); // init
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0A), 0x0013); // play
    blob[0x0C] = 0x00; // start bank
    blob[0x0D] = 0x00; // extra banks
    blob[0x0E] = 0x00; // extra header len
    blob[0x0F] = deviceFlags;
    blob[0x10] = 0xDE; blob[0x11] = 0xAD; blob[0x12] = 0xBE; blob[0x13] = 0xEF;
    return blob;
  }

  private static byte[] BuildKssx() {
    // KSSX with a 0x10-byte extension block; payload follows at 0x20.
    var blob = new byte[0x10 + 0x10 + 2];
    blob[0] = 0x4B; blob[1] = 0x53; blob[2] = 0x53; blob[3] = 0x58; // "KSSX"
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x06), 0x2000); // dataLen
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x08), 0x0010);
    blob[0x0E] = 0x10; // extra header length → KSSX extension present
    blob[0x0F] = 0x01; // FMPAC
    // extension block at 0x10
    blob[0x10] = 0x04; // extra device flags
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x11), 1); // first song
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x13), 8); // song count
    // payload at 0x20
    blob[0x20] = 0xCA; blob[0x21] = 0xFE;
    return blob;
  }

  private static string Meta(byte[] blob) => Encoding.UTF8.GetString(Bytes(blob, "metadata.ini"));

  private static byte[] Bytes(byte[] blob, string entry) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new KssFormatDescriptor().ExtractEntry(ms, entry, output, null);
    return output.ToArray();
  }

  [Test]
  public void Kscc_SurfacesFullMetadataAndProgram() {
    var blob = BuildKscc();
    using var ms = new MemoryStream(blob);
    var entries = new KssFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.kss").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "program.bin").Kind, Is.EqualTo("Stream"));
    Assert.That(Bytes(blob, "program.bin"), Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
  }

  [Test]
  public void Kscc_MetadataFieldsAndDevices() {
    var ini = Meta(BuildKscc(deviceFlags: 0x02)); // SCC
    Assert.That(ini, Does.Contain("variant=KSCC"));
    Assert.That(ini, Does.Contain("data_len=0x1234"));
    Assert.That(ini, Does.Contain("init_addr=0x0010"));
    Assert.That(ini, Does.Contain("SCC"));
  }

  [Test]
  public void Kssx_ParsesExtensionAndPayloadAfterExtraHeader() {
    var blob = BuildKssx();
    var ini = Meta(blob);
    Assert.That(ini, Does.Contain("variant=KSSX"));
    Assert.That(ini, Does.Contain("first_song=1"));
    Assert.That(ini, Does.Contain("song_count=8"));

    Assert.That(Bytes(blob, "program.bin"), Is.EqualTo(new byte[] { 0xCA, 0xFE }), "payload begins after the extension block");
  }

  [Test]
  public void ShortBlob_DegradesToFullOnly() {
    using var ms = new MemoryStream(new byte[4]);
    var entries = new KssFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.kss"), Is.True);
    Assert.That(entries.Any(e => e.Name == "program.bin"), Is.False);
  }

  // ── player end-to-end ────────────────────────────────────────────────────────

  // Builds a runnable KSS whose init writes MSX PSG regs (tone A) via OUT to $A0/$A1, play=RET.
  private static byte[] BuildRenderableKss(byte deviceFlags = 0x00) {
    const ushort loadAddr = 0x9000;

    var code = new List<byte>();
    void OutPsg(byte reg, byte value) {
      // LD A,reg ; OUT ($A0),A ; LD A,value ; OUT ($A1),A
      code.AddRange([0x3E, reg, 0xD3, 0xA0]);
      code.AddRange([0x3E, value, 0xD3, 0xA1]);
    }
    OutPsg(0x00, 0x00); // tone A fine
    OutPsg(0x01, 0x01); // tone A coarse → period $100
    OutPsg(0x08, 0x0F); // channel A volume
    OutPsg(0x07, 0xFE); // mixer: tone A enabled
    code.Add(0xC9);     // RET (init)
    var initLen = code.Count;
    code.Add(0xC9);     // play: RET, right after init

    var codeArr = code.ToArray();
    var initAddr = loadAddr;
    var playAddr = (ushort)(loadAddr + initLen);

    var blob = new byte[0x10 + codeArr.Length];
    blob[0] = 0x4B; blob[1] = 0x53; blob[2] = 0x43; blob[3] = 0x43; // KSCC
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x04), loadAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x06), (ushort)codeArr.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x08), initAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0A), playAddr);
    blob[0x0F] = deviceFlags;
    codeArr.CopyTo(blob, 0x10);
    return blob;
  }

  [Test]
  public void Player_RendersStereoTone() {
    var blob = BuildRenderableKss();
    using var ms = new MemoryStream(blob);
    var entries = new KssFormatDescriptor().List(ms, null);

    var left = entries.FirstOrDefault(e => e.Name == "LEFT.wav");
    Assert.That(left, Is.Not.Null, "KSS should surface a rendered LEFT.wav");
    Assert.That(left!.Kind, Is.EqualTo("Channel"));

    var wav = Bytes(blob, "LEFT.wav");
    Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
    var peak = 0;
    for (var i = 44; i + 1 < wav.Length; i += 2)
      peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(i))));
    Assert.That(peak, Is.GreaterThan(0), "the rendered PSG tone must be audible");

    var ini = Meta(blob);
    Assert.That(ini, Does.Contain("rendered_chip=PSG"));
  }

  [Test]
  public void Player_DeviceFlagged_RendersPsgWithNote() {
    var blob = BuildRenderableKss(deviceFlags: 0x02); // SCC enabled
    using var ms = new MemoryStream(blob);
    var entries = new KssFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True, "PSG is still rendered even with extra devices");
    var ini = Meta(blob);
    Assert.That(ini, Does.Contain("rendered_note="));
    Assert.That(ini, Does.Contain("not synthesised"));
  }

  // ── multi-song subtune surfacing ──────────────────────────────────────────

  // A runnable KSSX declaring a song count of 2. Init is entered with A = song index; it stores A
  // into the PSG tone-A coarse-period register so each subtune renders a distinct tone. Play=RET.
  private static byte[] BuildMultiSongKssx(int songCount = 2) {
    const ushort loadAddr = 0x9000;

    var code = new List<byte>();
    void OutPsg(byte reg, byte value) {
      code.AddRange([0x3E, reg, 0xD3, 0xA0]);   // LD A,reg ; OUT ($A0),A
      code.AddRange([0x3E, value, 0xD3, 0xA1]); // LD A,value ; OUT ($A1),A
    }
    // Init: A = song index on entry. Stash it, set fine period 0 + volume + mixer, then use the
    // stashed song index (offset by 1) as the coarse-period register → distinct tone per song.
    code.AddRange([0x32, 0x00, 0x80]);          // LD ($8000),A  stash song index
    OutPsg(0x00, 0x00);                         // tone A fine = 0
    OutPsg(0x08, 0x0F);                         // channel A volume
    OutPsg(0x07, 0xFE);                         // mixer tone A on
    code.AddRange([0x3E, 0x01, 0xD3, 0xA0]);    // LD A,#1 ; OUT ($A0),A  select reg 1 (coarse)
    code.AddRange([0x3A, 0x00, 0x80]);          // LD A,($8000)  reload song index
    code.AddRange([0x3C]);                       // INC A          (song 0 → 1, song 1 → 2)
    code.AddRange([0xD3, 0xA1]);                // OUT ($A1),A    coarse period (song-dependent)
    code.Add(0xC9);                             // init RET
    var initLen = code.Count;
    code.Add(0xC9);                             // play RET

    var codeArr = code.ToArray();
    var initAddr = loadAddr;
    var playAddr = (ushort)(loadAddr + initLen);

    var blob = new byte[0x20 + codeArr.Length];
    blob[0] = 0x4B; blob[1] = 0x53; blob[2] = 0x53; blob[3] = 0x58; // KSSX
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x04), loadAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x06), (ushort)codeArr.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x08), initAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0A), playAddr);
    blob[0x0E] = 0x10; // extra header length → KSSX extension present
    blob[0x0F] = 0x00; // PSG only
    // extension block at 0x10
    blob[0x10] = 0x00; // extra device flags
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x11), 0);             // first song
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x13), (ushort)songCount); // song count
    codeArr.CopyTo(blob, 0x20);
    return blob;
  }

  [Test]
  public void Kscc_NoTrackList_PlainKsccGetsNoSubtunes() {
    // A plain KSCC carries no subtune table → no TRACK entries (only the default LEFT/RIGHT).
    var blob = BuildRenderableKss();
    using var ms = new MemoryStream(blob);
    var entries = new KssFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Kind == "Track"), Is.False, "plain KSCC must not surface a track list");
    Assert.That(Meta(blob), Does.Not.Contain("total_tracks="));
  }

  [Test]
  public void Kssx_MultiSong_SurfacesStereoTrackPairs_WithExactSizes() {
    var blob = BuildMultiSongKssx(songCount: 2);
    using var ms = new MemoryStream(blob);
    var entries = new KssFormatDescriptor().List(ms, null);

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
  public void Kssx_MultiSong_TracksRenderExactBytes_AndSongsDiffer() {
    var blob = BuildMultiSongKssx(songCount: 2);
    using var ms = new MemoryStream(blob);
    var declared = new KssFormatDescriptor().List(ms, null)
      .First(e => e.Name == "TRACK_01_LEFT.wav").OriginalSize;

    var t1 = Bytes(blob, "TRACK_01_LEFT.wav");
    var t2 = Bytes(blob, "TRACK_02_LEFT.wav");

    Assert.That(Encoding.ASCII.GetString(t1, 0, 4), Is.EqualTo("RIFF"));
    Assert.That(t1.Length, Is.EqualTo(declared), "declared size must equal produced bytes exactly");
    Assert.That(t2, Is.Not.EqualTo(t1), "song 2 must render a distinct tone from song 1");
  }
}
