#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Cmf;

namespace Compression.Tests.Cmf;

[TestFixture]
public class CmfTests {

  // Builds a minimal CMF: header, three strings, two 16-byte OPL patches, and a
  // tiny music event stream (a note-on + note-off).
  private static byte[] BuildCmf() {
    const int headerSize = 0x28;   // 40 bytes covering all documented header fields
    const ushort ticksPerQuarter = 96;
    const ushort numInstruments = 2;

    var title = Encoding.ASCII.GetBytes("Demo Tune\0");
    var composer = Encoding.ASCII.GetBytes("J. Coder\0");
    var remarks = Encoding.ASCII.GetBytes("notes here\0");

    var titleOffset = headerSize;
    var composerOffset = titleOffset + title.Length;
    var remarksOffset = composerOffset + composer.Length;
    var instrumentOffset = remarksOffset + remarks.Length;
    var musicOffset = instrumentOffset + numInstruments * 16;

    var music = new byte[] { 0x00, 0x90, 60, 100, 0x60, 0x80, 60, 0x40 };
    var total = musicOffset + music.Length;
    var blob = new byte[total];

    "CTMF"u8.CopyTo(blob);
    WriteU16(blob, 0x04, 0x0101);                 // version 1.1
    WriteU16(blob, 0x06, (ushort)instrumentOffset);
    WriteU16(blob, 0x08, (ushort)musicOffset);
    WriteU16(blob, 0x0A, ticksPerQuarter);
    WriteU16(blob, 0x0C, 96);                      // ticks per second
    WriteU16(blob, 0x0E, (ushort)titleOffset);
    WriteU16(blob, 0x10, (ushort)composerOffset);
    WriteU16(blob, 0x12, (ushort)remarksOffset);
    WriteU16(blob, 0x24, numInstruments);
    WriteU16(blob, 0x26, 120);                     // basic tempo

    title.CopyTo(blob.AsSpan(titleOffset));
    composer.CopyTo(blob.AsSpan(composerOffset));
    remarks.CopyTo(blob.AsSpan(remarksOffset));

    // Two distinct OPL patches.
    for (var i = 0; i < 16; ++i) blob[instrumentOffset + i] = (byte)(0x10 + i);
    for (var i = 0; i < 16; ++i) blob[instrumentOffset + 16 + i] = (byte)(0xA0 + i);

    music.CopyTo(blob.AsSpan(musicOffset));
    return blob;
  }

  private static void WriteU16(byte[] blob, int offset, ushort value)
    => BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(offset, 2), value);

  private static byte[] Extract(byte[] blob, string name) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new CmfFormatDescriptor().ExtractEntry(ms, name, output, null);
    return output.ToArray();
  }

  // ──────────────────────────────────────────────────────────────────────────

  [Test]
  public void List_SurfacesFullMetadataInstrumentsAndMusic() {
    using var ms = new MemoryStream(BuildCmf());
    var entries = new CmfFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.cmf").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Count(e => e.Name.StartsWith("instruments/")), Is.EqualTo(2));
    Assert.That(entries.First(e => e.Name == "music.mid").Kind, Is.EqualTo("Track"));
  }

  [Test]
  public void Metadata_ExtractsStringsAndTimings() {
    var ini = Encoding.UTF8.GetString(Extract(BuildCmf(), "metadata.ini"));
    Assert.That(ini, Does.Contain("title=Demo Tune"));
    Assert.That(ini, Does.Contain("composer=J. Coder"));
    Assert.That(ini, Does.Contain("remarks=notes here"));
    Assert.That(ini, Does.Contain("ticks_per_quarter=96"));
    Assert.That(ini, Does.Contain("ticks_per_second=96"));
    Assert.That(ini, Does.Contain("basic_tempo=120"));
    Assert.That(ini, Does.Contain("instruments=2"));
  }

  [Test]
  public void Instrument_IsExactSixteenByteOplPatch() {
    var patch = Extract(BuildCmf(), "instruments/00.bin");
    Assert.That(patch.Length, Is.EqualTo(16));
    for (var i = 0; i < 16; ++i)
      Assert.That(patch[i], Is.EqualTo((byte)(0x10 + i)));
  }

  [Test]
  public void Music_IsWrappedAsValidSmfWithCorrectDivisionAndEndOfTrack() {
    var midi = Extract(BuildCmf(), "music.mid");

    Assert.That(midi.AsSpan(0, 4).ToArray(), Is.EqualTo("MThd"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(midi.AsSpan(8)), Is.EqualTo(0));   // format 0
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(midi.AsSpan(12)), Is.EqualTo(96)); // division

    Assert.That(midi.AsSpan(14, 4).ToArray(), Is.EqualTo("MTrk"u8.ToArray()));
    var trackLen = (int)BinaryPrimitives.ReadUInt32BigEndian(midi.AsSpan(18));
    var body = midi.AsSpan(22, trackLen).ToArray();

    // Raw music bytes preserved, end-of-track appended (input had none).
    var expected = new List<byte>(new byte[] { 0x00, 0x90, 60, 100, 0x60, 0x80, 60, 0x40 });
    expected.AddRange([0x00, 0xFF, 0x2F, 0x00]);
    Assert.That(body, Is.EqualTo(expected.ToArray()));
  }

  [Test]
  public void GarbageInput_DegradesToFullOnly() {
    using var ms = new MemoryStream(new byte[] { 0x00, 0x01, 0x02, 0x03 });
    var entries = new CmfFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.cmf"));
  }
}
