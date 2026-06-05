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
}
