#pragma warning disable CS1591
using System.Text;
using Codec.Musepack;
using Compression.Registry;
using FileFormat.Mpc;

namespace Compression.Tests.Mpc;

[TestFixture]
public class MusepackTests {

  // ── Container varint (ffio_read_varlen) ───────────────────────────────────

  [Test]
  public void Varint_SingleByte_DecodesValue() {
    var data = new byte[] { 0x7F };
    var pos = 0;
    Assert.That(MpcContainer.ReadVarint(data, ref pos), Is.EqualTo(0x7F));
    Assert.That(pos, Is.EqualTo(1));
  }

  [Test]
  public void Varint_MultiByte_BigEndianBase128() {
    // 0x81,0x00 => (1<<7)|0 = 128 ; continuation on first byte (MSB set).
    var data = new byte[] { 0x81, 0x00 };
    var pos = 0;
    Assert.That(MpcContainer.ReadVarint(data, ref pos), Is.EqualTo(128));
    Assert.That(pos, Is.EqualTo(2));
  }

  [Test]
  public void Varint_ThreeBytes() {
    // 0x82,0x80,0x01 => ((2<<14)|(0<<7)|1) = 32769
    var data = new byte[] { 0x82, 0x80, 0x01 };
    var pos = 0;
    Assert.That(MpcContainer.ReadVarint(data, ref pos), Is.EqualTo(32769));
  }

  // ── Chunk header parsing (size includes tag + varint) ─────────────────────

  [Test]
  public void ChunkHeader_PayloadLength_ExcludesHeaderBytes() {
    // 'SE' tag, empty payload. The varint counts the whole chunk (tag + varint),
    // so an empty chunk has total size 3 (2 tag bytes + 1 varint byte).
    var data = new byte[] { (byte)'S', (byte)'E', 0x03 };
    var pos = 0;
    var chunk = MpcContainer.ReadChunkHeader(data, ref pos);
    Assert.That(chunk.Tag, Is.EqualTo("SE"));
    Assert.That(chunk.PayloadLength, Is.EqualTo(0));
    Assert.That(chunk.PayloadStart, Is.EqualTo(3));
  }

  [Test]
  public void ChunkHeader_WithPayload() {
    // 'AP' total size 6 => header is 3 bytes (tag+varint) so payload is 3.
    var data = new byte[] { (byte)'A', (byte)'P', 0x06, 0xAA, 0xBB, 0xCC };
    var pos = 0;
    var chunk = MpcContainer.ReadChunkHeader(data, ref pos);
    Assert.That(chunk.Tag, Is.EqualTo("AP"));
    Assert.That(chunk.PayloadLength, Is.EqualTo(3));
    Assert.That(pos, Is.EqualTo(3));
  }

  // ── Stream-header (SH) parse exactness ────────────────────────────────────

  [Test]
  public void StreamHeader_ParsesAllFields() {
    var blob = BuildSv8Header(sampleRateIdx: 0, maxBandMinus1: 0, channelsMinus1: 1,
      midSide: true, framesExp: 0, sampleCount: 1152, beginningSilence: 0);
    using var ms = new MemoryStream(blob);
    var info = MusepackCodec.ReadStreamInfo(ms);

    Assert.That(info.Version, Is.EqualTo(8));
    Assert.That(info.SampleRate, Is.EqualTo(44100));
    Assert.That(info.Channels, Is.EqualTo(2));
    Assert.That(info.MaxBand, Is.EqualTo(1));
    Assert.That(info.MidSideUsed, Is.True);
    Assert.That(info.SampleCount, Is.EqualTo(1152));
  }

  [Test]
  public void StreamHeader_SampleRateIndexMapping() {
    foreach (var (idx, hz) in new[] { (0, 44100), (1, 48000), (2, 37800), (3, 32000) }) {
      var blob = BuildSv8Header(sampleRateIdx: idx, maxBandMinus1: 0, channelsMinus1: 0,
        midSide: false, framesExp: 0, sampleCount: 0, beginningSilence: 0);
      using var ms = new MemoryStream(blob);
      Assert.That(MusepackCodec.ReadStreamInfo(ms).SampleRate, Is.EqualTo(hz), $"index {idx}");
    }
  }

