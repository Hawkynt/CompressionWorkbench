#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.DspAdpcm;

namespace FileFormat.Bwav;

/// <summary>
/// Parses a Nintendo Switch <c>.bwav</c> stream into per-channel decoded PCM16. The file is
/// little-endian: a fixed header (magic, BOM, version, crc, prefetch flag, channel count) is
/// followed by one 0x4C-byte channel-info block per channel. Each channel's coded data is stored
/// contiguously (NOT interleaved) at its <c>absoluteStart</c> offset. Two codecs are recognised:
/// <c>0</c> = PCM16 little-endian, <c>1</c> = DSP-ADPCM (decoded with the channel's 16 coefficients).
/// </summary>
public sealed class BwavReader {

    /// <summary>
  /// Represents a channel info.
  /// </summary>
public sealed record ChannelInfo(
    int Codec,
    int ChannelPan,
    int SampleRate,
    int SampleCount,
    short[] Coefs,            // [16]
    int AbsoluteStart,
    bool IsLooping,
    int LoopEnd,
    int LoopStart,
    int InitialPredictorScale,
    short Hist1,
    short Hist2);

    /// <summary>
  /// Represents a parsed bwav.
  /// </summary>
public sealed record ParsedBwav(
    int Version,
    uint Crc,
    int ChannelCount,
    IReadOnlyList<ChannelInfo> Channels,
    short[][] Pcm);           // [channel][sampleCount]

    /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public ParsedBwav Read(ReadOnlySpan<byte> data) {
    if (data.Length < 0x10)
      throw new InvalidDataException("BWAV too short for header.");
    if (data[0] != 'B' || data[1] != 'W' || data[2] != 'A' || data[3] != 'V')
      throw new InvalidDataException("Missing BWAV magic.");
    var bom = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
    if (bom != 0xFEFF)
      throw new InvalidDataException("Only little-endian BWAV (BOM 0xFEFF) is supported.");

    var version = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
    var crc = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
    // prefetch at 0x0C (u16), channelCount at 0x0E (u16).
    var channelCount = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0E..]);
    if (channelCount is < 1 or > 64)
      throw new InvalidDataException($"Implausible BWAV channel count {channelCount}.");

    const int channelInfoSize = 0x4C;
    var channels = new ChannelInfo[channelCount];
    var pcm = new short[channelCount][];

    for (var c = 0; c < channelCount; ++c) {
      var o = 0x10 + c * channelInfoSize;
      if (o + channelInfoSize > data.Length)
        throw new InvalidDataException("BWAV channel info out of range.");

      var codec = BinaryPrimitives.ReadUInt16LittleEndian(data[o..]);
      var pan = BinaryPrimitives.ReadUInt16LittleEndian(data[(o + 2)..]);
      var sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 4)..]);
      // nonPrefetch sampleCount at +8, full sampleCount at +0x0C.
      var sampleCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 0x0C)..]);
      var coefs = new short[16];
      for (var i = 0; i < 16; ++i)
        coefs[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(o + 0x10 + i * 2)..]);
      // nonPrefetch absoluteStart at +0x30, full absoluteStart at +0x34.
      var absoluteStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 0x34)..]);
      var isLooping = BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 0x38)..]) != 0;
      var loopEnd = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 0x3C)..]);
      var loopStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 0x40)..]);
      var initialPredictorScale = BinaryPrimitives.ReadUInt16LittleEndian(data[(o + 0x44)..]);
      var hist1 = BinaryPrimitives.ReadInt16LittleEndian(data[(o + 0x46)..]);
      var hist2 = BinaryPrimitives.ReadInt16LittleEndian(data[(o + 0x48)..]);

      var info = new ChannelInfo(codec, pan, sampleRate, sampleCount, coefs, absoluteStart,
        isLooping, loopEnd, loopStart, initialPredictorScale, hist1, hist2);
      channels[c] = info;
      pcm[c] = DecodeChannel(data, info);
    }

    return new ParsedBwav(version, crc, channelCount, channels, pcm);
  }

  private static short[] DecodeChannel(ReadOnlySpan<byte> data, ChannelInfo info) {
    switch (info.Codec) {
      case 1: { // DSP-ADPCM. Frame headers carry the per-frame scale; history starts at 0 for a
                // freshly-written stream and the standard frame walk reconstructs the rest.
        var byteCount = (info.SampleCount + DspAdpcmCodec.SamplesPerFrame - 1)
                        / DspAdpcmCodec.SamplesPerFrame * DspAdpcmCodec.BytesPerFrame;
        var end = Math.Min(info.AbsoluteStart + byteCount, data.Length);
        if (info.AbsoluteStart < 0 || info.AbsoluteStart > end)
          throw new InvalidDataException("BWAV channel data out of range.");
        var coded = data[info.AbsoluteStart..end];
        return DspAdpcmCodec.Decode(coded, info.Coefs, info.SampleCount);
      }
      case 0: { // PCM16 little-endian.
        var end = Math.Min(info.AbsoluteStart + info.SampleCount * 2, data.Length);
        if (info.AbsoluteStart < 0 || info.AbsoluteStart > end)
          throw new InvalidDataException("BWAV channel data out of range.");
        var available = (end - info.AbsoluteStart) / 2;
        var n = Math.Min(available, info.SampleCount);
        var pcm = new short[info.SampleCount];
        for (var i = 0; i < n; ++i)
          pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(info.AbsoluteStart + i * 2)..]);
        return pcm;
      }
      default:
        throw new InvalidDataException($"Unsupported BWAV codec {info.Codec}.");
    }
  }
}
