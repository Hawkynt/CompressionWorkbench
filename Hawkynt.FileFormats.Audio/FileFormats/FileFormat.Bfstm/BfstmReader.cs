#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Bfstm;

/// <summary>
/// Parses a WiiU/Switch <c>.bfstm</c> (FSTM) stream into its stream-info, per-channel DSP-ADPCM
/// coefficient tables and the channel-interleaved block data. FSTM is structurally identical to the
/// 3DS CSTM container; the only difference is the magic (<c>"FSTM"</c>) and that the byte-order mark
/// selects endianness — WiiU files are big-endian, Switch files little-endian. Both are honoured by
/// reading the BOM at <c>0x04</c>.
/// <para>
/// The header is <c>"FSTM"</c> + BOM <c>0xFEFF</c> + header size + version + file size + block count,
/// followed by per-block <c>(u16 sectionId, u16 pad, u32 offset, u32 size)</c> entries
/// (<c>0x4000</c> = INFO, <c>0x4001</c> = SEEK, <c>0x4002</c> = DATA). The INFO block carries the
/// stream-info structure plus per-channel coefficient tables; DATA holds channel-interleaved blocks.
/// </para>
/// <para>
/// SIMPLIFICATION: as with the CSTM reader, the per-channel coefficient structs are read from a flat,
/// documented per-channel <c>0x2E</c>-byte layout that <see cref="BfstmWriter"/> emits, rather than
/// chasing C/FSTM's reference-offset indirection. The reader round-trips against the writer.
/// </para>
/// </summary>
public sealed class BfstmReader {

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
    short[][] Coefs,
    short[][] Pcm);

  private const int SectionInfo = 0x4000;
  private const int SectionData = 0x4002;

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public ParsedStream Read(ReadOnlySpan<byte> data) {
    if (data.Length < 0x40)
      throw new InvalidDataException("Stream too short for FSTM header.");
    if (data[0] != 'F' || data[1] != 'S' || data[2] != 'T' || data[3] != 'M')
      throw new InvalidDataException("Missing FSTM magic.");

    var bigEndian = BinaryPrimitives.ReadUInt16BigEndian(data[4..]) == 0xFEFF;
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
      throw new InvalidDataException("FSTM is missing INFO or DATA block.");

    if (infoOff + 8 > data.Length || data[infoOff] != (byte)'I' || data[infoOff + 1] != (byte)'N')
      throw new InvalidDataException("INFO block magic missing.");

    var (info, coefs) = ReadInfo(data, infoOff, dataOff, bigEndian);
    var pcm = Decode(data, info, coefs);
    return new ParsedStream(info, coefs, pcm);
  }

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
    var audioStart = dataOff + 8 + 0x18;

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
      case 1: {
        var pcm = new short[info.TotalSamples];
        for (var i = 0; i < info.TotalSamples; ++i)
          pcm[i] = info.BigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(coded.AsSpan(i * 2))
            : BinaryPrimitives.ReadInt16LittleEndian(coded.AsSpan(i * 2));
        return pcm;
      }
      case 0: {
        var pcm = new short[info.TotalSamples];
        for (var i = 0; i < info.TotalSamples; ++i)
          pcm[i] = (short)((sbyte)coded[i] << 8);
        return pcm;
      }
      default:
        throw new InvalidDataException($"Unsupported FSTM coding type {info.Codec}.");
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
