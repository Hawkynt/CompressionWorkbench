#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Bcstm;

/// <summary>
/// Parses a little-endian 3DS <c>.bcstm</c> (CSTM) stream into its stream-info, per-channel
/// DSP-ADPCM coefficient tables and the channel-interleaved block data, following the public
/// CSTM/CWAV specification (the layout documented by VGAudio / 3dbrew).
/// <para>
/// The file header is <c>"CSTM"</c> + BOM <c>0xFEFF</c> (little-endian) + header size + version +
/// file size + block count, followed by per-block <c>(u16 sectionId, u16 pad, u32 offset, u32 size)</c>
/// table entries (<c>0x4000</c> = INFO, <c>0x4001</c> = SEEK, <c>0x4002</c> = DATA). The INFO block
/// carries a stream-info structure (codec / loop / channel count / sample rate / sample count /
/// block sizes) plus a per-channel ADPCM coefficient table; the DATA block holds channel-interleaved
/// coded blocks exactly like BRSTM.
/// </para>
/// <para>
/// SIMPLIFICATION: real C/FSTM INFO blocks reach the per-channel coefficient structs through a chain
/// of relative reference offsets. To keep the reader robust the channel coefficient table is read
/// from a fixed, documented layout (a flat per-channel <c>0x2E</c>-byte block right after the
/// stream-info body) that <see cref="BcstmWriter"/> emits; the documented section/field layout of the
/// header, block table, stream-info and DATA block is otherwise honoured faithfully. The reader is
/// verified against the writer for round-tripping.
/// </para>
/// </summary>
public sealed class BcstmReader {

  /// <summary>
  /// Represents a stream info.
  /// </summary>
public sealed record StreamInfo(
    int Codec,
    bool Loop,
    int NumChannels,
    int SampleRate,
    int LoopStart,
    int TotalSamples,
    int DataOffset,
    int NumBlocks,
    int BlockSize,
    int SamplesPerBlock,
    int FinalBlockSize,
    int FinalBlockSamples,
    int FinalBlockSizePadded,
    bool BigEndian);

  /// <summary>
  /// Represents a parsed stream.
  /// </summary>
public sealed record ParsedStream(
    StreamInfo Info,
    short[][] Coefs,           // [channel][16]
    short[][] Pcm);            // [channel][totalSamples] decoded to PCM16

  // Section ids.
  private const int SectionInfo = 0x4000;
  private const int SectionData = 0x4002;

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public ParsedStream Read(ReadOnlySpan<byte> data) {
    if (data.Length < 0x40)
      throw new InvalidDataException("Stream too short for CSTM header.");
    if (data[0] != 'C' || data[1] != 'S' || data[2] != 'T' || data[3] != 'M')
      throw new InvalidDataException("Missing CSTM magic.");

    // BOM at 0x04 decides endianness: 0xFEFF read big-endian => BE; little-endian => LE.
    var bomBe = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
    var bigEndian = bomBe == 0xFEFF;
    if (!bigEndian && BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != 0xFEFF)
      throw new InvalidDataException("Missing/invalid byte-order mark.");

    var numBlocks = ReadU16(data[0x10..], bigEndian);

    int infoOff = -1, dataOff = -1;
    for (var b = 0; b < numBlocks; ++b) {
      var entry = 0x14 + b * 12;
      if (entry + 12 > data.Length)
        throw new InvalidDataException("Block table runs past end of file.");
      var sectionId = ReadU16(data[entry..], bigEndian);
      var off = (int)ReadU32(data[(entry + 4)..], bigEndian);
      if (sectionId == SectionInfo) infoOff = off;
      else if (sectionId == SectionData) dataOff = off;
    }
    if (infoOff < 0 || dataOff < 0)
      throw new InvalidDataException("CSTM is missing INFO or DATA block.");

    if (infoOff + 8 > data.Length || data[infoOff] != (byte)'I' || data[infoOff + 1] != (byte)'N')
      throw new InvalidDataException("INFO block magic missing.");

    var (info, coefs) = ReadInfo(data, infoOff, dataOff, bigEndian);
    var pcm = Decode(data, info, coefs);
    return new ParsedStream(info, coefs, pcm);
  }

