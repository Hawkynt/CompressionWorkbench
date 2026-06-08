#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Nsf;

namespace Compression.Tests.Nsf;

[TestFixture]
public class NsfTests {

  private static byte[] BuildNesm(byte chipFlags = 0x01) {
    var blob = new byte[0x80 + 4];
    "NESM\x1A"u8.CopyTo(blob);
    blob[0x05] = 1;    // version
    blob[0x06] = 3;    // total songs
    blob[0x07] = 1;    // start song
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x08), 0x8000); // load
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0A), 0x8003); // init
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0C), 0x8006); // play
    WriteText(blob, 0x0E, "Mega Tune", 32);
    WriteText(blob, 0x2E, "Composer X", 32);
    WriteText(blob, 0x4E, "2024 Label", 32);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x6E), 16639); // ntsc speed
    blob[0x7A] = 0x00; // NTSC
    blob[0x7B] = chipFlags;
    // 4-byte program after header.
    blob[0x80] = 0xAA; blob[0x81] = 0xBB; blob[0x82] = 0xCC; blob[0x83] = 0xDD;
    return blob;
  }

  private static byte[] BuildNsfe() {
    var ms = new MemoryStream();
    ms.Write("NSFE"u8);
    // INFO chunk: load,init,play (u16), region u8, chips u8, totalSongs u8, startSong u8.
    var info = new byte[10];
    BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(0), 0x8000);
    BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(2), 0x8003);
    BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(4), 0x8006);
    info[6] = 0x00;       // NTSC
    info[7] = 0x04;       // FDS
    info[8] = 2;          // total songs
    info[9] = 0;          // start song
    WriteChunk(ms, "INFO", info);
    var data = new byte[] { 0x01, 0x02, 0x03 };
    WriteChunk(ms, "DATA", data);
    // auth: title, artist, copyright, ripper (NUL-separated)
    var auth = Encoding.UTF8.GetBytes("Big Song\0Artist Q\0Copy 2024\0Ripper R\0");
    WriteChunk(ms, "auth", auth);
    WriteChunk(ms, "time", [0x10, 0x20, 0x30, 0x40]);
    WriteChunk(ms, "NEND", []);
    return ms.ToArray();
  }

  private static void WriteChunk(Stream s, string id, byte[] data) {
    Span<byte> size = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
    s.Write(size);
    s.Write(Encoding.ASCII.GetBytes(id));
    s.Write(data);
  }

  private static void WriteText(byte[] b, int off, string t, int len) {
    var bytes = Encoding.ASCII.GetBytes(t);
    Array.Copy(bytes, 0, b, off, Math.Min(bytes.Length, len - 1));
  }

  private static string Meta(byte[] blob) => Encoding.UTF8.GetString(Bytes(blob, "metadata.ini"));

  private static byte[] Bytes(byte[] blob, string entry) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new NsfFormatDescriptor().ExtractEntry(ms, entry, output, null);
    return output.ToArray();
  }

  [Test]
  public void Nesm_SurfacesFullProgramAndMetadata() {
    using var ms = new MemoryStream(BuildNesm());
    var entries = new NsfFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.nsf").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    Assert.That(entries.First(e => e.Name == "program.bin").Kind, Is.EqualTo("Stream"));
    Assert.That(Bytes(BuildNesm(), "program.bin"), Is.EqualTo(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }));
  }

  [Test]
  public void Nesm_MetadataHasHeaderFieldsAndChipName() {
    var ini = Meta(BuildNesm(chipFlags: 0x01)); // VRC6
    Assert.That(ini, Does.Contain("name=Mega Tune"));
    Assert.That(ini, Does.Contain("artist=Composer X"));
    Assert.That(ini, Does.Contain("copyright=2024 Label"));
    Assert.That(ini, Does.Contain("total_songs=3"));
    Assert.That(ini, Does.Contain("load_addr=0x8000"));
    Assert.That(ini, Does.Contain("expansion_chips=VRC6"));
    Assert.That(ini, Does.Contain("region=NTSC"));
  }

  [Test]
  public void Nesm_MultipleChipFlagsAreNamed() {
    var ini = Meta(BuildNesm(chipFlags: 0x01 | 0x04 | 0x20)); // VRC6 + FDS + S5B
    Assert.That(ini, Does.Contain("VRC6"));
    Assert.That(ini, Does.Contain("FDS"));
    Assert.That(ini, Does.Contain("S5B"));
  }

  [Test]
  public void Nsfe_SurfacesDataAuthAndOtherChunks() {
    var blob = BuildNsfe();
    using var ms = new MemoryStream(blob);
    var entries = new NsfFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.nsfe").Kind, Is.EqualTo("Container"));
    Assert.That(Bytes(blob, "program.bin"), Is.EqualTo(new byte[] { 0x01, 0x02, 0x03 }));
    // "time" chunk surfaced verbatim.
    Assert.That(Bytes(blob, "metadata/time.bin"), Is.EqualTo(new byte[] { 0x10, 0x20, 0x30, 0x40 }));
  }

  [Test]
  public void Nsfe_AuthFieldsAndChipsParse() {
    var ini = Meta(BuildNsfe());
    Assert.That(ini, Does.Contain("variant=NSFE"));
    Assert.That(ini, Does.Contain("name=Big Song"));
    Assert.That(ini, Does.Contain("artist=Artist Q"));
    Assert.That(ini, Does.Contain("copyright=Copy 2024"));
    Assert.That(ini, Does.Contain("ripper=Ripper R"));
    Assert.That(ini, Does.Contain("expansion_chips=FDS"));
  }

  // A NESM with a real, terminating init/play program (base 2A03): init enables pulse 1 and
  // sets a tone then RTS; play is a bare RTS. Loaded at $8000, init $8000, play $8100.
  private static byte[] BuildPlayableNesm() {
    var p = new List<byte>();
    void Sta(byte lo, byte hi, byte value) {
      p.Add(0xA9); p.Add(value);          // LDA #value
      p.Add(0x8D); p.Add(lo); p.Add(hi);  // STA $hilo
    }
    Sta(0x15, 0x40, 0x01);                // $4015 enable pulse 1
    Sta(0x00, 0x40, 0xBF);                // $4000 duty 50% + const vol + halt
    Sta(0x02, 0x40, 0xFD);                // $4002 timer low
    Sta(0x03, 0x40, 0x18);                // $4003 timer high + length load
    p.Add(0x60);                          // RTS
    while (p.Count < 0x100) p.Add(0xEA);  // pad to $8100
    p.Add(0x60);                          // play RTS
    var program = p.ToArray();

    var blob = new byte[0x80 + program.Length];
    "NESM\x1A"u8.CopyTo(blob);
    blob[0x05] = 1;
    blob[0x06] = 1;
    blob[0x07] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x08), 0x8000);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0A), 0x8000);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0C), 0x8100);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x6E), 16639); // NTSC speed
    blob[0x7A] = 0x00; // NTSC
    blob[0x7B] = 0x00; // no expansion chips
    program.CopyTo(blob, 0x80);
    return blob;
  }

  [Test]
  public void Nesm_BaseChip_RendersMonoWav() {
    var blob = BuildPlayableNesm();
    using var ms = new MemoryStream(blob);
    var entries = new NsfFormatDescriptor().List(ms, null);

    var mono = entries.FirstOrDefault(e => e.Name == "MONO.wav");
    Assert.That(mono, Is.Not.Null, "base 2A03 tune should render a MONO.wav");
    Assert.That(mono!.Kind, Is.EqualTo("Channel"));

    var wav = Bytes(blob, "MONO.wav");
    Assert.That(wav.Length, Is.GreaterThan(44), "WAV should carry audio data");
    Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));

    var ini = Meta(blob);
    Assert.That(ini, Does.Contain("rendered_sample_rate=44100"));
    Assert.That(ini, Does.Contain("rendered_region=NTSC"));
  }

  [Test]
  public void Nesm_ExpansionChip_NoMonoWav() {
    // A tune declaring an expansion chip cannot be rendered; it degrades to header/program.
    using var ms = new MemoryStream(BuildNesm(chipFlags: 0x01)); // VRC6
    var entries = new NsfFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.False);
    Assert.That(Meta(BuildNesm(chipFlags: 0x01)), Does.Not.Contain("rendered_sample_rate"));
  }

  [Test]
  public void ShortBlob_DegradesToFullOnly() {
    using var ms = new MemoryStream("NESM\x1A"u8.ToArray());
    var entries = new NsfFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.nsf"), Is.True);
    Assert.That(entries.Any(e => e.Name == "program.bin"), Is.False);
  }

  // ── multi-song subtune surfacing ──────────────────────────────────────────

  // A 3-song playable NESM. Init enables pulse 1 then stores the song-derived A register as the
  // pulse timer-low byte, so each subtune produces a distinct tone (audio differs per song). Play
  // is a bare RTS. Loaded at $8000, init $8000, play $8100.
  private static byte[] BuildMultiSongNesm(byte songs = 3) {
    var program = BuildMultiSongProgram();

    var blob = new byte[0x80 + program.Length];
    "NESM\x1A"u8.CopyTo(blob);
    blob[0x05] = 1;
    blob[0x06] = songs;       // total songs
    blob[0x07] = 1;           // start song (1-based)
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x08), 0x8000);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0A), 0x8000);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0C), 0x8100);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x6E), 16639); // NTSC speed
    blob[0x7A] = 0x00; // NTSC
    blob[0x7B] = 0x00; // no expansion chips
    program.CopyTo(blob, 0x80);
    return blob;
  }

  // Init: A = (song-1) on entry. Stash it, program a fixed enable/duty, then use the stashed song
  // value (scaled) as the pulse-1 timer-low byte so the rendered tone differs per subtune.
  private static byte[] BuildMultiSongProgram() {
    var p = new List<byte> {
      0x85, 0x10,                 // STA $10  (stash song-1 in zero page)
      0xA9, 0x01, 0x8D, 0x15, 0x40, // LDA #$01 ; STA $4015  enable pulse 1
      0xA9, 0xBF, 0x8D, 0x00, 0x40, // LDA #$BF ; STA $4000  duty/const-vol/halt
      0xA5, 0x10,                 // LDA $10  (reload song-1)
      0x0A, 0x0A, 0x0A, 0x0A,     // ASL x4   (song-1 << 4 → clearly distinct per song)
      0x09, 0x40,                 // ORA #$40 (keep a non-zero base so song 0 still toned)
      0x8D, 0x02, 0x40,           // STA $4002 timer low (song-dependent)
      0xA9, 0x18, 0x8D, 0x03, 0x40, // LDA #$18 ; STA $4003 timer high + length load
      0x60,                       // RTS
    };
    while (p.Count < 0x100) p.Add(0xEA);  // pad to $8100
    p.Add(0x60);                          // play RTS
    return p.ToArray();
  }

  [Test]
  public void Nesm_MultiSong_SurfacesOneTrackPerSong_WithExactSizes() {
    var blob = BuildMultiSongNesm(songs: 3);
    using var ms = new MemoryStream(blob);
    var entries = new NsfFormatDescriptor().List(ms, null);

    var tracks = entries.Where(e => e.Kind == "Track").ToList();
    Assert.That(tracks.Select(t => t.Name),
      Is.EqualTo(new[] { "TRACK_01.wav", "TRACK_02.wav", "TRACK_03.wav" }));

    // Listing reports the exact deterministic WAV byte size with no rendering.
    var expected = 44L + 30L * 44100L * 2L; // 30 s mono 16-bit
    Assert.That(entries.First(e => e.Name == "TRACK_01.wav").OriginalSize, Is.EqualTo(expected));

    Assert.That(Meta(blob), Does.Contain("total_tracks=3"));
  }

  [Test]
  public void Nesm_MultiSong_DeclaredSizeMatchesRenderedBytes_AndSongsDiffer() {
    var blob = BuildMultiSongNesm(songs: 3);
    using var ms = new MemoryStream(blob);
    var entries = new NsfFormatDescriptor().List(ms, null);

    var declared = entries.First(e => e.Name == "TRACK_01.wav").OriginalSize;

    var t1 = Bytes(blob, "TRACK_01.wav");
    var t2 = Bytes(blob, "TRACK_02.wav");

    Assert.That(Encoding.ASCII.GetString(t1, 0, 4), Is.EqualTo("RIFF"));
    Assert.That(t1.Length, Is.EqualTo(declared), "declared size must equal produced bytes exactly");
    Assert.That(t2.Length, Is.EqualTo(declared));
    Assert.That(t2, Is.Not.EqualTo(t1), "song 2 must render a distinct tone from song 1");
  }

  // ── NSFE rendering (INFO + DATA + time + tlbl) ───────────────────────────

  private static byte[] BuildRenderableNsfe() {
    var program = BuildMultiSongProgram();
    var ms = new MemoryStream();
    ms.Write("NSFE"u8);

    var info = new byte[10];
    BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(0), 0x8000); // load
    BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(2), 0x8000); // init
    BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(4), 0x8100); // play
    info[6] = 0x00; // NTSC
    info[7] = 0x00; // no expansion chips
    info[8] = 2;    // total songs
    info[9] = 0;    // start song (0-based)
    WriteChunk(ms, "INFO", info);
    WriteChunk(ms, "DATA", program);

    // time: per-song durations in ms (1000 ms each → 1 s renders keep the test quick).
    var time = new byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(time.AsSpan(0), 1000);
    BinaryPrimitives.WriteInt32LittleEndian(time.AsSpan(4), 1000);
    WriteChunk(ms, "time", time);

    // tlbl: per-song labels (NUL-separated).
    WriteChunk(ms, "tlbl", Encoding.UTF8.GetBytes("Intro\0Boss Theme\0"));
    WriteChunk(ms, "NEND", []);
    return ms.ToArray();
  }

  [Test]
  public void Nsfe_RendersLabeledTracks_WithPerSongDurations() {
    var blob = BuildRenderableNsfe();
    using var ms = new MemoryStream(blob);
    var entries = new NsfFormatDescriptor().List(ms, null);

    var tracks = entries.Where(e => e.Kind == "Track").ToList();
    Assert.That(tracks, Has.Count.EqualTo(2));
    Assert.That(tracks[0].Name, Is.EqualTo("TRACK_01 Intro.wav"));
    Assert.That(tracks[1].Name, Is.EqualTo("TRACK_02 Boss Theme.wav"));

    // time chunk → 1 s per track: 44 + 1*44100*2 bytes.
    var expected = 44L + 1L * 44100L * 2L;
    Assert.That(tracks[0].OriginalSize, Is.EqualTo(expected));

    Assert.That(Meta(blob), Does.Contain("total_tracks=2"));

    var wav = Bytes(blob, "TRACK_01 Intro.wav");
    Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
    Assert.That(wav.Length, Is.EqualTo(expected), "rendered bytes must match the time-derived size");
  }
}