  [Test]
  public void StreamHeader_VarintSampleCount_LargeValue() {
    var blob = BuildSv8Header(sampleRateIdx: 1, maxBandMinus1: 5, channelsMinus1: 0,
      midSide: false, framesExp: 0, sampleCount: 1_000_000, beginningSilence: 480);
    using var ms = new MemoryStream(blob);
    var info = MusepackCodec.ReadStreamInfo(ms);
    Assert.That(info.SampleCount, Is.EqualTo(1_000_000));
    Assert.That(info.MaxBand, Is.EqualTo(6));
  }

  // ── Huffman / VLC table sanity invariants ─────────────────────────────────

  [Test]
  public void VlcBooks_SymbolPoolsFullyConsumed() {
    // The shared QSyms pool must be partitioned exactly across the q* books with
    // no symbols left over and no overrun — a direct check that every book's
    // length-count histogram sums to the right number of symbols.
    var books = MpcVlcBooks.Shared;
    var q = books.Q1.SymbolCount + books.Q9Up.SymbolCount;
    for (var i = 0; i < 2; ++i) {
      q += books.Q2[i].SymbolCount + books.Q3[i].SymbolCount;
      for (var j = 0; j < 4; ++j)
        q += books.Quant[j][i].SymbolCount;
    }
    Assert.That(q, Is.EqualTo(MpcHuffTables.QSyms.Length),
      "q* books must partition the whole QSyms pool");
  }

  [Test]
  public void VlcBooks_LengthCountsMatchKnownSymbolTotals() {
    Assert.That(Sum(MpcHuffTables.BandsLenCounts), Is.EqualTo(MpcHuffTables.BandsSyms.Length));
    Assert.That(Sum(MpcHuffTables.ResLenCounts[0]) + Sum(MpcHuffTables.ResLenCounts[1]),
      Is.EqualTo(MpcHuffTables.ResSyms.Length));
    Assert.That(Sum(MpcHuffTables.DscfLenCounts[0]) + Sum(MpcHuffTables.DscfLenCounts[1]),
      Is.EqualTo(MpcHuffTables.DscfSyms.Length));
    Assert.That(Sum(MpcHuffTables.ScfiLenCounts[0]) + Sum(MpcHuffTables.ScfiLenCounts[1]),
      Is.EqualTo(MpcHuffTables.ScfiSyms.Length));
  }

  [Test]
  public void Vlc_DecodesCanonicalPrefixesUniquely() {
    // A tiny hand-built book: one 1-bit code and two 2-bit codes. Canonical
    // assignment (shortest first, listing order): 1-bit -> 0, then 2-bit codes
    // -> 10, 11. Symbols are taken longest-first by build order, so the symbol
    // pool must list the two length-2 symbols before the length-1 symbol.
    byte[] counts = { 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    byte[] syms = { 20, 21, 7 }; // len2 a, len2 b, len1
    var vlc = new MpcVlc(counts, syms, 0, 0);

    Assert.That(ReadOne(vlc, "0"), Is.EqualTo(7));
    Assert.That(ReadOne(vlc, "10"), Is.EqualTo(20));
    Assert.That(ReadOne(vlc, "11"), Is.EqualTo(21));
  }

  // ── Bit reader ────────────────────────────────────────────────────────────

  [Test]
  public void BitReader_ReadsMsbFirst() {
    var r = new MpcBitReader(new byte[] { 0b1011_0010 }, 0, 1);
    Assert.That(r.GetBits(3), Is.EqualTo(0b101));
    Assert.That(r.GetBits(5), Is.EqualTo(0b10010));
  }

  [Test]
  public void BitReader_OverreadReturnsZero() {
    var r = new MpcBitReader(new byte[] { 0xFF }, 0, 1);
    Assert.That(r.GetBits(8), Is.EqualTo(0xFF));
    Assert.That(r.GetBits(8), Is.EqualTo(0)); // past end → zero padding
    Assert.That(r.BitsLeft, Is.LessThan(0));
  }

  // ── Deterministic decode of a crafted minimal AP packet ───────────────────

  [Test]
  public void Decode_AllZeroResFrame_ProducesSilenceWithExactSampleCount() {
    // One keyframe sub-frame with maxband = 0 (all-zero resolutions): no
    // coefficients are read, dequant is all-zero, and the polyphase synthesis of
    // zero subbands yields zero PCM — exactly 1152 samples per channel.
    var mpc = BuildSilentSv8(channels: 1, sampleCount: 1152);
    using var src = new MemoryStream(mpc);
    using var pcm = new MemoryStream();
    MusepackCodec.Decompress(src, pcm);

    var bytes = pcm.ToArray();
    Assert.That(bytes.Length, Is.EqualTo(1152 * 1 * 2), "1152 mono int16 samples");
    Assert.That(bytes, Is.All.EqualTo(0), "all-zero subbands decode to digital silence");
  }

  [Test]
  public void Decode_RespectsDeclaredSampleCount_TrimsPadding() {
    // Declare only 500 samples even though a full frame is 1152: the decoder must
    // emit exactly the declared per-channel count.
    var mpc = BuildSilentSv8(channels: 2, sampleCount: 500);
    using var src = new MemoryStream(mpc);
    using var pcm = new MemoryStream();
    MusepackCodec.Decompress(src, pcm);
    Assert.That(pcm.ToArray().Length, Is.EqualTo(500 * 2 * 2));
  }

  [Test]
  public void Decode_TruncatedAudioPacket_DoesNotThrow() {
    var mpc = BuildSilentSv8(channels: 1, sampleCount: 1152);
    var truncated = mpc[..(mpc.Length - 1)]; // chop the last byte of the AP/SE region
    using var src = new MemoryStream(truncated);
    using var pcm = new MemoryStream();
    Assert.DoesNotThrow(() => MusepackCodec.Decompress(src, pcm));
  }

  // ── SV7 fallback ──────────────────────────────────────────────────────────

  [Test]
  public void Sv7_Decompress_ThrowsNotSupported() {
    var sv7 = "MP+"u8.ToArray().Concat(new byte[] { 0x07, 0, 0, 0 }).ToArray();
    using var src = new MemoryStream(sv7);
    using var pcm = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => MusepackCodec.Decompress(src, pcm));
  }

