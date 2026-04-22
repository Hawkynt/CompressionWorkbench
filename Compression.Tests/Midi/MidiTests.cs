#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Midi;
using FileFormat.Midi;

namespace Compression.Tests.Midi;

[TestFixture]
public class MidiTests {

  // Build a minimal valid 2-track SMF format-1 file with track names + tempo meta.
  private static byte[] MakeSampleMidi() {
    using var ms = new MemoryStream();

    // MThd: format=1, ntrks=2, division=96
    ms.Write("MThd"u8);
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(buf, 6);
    ms.Write(buf);
    Span<byte> hdr = stackalloc byte[6];
    BinaryPrimitives.WriteInt16BigEndian(hdr[0..], 1);     // format
    BinaryPrimitives.WriteInt16BigEndian(hdr[2..], 2);     // ntrks
    BinaryPrimitives.WriteInt16BigEndian(hdr[4..], 96);    // division
    ms.Write(hdr);

    // Track 0: name "Conductor", tempo 500000 µs/quarter (120 BPM), end-of-track
    WriteTrack(ms, BuildConductorTrack());

    // Track 1: name "Bass", end-of-track
    WriteTrack(ms, BuildBassTrack());

    return ms.ToArray();
  }

  private static byte[] BuildConductorTrack() {
    using var ms = new MemoryStream();
    // delta=0 + 0xFF 0x03 + len "Conductor"
    ms.WriteByte(0x00);
    ms.WriteByte(0xFF); ms.WriteByte(0x03);
    var name = "Conductor"u8.ToArray();
    ms.WriteByte((byte)name.Length);
    ms.Write(name);
    // delta=0 + tempo 500000 µs/quarter
    ms.WriteByte(0x00);
    ms.WriteByte(0xFF); ms.WriteByte(0x51); ms.WriteByte(0x03);
    ms.WriteByte(0x07); ms.WriteByte(0xA1); ms.WriteByte(0x20);   // 500000
    // delta=0 + end-of-track
    ms.WriteByte(0x00);
    ms.WriteByte(0xFF); ms.WriteByte(0x2F); ms.WriteByte(0x00);
    return ms.ToArray();
  }

  private static byte[] BuildBassTrack() {
    using var ms = new MemoryStream();
    ms.WriteByte(0x00);
    ms.WriteByte(0xFF); ms.WriteByte(0x03);
    var name = "Bass"u8.ToArray();
    ms.WriteByte((byte)name.Length);
    ms.Write(name);
    ms.WriteByte(0x00);
    ms.WriteByte(0xFF); ms.WriteByte(0x2F); ms.WriteByte(0x00);
    return ms.ToArray();
  }

  private static void WriteTrack(Stream outer, byte[] body) {
    outer.Write("MTrk"u8);
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(buf, body.Length);
    outer.Write(buf);
    outer.Write(body);
  }

  [Test]
  public void HeaderIsParsed() {
    var midi = MakeSampleMidi();
    var codec = new MidiCodec();
    var hdr = codec.ReadHeader(midi);
    Assert.That(hdr.Format, Is.EqualTo(1));
    Assert.That(hdr.NumTracks, Is.EqualTo(2));
    Assert.That(hdr.Division, Is.EqualTo(96));
  }

  [Test]
  public void TracksAreDetected() {
    var midi = MakeSampleMidi();
    var codec = new MidiCodec();
    var tracks = codec.FindTracks(midi);
    Assert.That(tracks, Has.Count.EqualTo(2));
  }

  [Test]
  public void MetaEventsAreExtracted() {
    var midi = MakeSampleMidi();
    var codec = new MidiCodec();
    var tracks = codec.FindTracks(midi);
    var events = codec.ParseMetaEvents(midi, tracks[0]);
    Assert.That(events.Any(e => e.Type == 0x03 && Encoding.UTF8.GetString(e.Data) == "Conductor"), Is.True);
    Assert.That(events.Any(e => e.Type == 0x51), Is.True);  // tempo
  }

  [Test]
  public void DescriptorListContainsPerTrackFiles() {
    var midi = MakeSampleMidi();
    using var ms = new MemoryStream(midi);
    var entries = new MidiFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.mid"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("track_00_")), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("track_01_")), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void PerTrackFileIsValidFormat0() {
    var midi = MakeSampleMidi();
    using var ms = new MemoryStream(midi);
    using var out0 = new MemoryStream();
    new MidiFormatDescriptor().ExtractEntry(ms, "track_00_Conductor.mid", out0, null);
    var track0 = out0.ToArray();
    // It should have MThd header + format=0 + ntrks=1
    Assert.That(track0[0], Is.EqualTo((byte)'M'));
    var format = BinaryPrimitives.ReadInt16BigEndian(track0.AsSpan(8));
    var ntrks = BinaryPrimitives.ReadInt16BigEndian(track0.AsSpan(10));
    Assert.That(format, Is.EqualTo(0));
    Assert.That(ntrks, Is.EqualTo(1));
  }

  [Test]
  public void MetadataIniHasTempo() {
    var midi = MakeSampleMidi();
    using var ms = new MemoryStream(midi);
    using var iniOut = new MemoryStream();
    new MidiFormatDescriptor().ExtractEntry(ms, "metadata.ini", iniOut, null);
    var text = Encoding.UTF8.GetString(iniOut.ToArray());
    Assert.That(text, Does.Contain("tempo_bpm="));
    Assert.That(text, Does.Contain("tracks=2"));
  }
}
