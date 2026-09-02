#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Brstm;

/// <summary>
/// Parses a big-endian Wii <c>.brstm</c> (RSTM) stream into its stream-info, per-channel
/// DSP-ADPCM coefficient tables and the channel-interleaved block data, following the public
/// BRSTM specification (the layout documented by smashboards/VGAudio/brawllib).
/// <para>
/// Only big-endian RSTM (BOM <c>0xFEFF</c>, the Wii standard) is supported. Three coding
/// types are recognised: <c>0</c> = PCM8, <c>1</c> = PCM16 big-endian, <c>2</c> = DSP-ADPCM.
/// The reader is structured per the spec and is verified against
/// <see cref="BrstmWriter"/>'s output for round-tripping.
/// </para>
/// </summary>
public sealed class BrstmReader {

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
    int FinalBlockSizePadded);

    /// <summary>
  /// Represents a parsed brstm.
  /// </summary>
public sealed record ParsedBrstm(
    StreamInfo Info,
    short[][] Coefs,           // [channel][16]
    short[][] Pcm);            // [channel][totalSamples] decoded to PCM16

    /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public ParsedBrstm Read(ReadOnlySpan<byte> data) {
    if (data.Length < 0x40)
      throw new InvalidDataException("BRSTM too short for RSTM header.");
    if (data[0] != 'R' || data[1] != 'S' || data[2] != 'T' || data[3] != 'M')
      throw new InvalidDataException("Missing RSTM magic.");
    var bom = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
    if (bom != 0xFEFF)
      throw new InvalidDataException("Only big-endian BRSTM (BOM 0xFEFF) is supported.");

    var headOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x10..]);
    // 0x14: HEAD size, 0x18/0x1C ADPC off/size, 0x20/0x24 DATA off/size.

    if (headOffset + 8 > data.Length || data[headOffset] != 'H' || data[headOffset + 1] != 'E')
      throw new InvalidDataException("HEAD chunk missing.");

    // Three sub-chunk references begin at HEAD+8; each is u32 marker + u32 offset (rel to HEAD+8).
    var refBase = headOffset + 8;
    var info1Off = refBase + (int)BinaryPrimitives.ReadUInt32BigEndian(data[(refBase + 4)..]);
    var info3Off = refBase + (int)BinaryPrimitives.ReadUInt32BigEndian(data[(refBase + 20)..]);

    var info = ReadStreamInfo(data, info1Off);
    var coefs = ReadChannelCoefs(data, info3Off, refBase, info.NumChannels);

    var pcm = Decode(data, info, coefs);
    return new ParsedBrstm(info, coefs, pcm);
  }

  private static StreamInfo ReadStreamInfo(ReadOnlySpan<byte> data, int o) {
    var codec = data[o];
    var loop = data[o + 1] != 0;
    var channels = data[o + 2];
    var sampleRate = BinaryPrimitives.ReadUInt16BigEndian(data[(o + 4)..]);
    var loopStart = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(o + 8)..]);
    var totalSamples = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(o + 12)..]);
    var dataOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(o + 16)..]);
    var numBlocks = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(o + 20)..]);
    var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(o + 24)..]);
    var samplesPerBlock = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(o + 28)..]);
    var finalBlockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(o + 32)..]);
    var finalBlockSamples = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(o + 36)..]);
    var finalBlockSizePadded = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(o + 40)..]);

    return new StreamInfo(codec, loop, channels, sampleRate, loopStart, totalSamples,
      dataOffset, numBlocks, blockSize, samplesPerBlock, finalBlockSize, finalBlockSamples,
      finalBlockSizePadded);
  }

  // Sub-chunk 3: u8 numChannels, then per channel a (marker + offset) ref pair, each pointing
  // to a 0x38-byte channel-info block whose first 0x20 bytes are the 16 BE coefficients.
  private static short[][] ReadChannelCoefs(ReadOnlySpan<byte> data, int o, int refBase, int channels) {
    var coefs = new short[channels][];
    // numChannels at o (1 byte); ref pairs start at o + 4 (padded).
    var refPairBase = o + 4;
    for (var c = 0; c < channels; ++c) {
      var entryOff = refBase + (int)BinaryPrimitives.ReadUInt32BigEndian(data[(refPairBase + c * 8 + 4)..]);
      // entryOff points to a (marker + coefOffset) pair; the coef table is at coefOffset.
      var coefOff = refBase + (int)BinaryPrimitives.ReadUInt32BigEndian(data[(entryOff + 4)..]);
      var table = new short[16];
      for (var i = 0; i < 16; ++i)
        table[i] = BinaryPrimitives.ReadInt16BigEndian(data[(coefOff + i * 2)..]);
      coefs[c] = table;
    }
    return coefs;
  }

  private static short[][] Decode(ReadOnlySpan<byte> data, StreamInfo info, short[][] coefs) {
    var channels = info.NumChannels;

    // De-interleave the blocks into one contiguous per-channel coded buffer first. DSP-ADPCM
    // history runs continuously across blocks, so the channel must be decoded as a single
    // stream — not block-by-block — to keep hist1/hist2 consistent at block boundaries.
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
      case 1: { // PCM16 big-endian
        var pcm = new short[info.TotalSamples];
        for (var i = 0; i < info.TotalSamples; ++i)
          pcm[i] = BinaryPrimitives.ReadInt16BigEndian(coded.AsSpan(i * 2));
        return pcm;
      }
      case 0: { // PCM8 (signed)
        var pcm = new short[info.TotalSamples];
        for (var i = 0; i < info.TotalSamples; ++i)
          pcm[i] = (short)((sbyte)coded[i] << 8);
        return pcm;
      }
      default:
        throw new InvalidDataException($"Unsupported BRSTM coding type {info.Codec}.");
    }
  }

  private sealed class MemoryStreams(int count) : IDisposable {
    private readonly MemoryStream[] _streams = Enumerable.Range(0, count).Select(_ => new MemoryStream()).ToArray();
    public void Write(int channel, ReadOnlySpan<byte> data) => _streams[channel].Write(data);
    public byte[] ToArray(int channel) => _streams[channel].ToArray();
    public void Dispose() { foreach (var s in _streams) s.Dispose(); }
  }
}