  [Test]
  public void Sv7_ReadStreamInfo_ThrowsNotSupported() {
    var sv7 = "MP+"u8.ToArray().Concat(new byte[] { 0x07, 0, 0, 0 }).ToArray();
    using var src = new MemoryStream(sv7);
    Assert.Throws<NotSupportedException>(() => MusepackCodec.ReadStreamInfo(src));
  }

  // ── Descriptor list / extract / fallback ──────────────────────────────────

  [Test]
  public void Descriptor_ListsFullChannelsAndMetadata_Mono() {
    var mpc = BuildSilentSv8(channels: 1, sampleCount: 1152);
    using var ms = new MemoryStream(mpc);
    var entries = new MpcFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.mpc" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Descriptor_Stereo_SurfacesLeftRight() {
    var mpc = BuildSilentSv8(channels: 2, sampleCount: 1152);
    using var ms = new MemoryStream(mpc);
    var entries = new MpcFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
  }

  [Test]
  public void Descriptor_ExtractedChannelIsValidMonoWav() {
    var mpc = BuildSilentSv8(channels: 1, sampleCount: 1152);
    using var ms = new MemoryStream(mpc);
    using var output = new MemoryStream();
    new MpcFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));
  }

  [Test]
  public void Descriptor_Sv7_FallsBackToFullOnly() {
    var sv7 = "MP+"u8.ToArray().Concat(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0 }).ToArray();
    using var ms = new MemoryStream(sv7);
    var entries = new MpcFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.mpc"));
  }

  [Test]
  public void Descriptor_GarbageInput_FallsBackToFullOnly() {
    var garbage = Encoding.ASCII.GetBytes("MPCKnonsense-not-a-real-stream-header");
    using var ms = new MemoryStream(garbage);
    var entries = new MpcFormatDescriptor().List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.mpc"));
  }

  [Test]
  public void Descriptor_MagicsAndExtensions() {
    var d = new MpcFormatDescriptor();
    Assert.That(d.Extensions, Does.Contain(".mpc"));
    Assert.That(d.Extensions, Does.Contain(".mpp"));
    Assert.That(d.Extensions, Does.Contain(".mp+"));
    Assert.That(d.MagicSignatures.Any(m => m.Bytes.AsSpan().SequenceEqual("MPCK"u8)), Is.True);
    Assert.That(d.MagicSignatures.Any(m => m.Bytes.AsSpan().SequenceEqual("MP+"u8)), Is.True);
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  private static int Sum(byte[] a) {
    var s = 0;
    foreach (var v in a) s += v;
    return s;
  }

  private static int ReadOne(MpcVlc vlc, string bitsMsbFirst) {
    var bytes = new byte[(bitsMsbFirst.Length + 7) / 8];
    for (var i = 0; i < bitsMsbFirst.Length; ++i)
      if (bitsMsbFirst[i] == '1')
        bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
    return vlc.Read(new MpcBitReader(bytes, 0, bytes.Length));
  }

  // Encodes a base-128 big-endian varint (ffio_read_varlen form).
  private static void WriteVarint(List<byte> buf, long value) {
    Span<byte> tmp = stackalloc byte[10];
    var n = 0;
    tmp[n++] = (byte)(value & 0x7F);
    value >>= 7;
    while (value > 0) {
      tmp[n++] = (byte)((value & 0x7F) | 0x80);
      value >>= 7;
    }
    for (var i = n - 1; i >= 0; --i)
      buf.Add(tmp[i]);
  }

  // Builds an MPCK file containing only the SH chunk (enough for ReadStreamInfo).
  private static byte[] BuildSv8Header(int sampleRateIdx, int maxBandMinus1, int channelsMinus1,
      bool midSide, int framesExp, long sampleCount, long beginningSilence) {
    var payload = new List<byte> { 0x12, 0x34, 0x56, 0x78, /* version */ 8 };
    WriteVarint(payload, sampleCount);
    WriteVarint(payload, beginningSilence);
    var b0 = (byte)(((sampleRateIdx & 0x07) << 5) | (maxBandMinus1 & 0x1F));
    var b1 = (byte)(((channelsMinus1 & 0x0F) << 4) | ((midSide ? 1 : 0) << 3) | (framesExp & 0x07));
    payload.Add(b0);
    payload.Add(b1);

    var file = new List<byte>();
    file.AddRange("MPCK"u8.ToArray());
    EmitChunk(file, "SH", payload);
    return file.ToArray();
  }

  // Emits a chunk whose varint size includes the tag + varint + payload bytes.
  private static void EmitChunk(List<byte> file, string tag, IReadOnlyList<byte> payload) {
    // The size field is self-referential (it counts its own bytes). Compute the
    // varint length iteratively until it stabilises.
    var headerOnly = 2; // tag bytes
    var varintLen = 1;
    long total;
    while (true) {
      total = headerOnly + varintLen + payload.Count;
      var actual = VarintLength(total);
      if (actual == varintLen)
        break;
      varintLen = actual;
    }
    file.Add((byte)tag[0]);
    file.Add((byte)tag[1]);
    WriteVarint(file, total);
    file.AddRange(payload);
  }

  private static int VarintLength(long value) {
    var n = 1;
    value >>= 7;
    while (value > 0) { ++n; value >>= 7; }
    return n;
  }

  // Builds a complete decodable silent SV8 stream: SH + one AP packet whose single
  // keyframe sub-frame codes maxband = 0 (all-zero resolutions). With sample-rate
  // index 0 and frames-exponent 0 the packet holds exactly one MPC frame.
  private static byte[] BuildSilentSv8(int channels, long sampleCount) {
    var shPayload = new List<byte> { 0x12, 0x34, 0x56, 0x78, 8 };
    WriteVarint(shPayload, sampleCount);
    WriteVarint(shPayload, 0);
    shPayload.Add(0x00);                                  // srIdx 0, maxband-1 = 0 → maxbands = 1
    shPayload.Add((byte)(((channels - 1) & 0x0F) << 4));  // channels, MSS=0, framesExp=0

    // AP payload: the keyframe reads maxband via GetModGolomb(maxbands+1 = 2).
    // GetModGolomb(2) -> DecBase(1, 3): reads CnkLen[0][2]-1 bits. CnkLen[0][2] is
    // the length for choosing the band count; supplying zero bits yields band 0.
    // A handful of zero bytes is plenty for a single all-zero-res frame.
    var apPayload = new byte[8]; // all zero

    var file = new List<byte>();
    file.AddRange("MPCK"u8.ToArray());
    EmitChunk(file, "SH", shPayload);
    EmitChunk(file, "AP", apPayload);
    EmitChunk(file, "SE", new List<byte>());
    return file.ToArray();
  }
}
