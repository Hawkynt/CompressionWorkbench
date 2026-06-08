#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Mp4;

namespace Compression.Tests.Mp4;

/// <summary>
/// Pins the synthetic Ogg page structure emitted by <see cref="OggStreamWriter"/> at the
/// page-header, segment-lacing and CRC level. A correct Ogg stream is required so the
/// repository's Opus/Vorbis decoders (which only accept Ogg) can consume re-wrapped MP4/MKV
/// packets.
/// </summary>
[TestFixture]
public class OggStreamWriterTests {

  /// <summary>Independent reference CRC-32 (Ogg: poly 0x04C11DB7, MSB-first, zero init/xor).</summary>
  private static uint ReferenceCrc(ReadOnlySpan<byte> data) {
    var crc = 0u;
    foreach (var b in data) {
      crc ^= (uint)b << 24;
      for (var i = 0; i < 8; ++i)
        crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
    }
    return crc;
  }

  [Test]
  public void FirstHeaderPage_HasOggSMagicAndBosFlag() {
    var ogg = OggStreamWriter.Build(serial: 1, [new byte[] { 1, 2, 3 }], [new byte[] { 9 }], granuleEnd: 4);
    Assert.That(Encoding.ASCII.GetString(ogg, 0, 4), Is.EqualTo("OggS"));
    Assert.That(ogg[4], Is.EqualTo(0));    // structure version
    Assert.That(ogg[5], Is.EqualTo(0x02)); // BOS on the first page
  }

  [Test]
  public void HeaderPage_SerialIsWrittenLittleEndian() {
    var ogg = OggStreamWriter.Build(serial: 0xCAFEBABE, [new byte[] { 1 }], [new byte[] { 2 }], 1);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(ogg.AsSpan(14)), Is.EqualTo(0xCAFEBABE));
  }

  [Test]
  public void ShortPacket_ProducesSingleLacingSegment() {
    // A 3-byte packet < 255 → exactly one segment of value 3.
    var ogg = OggStreamWriter.Build(serial: 1, [new byte[] { 0xAA, 0xBB, 0xCC }], [], granuleEnd: 0);
    Assert.That(ogg[26], Is.EqualTo(1));   // segment count
    Assert.That(ogg[27], Is.EqualTo(3));   // segment length
    Assert.That(ogg[28], Is.EqualTo(0xAA));
  }

  [Test]
  public void Packet255_ProducesTwoSegments_255Then0() {
    // A packet of exactly 255 bytes laces as [255, 0] to signal its end.
    var packet = new byte[255];
    Array.Fill(packet, (byte)0x5A);
    var ogg = OggStreamWriter.Build(serial: 1, [packet], [], granuleEnd: 0);
    Assert.That(ogg[26], Is.EqualTo(2));
    Assert.That(ogg[27], Is.EqualTo(255));
    Assert.That(ogg[28], Is.EqualTo(0));
  }

  [Test]
  public void Crc_MatchesIndependentReferenceImplementation() {
    var ogg = OggStreamWriter.Build(serial: 7, [new byte[] { 1, 2, 3, 4 }], [new byte[] { 5, 6 }], granuleEnd: 2);

    // The first page spans the header + its segment table + body. Recompute its CRC with
    // the field zeroed and compare against the stored value.
    var segCount = ogg[26];
    var bodyLen = 0;
    for (var i = 0; i < segCount; ++i) bodyLen += ogg[27 + i];
    var pageLen = 27 + segCount + bodyLen;

    var page = ogg.AsSpan(0, pageLen).ToArray();
    var stored = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(22));
    Array.Clear(page, 22, 4);
    Assert.That(stored, Is.EqualTo(ReferenceCrc(page)));
  }

  [Test]
  public void LastAudioPage_HasEosFlagAndGranuleEnd() {
    var ogg = OggStreamWriter.Build(serial: 1, [new byte[] { 1 }], [new byte[] { 2 }, new byte[] { 3 }], granuleEnd: 1920);

    // Walk pages to the final one.
    var pos = 0;
    var lastFlags = (byte)0;
    ulong lastGranule = 0;
    while (pos + 27 <= ogg.Length) {
      var segCount = ogg[pos + 26];
      var bodyLen = 0;
      for (var i = 0; i < segCount; ++i) bodyLen += ogg[pos + 27 + i];
      lastFlags = ogg[pos + 5];
      lastGranule = BinaryPrimitives.ReadUInt64LittleEndian(ogg.AsSpan(pos + 6));
      pos += 27 + segCount + bodyLen;
    }
    Assert.That(lastFlags, Is.EqualTo(0x04));     // EOS on the last page
    Assert.That(lastGranule, Is.EqualTo(1920ul)); // final granule position
  }
}
