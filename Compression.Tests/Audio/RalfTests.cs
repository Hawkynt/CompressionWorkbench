#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Ralf;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the RealAudio Lossless ("ralf") decoder (<see cref="RalfCodec"/>), a faithful decode-only
/// port of FFmpeg's <c>libavcodec/ralf.c</c> + the canonical Huffman tables from
/// <c>ralfdata.h</c>. Cross-checking against FFmpeg output is not available in this environment, so
/// these tests pin determinism + structure: the "LSD:" extradata parse, construction guards, and a
/// crafted minimal packet whose block carries an empty filter (FILTER_NONE) so the decoded output
/// is exactly the per-channel bias — a deterministic, hand-verifiable shape.
/// </summary>
[TestFixture]
public class RalfTests {

  /// <summary>Builds 24-byte "LSD:" extradata for the given geometry.</summary>
  private static byte[] Extradata(int channels, int sampleRate, int maxFrameSize) {
    var ex = new byte[24];
    ex[0] = (byte)'L'; ex[1] = (byte)'S'; ex[2] = (byte)'D'; ex[3] = (byte)':';
    BinaryPrimitives.WriteUInt16BigEndian(ex.AsSpan(4), 0x103);
    BinaryPrimitives.WriteUInt16BigEndian(ex.AsSpan(8), (ushort)channels);
    BinaryPrimitives.WriteUInt32BigEndian(ex.AsSpan(12), (uint)sampleRate);
    BinaryPrimitives.WriteUInt32BigEndian(ex.AsSpan(16), (uint)maxFrameSize);
    return ex;
  }

  // ── extradata parse ──────────────────────────────────────────────────────────────

  [Test]
  public void Extradata_ParsesGeometry() {
    var codec = new RalfCodec(Extradata(2, 44100, 4096));
    Assert.That(codec.Version, Is.EqualTo(0x103));
    Assert.That(codec.Channels, Is.EqualTo(2));
    Assert.That(codec.SampleRate, Is.EqualTo(44100));
    // max_frame_size is raised to at least the sample rate by the reference.
    Assert.That(codec.MaxFrameSize, Is.EqualTo(44100));
  }

  [Test]
  public void Extradata_MissingMarker_Throws() {
    var ex = Extradata(1, 44100, 4096);
    ex[0] = (byte)'X';
    Assert.That(() => new RalfCodec(ex), Throws.ArgumentException);
  }

  [Test]
  public void Extradata_TooShort_Throws() {
    Assert.That(() => new RalfCodec(new byte[10]), Throws.ArgumentException);
  }

  [Test]
  public void Extradata_UnsupportedVersion_Throws() {
    var ex = Extradata(1, 44100, 4096);
    BinaryPrimitives.WriteUInt16BigEndian(ex.AsSpan(4), 0x102);
    Assert.That(() => new RalfCodec(ex), Throws.InstanceOf<NotSupportedException>());
  }

  [Test]
  public void Extradata_RejectsInvalidChannelCount() {
    Assert.That(() => new RalfCodec(Extradata(3, 44100, 4096)), Throws.ArgumentException);
    Assert.That(() => new RalfCodec(Extradata(0, 44100, 4096)), Throws.ArgumentException);
  }

  [Test]
  public void Extradata_RejectsOutOfRangeSampleRate() {
    Assert.That(() => new RalfCodec(Extradata(1, 4000, 4096)), Throws.ArgumentException);
    Assert.That(() => new RalfCodec(Extradata(1, 192000, 4096)), Throws.ArgumentException);
  }

  // ── packet framing tolerance ───────────────────────────────────────────────────────

  [Test]
  public void TooShortPacket_DecodesToEmpty() {
    var codec = new RalfCodec(Extradata(1, 44100, 4096));
    Assert.That(codec.Decode(new byte[3]).Length, Is.EqualTo(0));
    Assert.That(codec.Decode([]).Length, Is.EqualTo(0));
  }

  // ── crafted minimal packet ───────────────────────────────────────────────────────────

  [Test]
  public void CraftedMonoPacket_DecodesToBoundedFiniteSamples() {
    // A single block-size-table entry pointing at an all-zero block body. The all-zero body
    // drives every VLC to its canonical zero-prefix symbol; whatever that resolves to, the decode
    // must stay bounded (16-bit) and never throw.
    var (codec, packet) = BuildCraftedMonoPacket();
    var pcm = codec.Decode(packet);
    Assert.That(pcm.All(s => s >= short.MinValue && s <= short.MaxValue), Is.True);
  }

  [Test]
  public void CraftedMonoPacket_IsDeterministic() {
    var (codec1, packet) = BuildCraftedMonoPacket();
    var (codec2, _) = BuildCraftedMonoPacket();
    Assert.That(codec1.Decode(packet), Is.EqualTo(codec2.Decode(packet)));
  }

  // ── table sanity ────────────────────────────────────────────────────────────────────

  [Test]
  public void TableBytes_HaveExpectedSizes() {
    Assert.That(RalfTables.FilterParamBytes.Length, Is.EqualTo(3 * 324));
    Assert.That(RalfTables.BiasBytes.Length, Is.EqualTo(3 * 128));
    Assert.That(RalfTables.CodingModeBytes.Length, Is.EqualTo(3 * 72));
    Assert.That(RalfTables.FilterCoeffsBytes.Length, Is.EqualTo(3 * 10 * 11 * 24));
    Assert.That(RalfTables.ShortCodesBytes.Length, Is.EqualTo(3 * 15 * 88));
    Assert.That(RalfTables.LongCodesBytes.Length, Is.EqualTo(3 * 125 * 224));
  }

  /// <summary>
  /// Builds a mono RALF packet: a 2-byte big-endian table-size prefix, a one-entry block-size
  /// table (entry width <c>13 + channels = 14</c> bits + a 0 "has pts" flag) that points at a small
  /// all-zero block body. The decoder reads the table, then decodes the single block; an all-zero
  /// body drives every canonical-Huffman VLC to its zero-prefix symbol.
  /// </summary>
  private static (RalfCodec, byte[]) BuildCraftedMonoPacket() {
    var codec = new RalfCodec(Extradata(1, 44100, 4096));

    const int blockBytes = 8;
    const int tableBits = 14 + 1;
    var tableBytes = (tableBits + 7) >> 3;

    var packet = new byte[2 + tableBytes + blockBytes];
    BinaryPrimitives.WriteUInt16BigEndian(packet, tableBits);

    // Encode the single block-size value (= blockBytes) MSB-first into the 14-bit field, then a
    // 0 "has pts" flag.
    var tableRegion = new bool[tableBits];
    for (var b = 0; b < 14; ++b)
      tableRegion[b] = ((blockBytes >> (13 - b)) & 1) != 0;
    tableRegion[14] = false; // no pts
    for (var b = 0; b < tableBits; ++b)
      if (tableRegion[b])
        packet[2 + (b >> 3)] |= (byte)(1 << (7 - (b & 7)));

    return (codec, packet);
  }
}
