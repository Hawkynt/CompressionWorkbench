#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.AdpcmX;

namespace FileFormat.Ast;

/// <summary>
/// Parses a big-endian GameCube/Wii <c>.ast</c> (STRM) stream into its header and per-channel PCM.
/// <para>
/// The 64-byte header is <c>"STRM"</c> | <c>u32 dataSize</c> | <c>u16 codec</c>
/// (<c>0</c> = ADPCM-AFC, <c>1</c> = PCM16 big-endian) | <c>u16 bitDepth</c> | <c>u16 channels</c> |
/// <c>u16 loopFlag</c> | <c>u32 sampleRate</c> | <c>u32 sampleCount</c> | <c>u32 loopStart</c> |
/// <c>u32 loopEnd</c> | <c>u32 firstBlockSize</c> | reserved. The audio follows as a sequence of
/// <c>"BLCK"</c> blocks: <c>"BLCK"</c> | <c>u32 blockSize</c> (per channel) | 24 reserved bytes |
/// then each channel's <c>blockSize</c> bytes back-to-back (channel-interleaved at block granularity).
/// </para>
/// <para>
/// Codec 1 (PCM16BE) is decoded fully to little-endian PCM. Codec 0 (AFC ADPCM) is decoded via
/// <see cref="Codec.AdpcmX.Thp.DecodeAfc"/>: each channel's BLCK bytes are concatenated and run
/// through the fixed-table AFC decoder (9-byte frames → 16 samples each), capped at the header's
/// sample count.
/// </para>
/// </summary>
public sealed class AstReader {

  /// <summary>
  /// Represents a header.
  /// </summary>
  public sealed record Header(
    int Codec,
    int BitDepth,
    int NumChannels,
    bool Loop,
    int SampleRate,
    int SampleCount,
    int LoopStart,
    int LoopEnd);

  /// <summary>
  /// Represents a parsed ast.
  /// </summary>
  public sealed record ParsedAst(
    Header Info,
    short[][] Pcm);          // [channel][sampleCount]; empty for undecoded codecs.

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public ParsedAst Read(ReadOnlySpan<byte> data) {
    if (data.Length < 0x40)
      throw new InvalidDataException("AST too short for STRM header.");
    if (data[0] != 'S' || data[1] != 'T' || data[2] != 'R' || data[3] != 'M')
      throw new InvalidDataException("Missing STRM magic.");

    var codec = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
    var bitDepth = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
    var channels = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
    var loopFlag = BinaryPrimitives.ReadUInt16BigEndian(data[14..]);
    var sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
    var sampleCount = (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
    var loopStart = (int)BinaryPrimitives.ReadUInt32BigEndian(data[24..]);
    var loopEnd = (int)BinaryPrimitives.ReadUInt32BigEndian(data[28..]);

    if (channels < 1)
      throw new InvalidDataException("AST has no channels.");

    var info = new Header(codec, bitDepth, channels, loopFlag != 0, sampleRate, sampleCount,
      loopStart, loopEnd);

    var pcm = codec switch {
      1 => DecodePcm16(data, info),
      0 => DecodeAfc(data, info),
      _ => [],   // unknown coding: descriptor falls back to FULL-only.
    };
    return new ParsedAst(info, pcm);
  }

  // AFC ADPCM: gather each channel's BLCK bytes, then decode the per-channel stream with the
  // fixed AFC coefficient table. AFC frames are 9 bytes (16 samples); decoding is stateful within
  // a channel, so the channel's blocks are concatenated before a single decode.
  private static short[][] DecodeAfc(ReadOnlySpan<byte> data, Header info) {
    var channels = info.NumChannels;
    var channelBytes = new List<byte>[channels];
    for (var c = 0; c < channels; ++c)
      channelBytes[c] = [];

    var pos = 0x40;
    while (pos + 32 <= data.Length) {
      if (data[pos] != 'B' || data[pos + 1] != 'L' || data[pos + 2] != 'C' || data[pos + 3] != 'K')
        break;
      var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]);
      var payload = pos + 32;
      for (var c = 0; c < channels; ++c) {
        var chStart = payload + c * blockSize;
        if (chStart + blockSize > data.Length)
          break;
        channelBytes[c].AddRange(data.Slice(chStart, blockSize).ToArray());
      }
      pos = payload + channels * blockSize;
    }

    var pcm = new short[channels][];
    for (var c = 0; c < channels; ++c)
      pcm[c] = Thp.DecodeAfc(channelBytes[c].ToArray(), info.SampleCount);
    return pcm;
  }

  // PCM16BE: walk BLCK blocks, accumulate each channel's big-endian samples → little-endian shorts.
  private static short[][] DecodePcm16(ReadOnlySpan<byte> data, Header info) {
    var channels = info.NumChannels;
    var produced = new int[channels];
    var pcm = new short[channels][];
    for (var c = 0; c < channels; ++c)
      pcm[c] = new short[info.SampleCount];

    var pos = 0x40;
    while (pos + 32 <= data.Length) {
      if (data[pos] != 'B' || data[pos + 1] != 'L' || data[pos + 2] != 'C' || data[pos + 3] != 'K')
        break;
      var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]);
      var payload = pos + 32;
      for (var c = 0; c < channels; ++c) {
        var chStart = payload + c * blockSize;
        if (chStart + blockSize > data.Length)
          break;
        var samples = blockSize / 2;
        for (var i = 0; i < samples && produced[c] < info.SampleCount; ++i)
          pcm[c][produced[c]++] = BinaryPrimitives.ReadInt16BigEndian(data[(chStart + i * 2)..]);
      }
      pos = payload + channels * blockSize;
    }

    return pcm;
  }
}
