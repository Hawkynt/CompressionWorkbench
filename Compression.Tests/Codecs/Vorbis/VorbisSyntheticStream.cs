#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Compression.Tests.Codecs.Vorbis;

/// <summary>
/// Hand-assembles a complete, minimal Ogg-Vorbis I bitstream for end-to-end
/// decoder tests. There is no Vorbis encoder in the tree, so the identification,
/// comment and setup headers plus a single audio packet are crafted bit-by-bit
/// from the Vorbis I specification.
/// <para>
/// The setup is the smallest spec-legal configuration that still exercises a
/// real floor decode: one 1-entry codebook, one floor (type 0 or type 1), one
/// residue (type 0), one mapping and one short-block mode. blocksize_0 = 64 keeps
/// the IMDCT small. The audio packet drives the floor "unused" (silence) path, so
/// the decoder must produce a deterministic, fully-zero PCM frame end to end.
/// </para>
/// </summary>
internal static class VorbisSyntheticStream {

  /// <summary>LSB-first bit writer matching <c>VorbisBitReader</c> byte/bit order.</summary>
  internal sealed class BitWriter {
    private readonly List<byte> _bytes = [];
    private int _cur;
    private int _bitPos;

    public void Write(uint value, int count) {
      for (var i = 0; i < count; ++i) {
        var bit = (int)((value >> i) & 1);
        this._cur |= bit << this._bitPos;
        if (++this._bitPos == 8) { this._bytes.Add((byte)this._cur); this._cur = 0; this._bitPos = 0; }
      }
    }

    public byte[] ToArray() {
      var copy = new List<byte>(this._bytes);
      if (this._bitPos > 0) copy.Add((byte)this._cur);
      return copy.ToArray();
    }
  }

  /// <summary>
  /// Builds a full .ogg byte stream with floor type 0 or 1. When
  /// <paramref name="activeFloor"/> is false the audio packets drive the floor
  /// "unused" (silence) path; when true the floor is decoded (amplitude &gt; 0 /
  /// nonzero flag set) so the curve-synthesis code runs. The residue range is
  /// empty either way, so the emitted PCM frame is deterministically zero
  /// (floor × 0-residue), which still proves synthesis produced finite values.
  /// </summary>
  public static byte[] Build(int floorType, bool activeFloor = false, int sampleRate = 8000, byte channels = 1) {
    var ident = BuildIdentification(sampleRate, channels);
    var comment = BuildComment("synthetic");
    var setup = BuildSetup(floorType);
    var audio = activeFloor ? BuildActiveAudioPacket(floorType) : BuildSilenceAudioPacket(floorType);

    var ogg = new List<byte>();
    // Page 0: identification (BOS).
    ogg.AddRange(BuildOggPage(ident, serial: 7, flags: 0x02, granule: 0, seq: 0));
    // Page 1: comment + setup (a single page carrying two packets is legal).
    ogg.AddRange(BuildOggPageMulti([comment, setup], serial: 7, flags: 0, granule: 0, seq: 1));
    // The first audio packet primes the overlap-add buffer and emits no PCM; the
    // second produces one decoded half-block. Two identical silence packets give
    // a single emitted frame of pure-zero PCM.
    ogg.AddRange(BuildOggPageMulti([audio, audio], serial: 7, flags: 0x04, granule: 64, seq: 2));
    return ogg.ToArray();
  }

  private static byte[] BuildIdentification(int sampleRate, byte channels) {
    var pkt = new byte[30];
    pkt[0] = 0x01;
    pkt[1] = (byte)'v'; pkt[2] = (byte)'o'; pkt[3] = (byte)'r';
    pkt[4] = (byte)'b'; pkt[5] = (byte)'i'; pkt[6] = (byte)'s';
    BinaryPrimitives.WriteInt32LittleEndian(pkt.AsSpan(7, 4), 0);
    pkt[11] = channels;
    BinaryPrimitives.WriteInt32LittleEndian(pkt.AsSpan(12, 4), sampleRate);
    BinaryPrimitives.WriteInt32LittleEndian(pkt.AsSpan(16, 4), 0);
    BinaryPrimitives.WriteInt32LittleEndian(pkt.AsSpan(20, 4), 64_000);
    BinaryPrimitives.WriteInt32LittleEndian(pkt.AsSpan(24, 4), 0);
    pkt[28] = 0x66; // blocksize_0 = 1<<6 = 64, blocksize_1 = 1<<6 = 64
    pkt[29] = 1;    // framing
    return pkt;
  }