  // INFO body begins at infoOff+8. Stream-info structure sits at +0x18 (documented offset).
  private static (StreamInfo, short[][]) ReadInfo(ReadOnlySpan<byte> data, int infoOff, int dataOff, bool be) {
    var siBase = infoOff + 8 + 0x18;
    var codec = data[siBase + 0];
    var loop = data[siBase + 1] != 0;
    var channels = data[siBase + 2];
    var sampleRate = (int)ReadU32(data[(siBase + 4)..], be);
    var loopStart = (int)ReadU32(data[(siBase + 8)..], be);
    var totalSamples = (int)ReadU32(data[(siBase + 12)..], be);
    var numBlocks = (int)ReadU32(data[(siBase + 16)..], be);
    var blockSize = (int)ReadU32(data[(siBase + 20)..], be);
    var samplesPerBlock = (int)ReadU32(data[(siBase + 24)..], be);
    var finalBlockSamples = (int)ReadU32(data[(siBase + 28)..], be);
    var finalBlockSize = (int)ReadU32(data[(siBase + 32)..], be);
    var finalBlockSizePadded = (int)ReadU32(data[(siBase + 36)..], be);
    // DATA payload begins after the DATA chunk header (8 bytes) + 0x18 reserved.
    var audioStart = dataOff + 8 + 0x18;

    // Per-channel coefficient table: flat 0x2E-byte struct per channel right after stream-info body.
    var coefBase = siBase + 0x40;
    var coefs = new short[channels][];
    for (var c = 0; c < channels; ++c) {
      var o = coefBase + c * 0x2E;
      var table = new short[16];
      for (var i = 0; i < 16; ++i)
        table[i] = ReadS16(data[(o + i * 2)..], be);
      coefs[c] = table;
    }

    var info = new StreamInfo(codec, loop, channels, sampleRate, loopStart, totalSamples,
      audioStart, numBlocks, blockSize, samplesPerBlock, finalBlockSize, finalBlockSamples,
      finalBlockSizePadded, be);
    return (info, coefs);
  }

  private static short[][] Decode(ReadOnlySpan<byte> data, StreamInfo info, short[][] coefs) {
    var channels = info.NumChannels;
    var perChannelCoded = new byte[channels][];
    using (var streams = new MemoryStreams(channels)) {
      var pos = info.DataOffset;
      for (var blk = 0; blk < info.NumBlocks; ++blk) {
        var isFinal = blk == info.NumBlocks - 1;
        var rawSize = isFinal ? info.FinalBlockSize : info.BlockSize;
        var paddedSize = isFinal ? info.FinalBlockSizePadded : info.BlockSize;
        for (var c = 0; c < channels; ++c) {
          streams.Write(c, data.Slice(pos, rawSize));
          pos += paddedSize;
        }
      }
      for (var c = 0; c < channels; ++c) perChannelCoded[c] = streams.ToArray(c);
    }

    var pcm = new short[channels][];
    for (var c = 0; c < channels; ++c)
      pcm[c] = DecodeChannel(perChannelCoded[c], info, coefs[c]);
    return pcm;
  }

  private static short[] DecodeChannel(byte[] coded, StreamInfo info, short[] coefs) {
    switch (info.Codec) {
      case 2:
        return Codec.DspAdpcm.DspAdpcmCodec.Decode(coded, coefs, info.TotalSamples);
      case 1: { // PCM16
        var pcm = new short[info.TotalSamples];
        for (var i = 0; i < info.TotalSamples; ++i)
          pcm[i] = info.BigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(coded.AsSpan(i * 2))
            : BinaryPrimitives.ReadInt16LittleEndian(coded.AsSpan(i * 2));
        return pcm;
      }
      case 0: { // PCM8 signed
        var pcm = new short[info.TotalSamples];
        for (var i = 0; i < info.TotalSamples; ++i)
          pcm[i] = (short)((sbyte)coded[i] << 8);
        return pcm;
      }
      default:
        throw new InvalidDataException($"Unsupported CSTM coding type {info.Codec}.");
    }
  }

  private static ushort ReadU16(ReadOnlySpan<byte> s, bool be)
    => be ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
  private static uint ReadU32(ReadOnlySpan<byte> s, bool be)
    => be ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
  private static short ReadS16(ReadOnlySpan<byte> s, bool be)
    => be ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s);

  private sealed class MemoryStreams(int count) : IDisposable {
    private readonly MemoryStream[] _streams = Enumerable.Range(0, count).Select(_ => new MemoryStream()).ToArray();
    public void Write(int channel, ReadOnlySpan<byte> data) => _streams[channel].Write(data);
    public byte[] ToArray(int channel) => _streams[channel].ToArray();
    public void Dispose() { foreach (var s in _streams) s.Dispose(); }
  }
}
