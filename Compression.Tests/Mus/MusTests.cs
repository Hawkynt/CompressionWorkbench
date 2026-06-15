#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Mus;

namespace Compression.Tests.Mus;

[TestFixture]
public class MusTests {

  // Builds a MUS file with the given instrument table and raw event bytes.
  private static byte[] BuildMus(ushort[] instruments, byte[] events) {
    using var ms = new MemoryStream();
    ms.Write("MUS"u8);
    ms.WriteByte(0x1A);
    var u16 = new byte[2];

    void W16(int v) { BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)v); ms.Write(u16); }

    var scoreStart = 16 + instruments.Length * 2;
    W16(events.Length);          // scoreLen
    W16(scoreStart);             // scoreStart
    W16(1);                      // primaryChannels
    W16(0);                      // secondaryChannels
    W16(instruments.Length);     // numInstruments
    W16(0);                      // pad
    foreach (var instr in instruments) W16(instr);
    ms.Write(events);
    return ms.ToArray();
  }

  private static byte[] Convert(byte[] mus) {
    using var ms = new MemoryStream(mus);
    using var output = new MemoryStream();
    new MusFormatDescriptor().ExtractEntry(ms, "converted.mid", output, null);
    return output.ToArray();
  }

  // The fixed tempo meta-event prepended to every converted track.
  private static byte[] TempoMeta() {
    var t = MusToMidiConverter.TempoMicrosPerQuarter;
    return [0x00, 0xFF, 0x51, 0x03, (byte)(t >> 16), (byte)(t >> 8), (byte)t];
  }

  // ──────────────────────────────────────────────────────────────────────────

  [Test]
  public void Convert_PlayNoteWithVolume_ProducesNoteOn() {
    // Play note (type 1) on MUS channel 0, last-in-group; note 60 with volume byte 100.
    // descriptor = 0x80 | (1<<4) | 0 = 0x90; note = 0x80 | 60 = 0xBC; volume = 100.
    // Then a delay varint of 35, then score-end (type 6) = 0x60.
    var events = new byte[] { 0x90, 0xBC, 100, 35, 0x60 };
    var midi = Convert(BuildMus([], events));

    // MThd, format 0, division 70.
    Assert.That(midi.AsSpan(0, 4).ToArray(), Is.EqualTo("MThd"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(midi.AsSpan(8)), Is.EqualTo(0));
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(midi.AsSpan(12)), Is.EqualTo((ushort)MusToMidiConverter.Division));

    // MTrk at offset 14.
    Assert.That(midi.AsSpan(14, 4).ToArray(), Is.EqualTo("MTrk"u8.ToArray()));
    var trackLen = (int)BinaryPrimitives.ReadUInt32BigEndian(midi.AsSpan(18));
    var body = midi.AsSpan(22, trackLen).ToArray();

    // Expected: tempo meta, then delta 0 + note-on (0x90 60 100), delta 35 + EOT.
    var expected = new List<byte>();
    expected.AddRange(TempoMeta());
    expected.AddRange([0x00, 0x90, 60, 100]);
    expected.AddRange([35, 0xFF, 0x2F, 0x00]);
    Assert.That(body, Is.EqualTo(expected.ToArray()));
  }

  [Test]
  public void Convert_PercussionChannel15_MapsToMidiChannel9() {
    // Play note on MUS channel 15 (percussion) without volume byte, not last-in-group.
    // descriptor = (1<<4) | 15 = 0x1F; note = 38 (no volume flag); then score-end.
    var events = new byte[] { 0x1F, 38, 0x6F };  // then score-end on channel 15
    var midi = Convert(BuildMus([], events));
    var trackLen = (int)BinaryPrimitives.ReadUInt32BigEndian(midi.AsSpan(18));
    var body = midi.AsSpan(22, trackLen).ToArray();

    var expected = new List<byte>();
    expected.AddRange(TempoMeta());
    expected.AddRange([0x00, 0x99, 38, 127]);    // channel 9, default volume 127
    expected.AddRange([0x00, 0xFF, 0x2F, 0x00]);
    Assert.That(body, Is.EqualTo(expected.ToArray()));
  }

  [Test]
  public void Convert_ControllerIndexZero_BecomesProgramChange() {
    // Controller change (type 4) channel 0, last-in-group; index 0 (program), value 5.
    // descriptor = 0x80 | (4<<4) | 0 = 0xC0; ctrlIndex = 0; value = 5; delay 0; score-end.
    var events = new byte[] { 0xC0, 0, 5, 0, 0x60 };
    var midi = Convert(BuildMus([], events));
    var trackLen = (int)BinaryPrimitives.ReadUInt32BigEndian(midi.AsSpan(18));
    var body = midi.AsSpan(22, trackLen).ToArray();

    var expected = new List<byte>();
    expected.AddRange(TempoMeta());
    expected.AddRange([0x00, 0xC0, 5]);          // program change to program 5
    expected.AddRange([0x00, 0xFF, 0x2F, 0x00]);
    Assert.That(body, Is.EqualTo(expected.ToArray()));
  }

  [Test]
  public void Convert_VolumeController_MapsToCc7() {
    // Controller (type 4), index 3 (volume) → CC 7, value 90.
    var events = new byte[] { 0xC0, 3, 90, 0, 0x60 };
    var midi = Convert(BuildMus([], events));
    var trackLen = (int)BinaryPrimitives.ReadUInt32BigEndian(midi.AsSpan(18));
    var body = midi.AsSpan(22, trackLen).ToArray();

    var expected = new List<byte>();
    expected.AddRange(TempoMeta());
    expected.AddRange([0x00, 0xB0, 7, 90]);
    expected.AddRange([0x00, 0xFF, 0x2F, 0x00]);
    Assert.That(body, Is.EqualTo(expected.ToArray()));
  }

  [Test]
  public void Metadata_ReportsChannelsAndInstruments() {
    var mus = BuildMus([24, 30], new byte[] { 0x60 });
    using var ms = new MemoryStream(mus);
    using var output = new MemoryStream();
    new MusFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var ini = Encoding.UTF8.GetString(output.ToArray());

    Assert.That(ini, Does.Contain("primary_channels=1"));
    Assert.That(ini, Does.Contain("instruments=2"));
    Assert.That(ini, Does.Contain("instrument_patches=24,30"));
  }

  [Test]
  public void List_SurfacesFullAndConverted() {
    var mus = BuildMus([], new byte[] { 0x60 });
    using var ms = new MemoryStream(mus);
    var entries = new MusFormatDescriptor().List(ms, null);
    Assert.That(entries.First(e => e.Name == "FULL.mus").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "converted.mid").Kind, Is.EqualTo("Track"));
  }

  [Test]
  public void GarbageInput_DegradesToFullOnly() {
    using var ms = new MemoryStream(new byte[] { 0x00, 0x01, 0x02, 0x03 });
    var entries = new MusFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.mus"));
  }
}