  private static byte[] BuildComment(string vendor) {
    var vendorBytes = System.Text.Encoding.UTF8.GetBytes(vendor);
    var pkt = new byte[7 + 4 + vendorBytes.Length + 4 + 1];
    pkt[0] = 0x03;
    pkt[1] = (byte)'v'; pkt[2] = (byte)'o'; pkt[3] = (byte)'r';
    pkt[4] = (byte)'b'; pkt[5] = (byte)'i'; pkt[6] = (byte)'s';
    BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(7, 4), (uint)vendorBytes.Length);
    Buffer.BlockCopy(vendorBytes, 0, pkt, 11, vendorBytes.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(11 + vendorBytes.Length, 4), 0);
    pkt[^1] = 1; // framing
    return pkt;
  }

  private static byte[] BuildSetup(int floorType) {
    var bw = new BitWriter();

    // ── 1 codebook ──
    bw.Write(0, 8); // codebook count - 1 = 0 ⇒ 1 codebook
    WriteCodebook(bw, floorType);

    // ── time-domain transforms: 1 entry, value 0 ──
    bw.Write(0, 6); // count - 1
    bw.Write(0, 16);

    // ── 1 floor ──
    bw.Write(0, 6); // count - 1
    bw.Write((uint)floorType, 16); // floor type
    if (floorType == 0) WriteFloor0(bw); else WriteFloor1(bw);

    // ── 1 residue (type 0) ──
    bw.Write(0, 6); // count - 1
    bw.Write(0, 16); // residue type 0
    WriteResidue0(bw);

    // ── 1 mapping (type 0) ──
    bw.Write(0, 6); // count - 1
    bw.Write(0, 16); // mapping type 0
    WriteMapping(bw);

    // ── 1 mode (short block) ──
    bw.Write(0, 6); // count - 1
    bw.Write(0, 1);  // block flag = 0 (short)
    bw.Write(0, 16); // window type
    bw.Write(0, 16); // transform type
    bw.Write(0, 8);  // mapping index

    bw.Write(1, 1); // framing

    var body = bw.ToArray();
    var pkt = new byte[7 + body.Length];
    pkt[0] = 0x05;
    pkt[1] = (byte)'v'; pkt[2] = (byte)'o'; pkt[3] = (byte)'r';
    pkt[4] = (byte)'b'; pkt[5] = (byte)'i'; pkt[6] = (byte)'s';
    Buffer.BlockCopy(body, 0, pkt, 7, body.Length);
    return pkt;
  }

  // A 1-entry codebook. The single codeword has length 1 (code 0). For floor 0 we
  // need a VQ codebook (lookup_type 1) so coefficients can be VQ-decoded; for
  // floor 1 a scalar codebook (lookup_type 0) is enough.
  private static void WriteCodebook(BitWriter bw, int floorType) {
    bw.Write(0x564342, 24);     // sync 'BCV'
    var dims = floorType == 0 ? 4 : 1; // floor0 order is 4 ⇒ dim 4 fills it in one vector
    bw.Write((uint)dims, 16);   // dimensions
    bw.Write(1, 24);            // entries
    bw.Write(0, 1);             // ordered = no
    bw.Write(0, 1);             // sparse = no
    bw.Write(0, 5);             // length-1 = 0 ⇒ codeword length 1

    if (floorType == 0) {
      bw.Write(1, 4);           // lookup_type 1
      WriteFloat32(bw, 0.5f);   // minimum value
      WriteFloat32(bw, 0.0f);   // delta value
      bw.Write(0, 4);           // value_bits - 1 = 0 ⇒ 1 bit
      bw.Write(0, 1);           // sequence_p = false
      // lookup_values for type 1 with entries=1, dim=4 ⇒ floor(1^(1/4)) = 1.
      // 1 multiplicand of 1 bit.
      bw.Write(0, 1);           // multiplicand[0]
    } else {
      bw.Write(0, 4);           // lookup_type 0 (scalar)
    }
  }

  private static void WriteFloat32(BitWriter bw, float value) {
    // Vorbis float: mantissa(21) | exponent(10) | sign(1), per VorbisBitReader.
    // We encode small constants exactly: value = mantissa * 2^(exp-788).
    if (value == 0f) { bw.Write(0, 32); return; }
    var sign = value < 0;
    var v = Math.Abs(value);
    // pick mantissa in [2^20, 2^21) range for precision
    var exp = 0;
    while (v < (1 << 20)) { v *= 2; exp--; }
    while (v >= (1 << 21)) { v /= 2; exp++; }
    var mantissa = (uint)Math.Round(v) & 0x1FFFFF;
    // value = mantissa * 2^(storedExp - 788) ⇒ storedExp = exp + 788
    var storedExp = (uint)(exp + 788) & 0x3FF;
    uint bits = mantissa | (storedExp << 21) | (sign ? 0x80000000u : 0u);
    bw.Write(bits, 32);
  }

  private static void WriteFloor0(BitWriter bw) {
    bw.Write(4, 8);    // order = 4 (even)
    bw.Write(8000, 16);// rate
    bw.Write(32, 16);  // bark_map_size
    bw.Write(8, 6);    // amplitude_bits
    bw.Write(0, 8);    // amplitude_offset
    bw.Write(0, 4);    // number_of_books - 1 = 0 ⇒ 1 book
    bw.Write(0, 8);    // book_list[0] = codebook 0
  }

  private static void WriteFloor1(BitWriter bw) {
    bw.Write(1, 5);    // partitions = 1
    bw.Write(0, 4);    // partition_class_list[0] = 0
    // class 0:
    bw.Write(0, 3);    // dimensions - 1 = 0 ⇒ 1
    bw.Write(0, 2);    // subclasses = 0
    // subclasses==0 ⇒ no masterbook; subclass_books has 1<<0 = 1 entry
    bw.Write(0, 8);    // subclass_book[0] = read - 1 ⇒ -1 (no book; value forced 0)
    bw.Write(0, 2);    // multiplier - 1 = 0 ⇒ 1
    bw.Write(4, 4);    // range_bits = 4 ⇒ x[1] = 16
    // one x value for the single class dimension
    bw.Write(8, 4);    // x value
  }

  private static void WriteResidue0(BitWriter bw) {
    bw.Write(0, 24);   // begin = 0
    bw.Write(0, 24);   // end = 0 ⇒ partitionsToRead 0, residue decode is a no-op
    bw.Write(0, 24);   // partition_size - 1 = 0 ⇒ 1
    bw.Write(0, 6);    // classifications - 1 = 0 ⇒ 1
    bw.Write(0, 8);    // classbook = 0
    // 1 classification cascade:
    bw.Write(0, 3);    // low bits
    bw.Write(0, 1);    // bitflag = 0 ⇒ no high bits
    // books: cascade==0 ⇒ no book reads
  }

  private static void WriteMapping(BitWriter bw) {
    bw.Write(0, 1);    // submaps flag = 0 ⇒ 1 submap
    bw.Write(0, 1);    // coupling flag = 0
    bw.Write(0, 2);    // reserved
    // submaps==1 ⇒ no mux bits
    bw.Write(0, 8);    // time placeholder
    bw.Write(0, 8);    // floor index = 0
    bw.Write(0, 8);    // residue index = 0
  }

  // Audio packet that drives the floor "unused" path → channel silence.
  private static byte[] BuildSilenceAudioPacket(int floorType) {
    var bw = new BitWriter();
    bw.Write(0, 1); // packet type = 0 (audio)
    // 1 mode ⇒ ilog(0) = 0 mode bits, no window flags for a short block.
    if (floorType == 0)
      bw.Write(0, 8); // amplitude = 0 ⇒ unused
    else
      bw.Write(0, 1); // floor1 nonzero flag = 0 ⇒ unused
    // residue end==begin ⇒ nothing read; nothing more needed.
    return bw.ToArray();
  }

  // Audio packet that drives the floor decode path (amplitude/flag set), so the
  // floor curve is synthesised. Residue is still empty ⇒ emitted PCM is zero.
  private static byte[] BuildActiveAudioPacket(int floorType) {
    var bw = new BitWriter();
    bw.Write(0, 1); // packet type = 0 (audio)
    if (floorType == 0) {
      bw.Write(200, 8); // amplitude > 0 ⇒ decode coefficients
      // book number: ilog(number_of_books - 1) = ilog(0) = 0 bits.
      // One VQ vector (dim 4) fills order 4. The 1-entry codebook's codeword is
      // length 1 with value 0, so a single 0 bit selects it.
      bw.Write(0, 1);
    } else {
      bw.Write(1, 1);  // floor1 nonzero flag = 1
      bw.Write(64, 8); // y[0]  (yBits = ilog(255) = 8, multiplier 1 ⇒ range 256)
      bw.Write(96, 8); // y[1]
      // partition class 0: subclasses 0 ⇒ no master decode; subclass_book -1 ⇒ y=0.
    }
    return bw.ToArray();
  }

  /// <summary>
  /// Like <see cref="Build"/> but the second audio packet's bitstream is cut
  /// short (Ogg framing stays valid) so the floor decode runs into end-of-packet.
  /// The decoder must degrade to silence rather than throw.
  /// </summary>
  public static byte[] BuildTruncatedAudio(int floorType, int sampleRate = 8000, byte channels = 1) {
    var ident = BuildIdentification(sampleRate, channels);
    var comment = BuildComment("synthetic");
    var setup = BuildSetup(floorType);
    var primer = BuildSilenceAudioPacket(floorType);
    // A packet that claims an active floor but supplies no coefficient/curve bits:
    // floor decode reads past end-of-packet (the bit reader returns 0 + EOF).
    var bw = new BitWriter();
    bw.Write(0, 1); // packet type audio
    if (floorType == 0)
      bw.Write(255, 8); // amplitude > 0, but no book/coefficient bits follow
    else
      bw.Write(1, 1);   // floor1 nonzero flag set, but no y-values follow
    var truncated = bw.ToArray();

    var ogg = new List<byte>();
    ogg.AddRange(BuildOggPage(ident, serial: 7, flags: 0x02, granule: 0, seq: 0));
    ogg.AddRange(BuildOggPageMulti([comment, setup], serial: 7, flags: 0, granule: 0, seq: 1));
    ogg.AddRange(BuildOggPageMulti([primer, truncated], serial: 7, flags: 0x04, granule: 64, seq: 2));
    return ogg.ToArray();
  }

  // ── Ogg page framing (CRC left zero; the reader does not validate it) ──

  private static byte[] BuildOggPage(byte[] payload, uint serial, byte flags, ulong granule, uint seq)
    => BuildOggPageMulti([payload], serial, flags, granule, seq);

  private static byte[] BuildOggPageMulti(byte[][] packets, uint serial, byte flags, ulong granule, uint seq) {
    var segSizes = new List<byte>();
    var payload = new List<byte>();
    foreach (var pkt in packets) {
      var remaining = pkt.Length;
      while (remaining >= 255) { segSizes.Add(255); remaining -= 255; }
      segSizes.Add((byte)remaining);
      payload.AddRange(pkt);
    }
    var header = new byte[27 + segSizes.Count];
    header[0] = (byte)'O'; header[1] = (byte)'g'; header[2] = (byte)'g'; header[3] = (byte)'S';
    header[4] = 0;
    header[5] = flags;
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(6, 8), granule);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14, 4), serial);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18, 4), seq);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22, 4), 0); // CRC
    header[26] = (byte)segSizes.Count;
    for (var i = 0; i < segSizes.Count; ++i) header[27 + i] = segSizes[i];
    var result = new byte[header.Length + payload.Count];
    Buffer.BlockCopy(header, 0, result, 0, header.Length);
    payload.CopyTo(result, header.Length);
    return result;
  }
}
