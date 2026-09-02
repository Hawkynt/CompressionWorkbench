#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Sid;

namespace Compression.Tests.Sid;

[TestFixture]
[Category("Slow")]
public class SidTests {

  // Builds a PSID/RSID header (big-endian). v2 adds flags + page bytes (header = 0x7C).
  private static byte[] BuildSid(string magic, ushort version, ushort flags = 0, ushort loadAddr = 0x1000, byte[]? program = null) {
    var headerSize = version >= 2 ? 0x7C : 0x76;
    program ??= [0x55, 0x66, 0x77];
    var blob = new byte[headerSize + program.Length];
    Encoding.ASCII.GetBytes(magic).CopyTo(blob, 0);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x04), version);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x06), (ushort)headerSize); // dataOffset
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x08), loadAddr);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0A), 0x1003); // init
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0C), 0x1006); // play
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0E), 1);      // songs
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x10), 1);      // start song
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(0x12), 0);      // speed
    WriteText(blob, 0x16, "Commando", 32);
    WriteText(blob, 0x36, "Rob Hubbard", 32);
    WriteText(blob, 0x56, "1985 Elite", 32);
    if (version >= 2)
      BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x76), flags);
    program.CopyTo(blob, headerSize);
    return blob;
  }

  private static void WriteText(byte[] b, int off, string t, int len) {
    var bytes = Encoding.ASCII.GetBytes(t);
    Array.Copy(bytes, 0, b, off, Math.Min(bytes.Length, len - 1));
  }

  private static string Meta(byte[] blob) => Encoding.UTF8.GetString(Bytes(blob, "metadata.ini"));

  private static byte[] Bytes(byte[] blob, string entry) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new SidFormatDescriptor().ExtractEntry(ms, entry, output, null);
    return output.ToArray();
  }

  [Test]
  public void Psid_SurfacesFullMetadataAndProgram() {
    var blob = BuildSid("PSID", 1);
    using var ms = new MemoryStream(blob);
    var entries = new SidFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.sid").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "program.bin").Kind, Is.EqualTo("Stream"));
    Assert.That(Bytes(blob, "program.bin"), Is.EqualTo(new byte[] { 0x55, 0x66, 0x77 }));
  }

  [Test]
  public void Psid_MetadataFields() {
    var ini = Meta(BuildSid("PSID", 1));
    Assert.That(ini, Does.Contain("magic=PSID"));
    Assert.That(ini, Does.Contain("name=Commando"));
    Assert.That(ini, Does.Contain("author=Rob Hubbard"));
    Assert.That(ini, Does.Contain("released=1985 Elite"));
    Assert.That(ini, Does.Contain("load_addr=0x1000"));
  }

  [Test]
  public void Rsid_MagicRecognised() {
    var ini = Meta(BuildSid("RSID", 2, flags: 0x0024)); // clock=PAL(1<<2=0x04 → bits2-3=1), model=8580(2<<4=0x20)
    Assert.That(ini, Does.Contain("magic=RSID"));
    Assert.That(ini, Does.Contain("clock=PAL"));
    Assert.That(ini, Does.Contain("sid_model=MOS8580"));
  }

  [Test]
  public void LoadAddrZero_ReadsRealLoadFromProgram() {
    // loadAddr==0 → first two LE program bytes are the real load address (0x2000).
    var program = new byte[] { 0x00, 0x20, 0xAB, 0xCD };
    var ini = Meta(BuildSid("PSID", 2, loadAddr: 0, program: program));
    Assert.That(ini, Does.Contain("real_load_addr=0x2000"));
  }

  [Test]
  public void ShortBlob_DegradesToFullOnly() {
    using var ms = new MemoryStream("PSID"u8.ToArray());
    var entries = new SidFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.sid"), Is.True);
    Assert.That(entries.Any(e => e.Name == "program.bin"), Is.False);
  }

  // Builds a renderable PSID v2: init at load address sets up voice 1 (saw + gate), play = RTS.
  private static byte[] BuildRenderablePsid(string magic, ushort flags) {
    var program = new List<byte> { 0x00, 0x10 }; // loadAddr==0 → embedded LE load addr $1000
    void Set(byte reg, byte value) { program.AddRange([0xA9, value, 0x8D, reg, 0xD4]); }
    Set(0x00, 0x80); Set(0x01, 0x08); // some audible frequency
    Set(0x06, 0xF0); Set(0x18, 0x0F); Set(0x04, 0x21);
    program.Add(0x60);                // init RTS
    while (program.Count < 2 + 0x40) program.Add(0xEA);
    program.Add(0x60);                // play RTS at $1040

    const int header = 0x7C;
    var blob = new byte[header + program.Count];
    Encoding.ASCII.GetBytes(magic).CopyTo(blob, 0);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x04), 2);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x06), header);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x08), 0);       // loadAddr 0 → embedded
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0A), 0x1000);  // init
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0C), 0x1040);  // play
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0E), 1);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x10), 1);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x76), flags);
    program.CopyTo(blob, header);
    return blob;
  }

  [Test]
  public void Psid_RendersMonoWav() {
    var blob = BuildRenderablePsid("PSID", 0x0014); // clock PAL, model 6581
    using var ms = new MemoryStream(blob);
    var entries = new SidFormatDescriptor().List(ms, null);

    var mono = entries.FirstOrDefault(e => e.Name == "MONO.wav");
    Assert.That(mono, Is.Not.Null, "PSID should surface a rendered MONO.wav");
    Assert.That(mono!.Kind, Is.EqualTo("Channel"));

    var wav = Bytes(blob, "MONO.wav");
    Assert.That(wav.Length, Is.GreaterThan(44 + 1000));         // header + audio
    Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));

    var ini = Meta(blob);
    Assert.That(ini, Does.Contain("rendered_model=MOS6581"));
    Assert.That(ini, Does.Contain("rendered_clock=PAL"));
  }

  [Test]
  public void Rsid_DegradesWithoutMonoWav() {
    var blob = BuildRenderablePsid("RSID", 0x0014);
    using var ms = new MemoryStream(blob);
    var entries = new SidFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.False, "RSID must not render");
    Assert.That(entries.Any(e => e.Name == "FULL.sid"), Is.True);
    Assert.That(entries.Any(e => e.Name == "program.bin"), Is.True);
  }

  // ── multi-song subtune surfacing ──────────────────────────────────────────

  // A renderable multi-song PSID v2. Init is called with A = (song-1); it stashes A, sets up
  // voice 1, then uses the stashed song value as the voice-1 frequency-low byte so each subtune
  // produces a distinct tone. Play is a bare RTS at $1040.
  private static byte[] BuildMultiSongPsid(ushort songs, ushort flags) {
    var program = new List<byte> { 0x00, 0x10 }; // loadAddr==0 → embedded LE load addr $1000
    void Set(byte reg, byte value) { program.AddRange([0xA9, value, 0x8D, reg, 0xD4]); }
    program.AddRange([0x85, 0x10]);   // STA $10  (stash song-1)
    Set(0x01, 0x08);                  // voice 1 freq hi
    Set(0x06, 0xF0);                  // sustain/release
    Set(0x18, 0x0F);                  // volume
    program.AddRange([0xA5, 0x10]);   // LDA $10  (reload song-1)
    program.AddRange([0x0A, 0x0A]);   // ASL x2  (song-1 << 2)
    program.AddRange([0x09, 0x20]);   // ORA #$20  base so song 0 is still toned
    program.AddRange([0x8D, 0x00, 0xD4]); // STA $D400  voice-1 freq lo (song-dependent)
    Set(0x04, 0x21);                  // gate + saw
    program.Add(0x60);                // init RTS
    while (program.Count < 2 + 0x40) program.Add(0xEA);
    program.Add(0x60);                // play RTS at $1040

    const int header = 0x7C;
    var blob = new byte[header + program.Count];
    Encoding.ASCII.GetBytes("PSID").CopyTo(blob, 0);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x04), 2);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x06), header);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x08), 0);       // loadAddr 0 → embedded
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0A), 0x1000);  // init
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0C), 0x1040);  // play
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0E), songs);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x10), 1);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x76), flags);
    program.CopyTo(blob, header);
    return blob;
  }

  [Test]
  public void MultiSong_SurfacesOneTrackPerSong_AndPreservesModelNaming() {
    // flags: clock PAL (bits2-3=1 → 0x04), SID #1 model 6581 (bits4-5=1 → 0x10).
    var blob = BuildMultiSongPsid(songs: 3, flags: 0x0014);
    using var ms = new MemoryStream(blob);
    var entries = new SidFormatDescriptor().List(ms, null);

    var tracks = entries.Where(e => e.Kind == "Track").Select(e => e.Name).ToList();
    Assert.That(tracks, Is.EqualTo(new[] { "TRACK_01.wav", "TRACK_02.wav", "TRACK_03.wav" }));

    var expected = 44L + 30L * 44100L * 2L; // 30 s mono 16-bit
    Assert.That(entries.First(e => e.Name == "TRACK_01.wav").OriginalSize, Is.EqualTo(expected));

    // Wave-10 model-matrix naming intact: a specified 6581 model yields the plain MONO.wav.
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);
    var ini = Meta(blob);
    Assert.That(ini, Does.Contain("sid_model=MOS6581"), "v2 model flag must still resolve");
    Assert.That(ini, Does.Contain("total_tracks=3"));
  }

  [Test]
  public void MultiSong_TracksRenderExactBytes_AndSongsDiffer() {
    var blob = BuildMultiSongPsid(songs: 3, flags: 0x0014);
    using var ms = new MemoryStream(blob);
    var declared = new SidFormatDescriptor().List(ms, null)
      .First(e => e.Name == "TRACK_01.wav").OriginalSize;

    var t1 = Bytes(blob, "TRACK_01.wav");
    var t2 = Bytes(blob, "TRACK_02.wav");

    Assert.That(Encoding.ASCII.GetString(t1, 0, 4), Is.EqualTo("RIFF"));
    Assert.That(t1.Length, Is.EqualTo(declared), "declared size must equal produced bytes exactly");
    Assert.That(t2.Length, Is.EqualTo(declared));
    Assert.That(t2, Is.Not.EqualTo(t1), "song 2 must render a distinct tone from song 1");
  }

  [Test]
  public void MultiSong_UnknownModel_TracksUse6581Only_WithoutDoubling() {
    // flags: clock PAL, SID #1 model 00 (unknown) → default render is dual; tracks fall to 6581.
    var blob = BuildMultiSongPsid(songs: 2, flags: 0x0004);
    using var ms = new MemoryStream(blob);
    var entries = new SidFormatDescriptor().List(ms, null);

    var tracks = entries.Where(e => e.Kind == "Track").Select(e => e.Name).ToList();
    Assert.That(tracks, Is.EqualTo(new[] { "TRACK_01.wav", "TRACK_02.wav" }),
      "unknown/dual model must NOT double the track list");

    var ini = Meta(blob);
    Assert.That(ini, Does.Contain("track_model=6581"));
    // Default dual render keeps the wave-10 _6581/_8580 suffixed channel WAVs.
    Assert.That(entries.Any(e => e.Name == "MONO_6581.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO_8580.wav"), Is.True);
  }
}
