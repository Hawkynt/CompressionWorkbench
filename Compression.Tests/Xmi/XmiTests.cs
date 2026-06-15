#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Xmi;

namespace Compression.Tests.Xmi;

[TestFixture]
public class XmiTests {

  // ── IFF assembly helpers ─────────────────────────────────────────────────

  private static byte[] Chunk(string id, byte[] body) {
    using var ms = new MemoryStream();
    ms.Write(Encoding.ASCII.GetBytes(id));
    var len = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
    ms.Write(len);
    ms.Write(body);
    if ((body.Length & 1) != 0) ms.WriteByte(0); // IFF even padding
    return ms.ToArray();
  }

  private static byte[] Form(string formType, byte[] body) {
    using var inner = new MemoryStream();
    inner.Write(Encoding.ASCII.GetBytes(formType));
    inner.Write(body);
    return Chunk("FORM", inner.ToArray());
  }

  private static byte[] Cat(string catType, byte[] body) {
    using var inner = new MemoryStream();
    inner.Write(Encoding.ASCII.GetBytes(catType));
    inner.Write(body);
    return Chunk("CAT ", inner.ToArray());
  }

  // Builds a single-song XMI: one note-on (note 60, vel 100, duration 10) plus a
  // program-change controller, with a 10-tick delay so the note-off lands at tick 10.
  private static byte[] BuildXmi() {
    // TIMB: u16 count=1, then (patch=5, bank=0).
    var timb = new byte[] { 0x01, 0x00, 0x05, 0x00 };

    // EVNT: program change (0xC0 05), note-on (0x90 60 100 dur=10), delay 10.
    var evnt = new byte[] {
      0xC0, 0x05,                 // program change
      0x90, 60, 100, 10,          // note-on note=60 vel=100 duration=10
      10,                          // delay 10 ticks → flush note-off
    };

    var song = Form("XMID", Concat(Chunk("TIMB", timb), Chunk("EVNT", evnt)));
    var cat = Cat("XMID", song);

    var xdir = Form("XDIR", Chunk("INFO", new byte[] { 0x01, 0x00 })); // numSongs=1
    return Concat(xdir, cat);
  }

  private static byte[] Concat(params byte[][] parts) {
    using var ms = new MemoryStream();
    foreach (var p in parts) ms.Write(p);
    return ms.ToArray();
  }

  private static byte[] ExtractSong(byte[] xmi, string name) {
    using var ms = new MemoryStream(xmi);
    using var output = new MemoryStream();
    new XmiFormatDescriptor().ExtractEntry(ms, name, output, null);
    return output.ToArray();
  }

  // ──────────────────────────────────────────────────────────────────────────

  [Test]
  public void List_SurfacesFullSongAndMetadata() {
    using var ms = new MemoryStream(BuildXmi());
    var entries = new XmiFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.xmi").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "songs/00.mid").Kind, Is.EqualTo("Track"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Metadata_ReportsSongCountAndTimbres() {
    using var ms = new MemoryStream(BuildXmi());
    using var output = new MemoryStream();
    new XmiFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var ini = Encoding.UTF8.GetString(output.ToArray());

    Assert.That(ini, Does.Contain("songs=1"));
    Assert.That(ini, Does.Contain("song00_timbres=5"));
  }

  [Test]
  public void ConvertedSong_PairsNoteOnWithScheduledNoteOff() {
    var midi = ExtractSong(BuildXmi(), "songs/00.mid");

    Assert.That(midi.AsSpan(0, 4).ToArray(), Is.EqualTo("MThd"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(midi.AsSpan(8)), Is.EqualTo(0));
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(midi.AsSpan(12)), Is.EqualTo((ushort)XmiToMidiConverter.Division));

    var trackLen = (int)BinaryPrimitives.ReadUInt32BigEndian(midi.AsSpan(18));
    var body = midi.AsSpan(22, trackLen).ToArray();

    var t = XmiToMidiConverter.TempoMicrosPerQuarter;
    var expected = new List<byte>();
    expected.AddRange([0x00, 0xFF, 0x51, 0x03, (byte)(t >> 16), (byte)(t >> 8), (byte)t]);
    expected.AddRange([0x00, 0xC0, 0x05]);          // program change at tick 0
    expected.AddRange([0x00, 0x90, 60, 100]);       // note-on at tick 0
    expected.AddRange([10, 0x80, 60, 0x40]);        // note-off at tick 10
    expected.AddRange([0x00, 0xFF, 0x2F, 0x00]);    // end-of-track
    Assert.That(body, Is.EqualTo(expected.ToArray()));
  }

  [Test]
  public void GarbageInput_DegradesToFullOnly() {
    using var ms = new MemoryStream(new byte[] { (byte)'F', (byte)'O', (byte)'R', (byte)'M', 0, 0, 0, 0, (byte)'X', (byte)'Y', (byte)'Z', (byte)'W' });
    var entries = new XmiFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.xmi"));
  }
}
