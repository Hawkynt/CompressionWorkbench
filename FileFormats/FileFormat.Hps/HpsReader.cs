#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Hps;

/// <summary>
/// Parses a big-endian GameCube <c>.hps</c> (HAL HALPST) stream into its header, per-channel
/// DSP-ADPCM coefficient tables and decoded PCM.
/// <para>
/// Layout: the 8-byte magic <c>" HALPST\0"</c> | <c>u32 sampleRate</c> | <c>u32 channelCount</c>,
/// followed by a <c>0x38</c>-byte channel header per channel:
/// <c>u32 maxBlockSize</c> | <c>u32 loopStart</c> | <c>u32 endAddress</c> | <c>u32 curAddress</c> |
/// 16 <c>s16</c> coefficients | <c>u16 gain</c> | <c>u16 initialPs</c> | <c>s16 hist1</c> |
/// <c>s16 hist2</c> | <c>u16 pad</c>. The audio is then a linked list of blocks, each:
/// <c>u32 dspDataLength</c> (coded bytes PER CHANNEL) | <c>u32 pad</c> | <c>u32 nextBlockOffset</c>
/// (file-relative; <c>0xFFFFFFFF</c> = end) | per channel an 8-byte decoder-state record
/// (<c>u16 ps</c> | <c>s16 hist1</c> | <c>s16 hist2</c> | <c>u16 pad</c>) | then per channel
/// <c>dspDataLength</c> coded bytes back-to-back.
/// </para>
/// <para>
/// SIMPLIFICATION: <c>dspDataLength</c> is treated as the per-channel coded length and the writer's
/// block layout (state records for every channel, then all channels' coded payloads) is the round-trip
/// bar; the documented header/block structure is otherwise followed. DSP-ADPCM history runs
/// continuously across blocks per channel.
/// </para>
/// </summary>
public sealed class HpsReader {

  public sealed record Header(
    int SampleRate,
    int NumChannels,
    int SampleCount);

  public sealed record ParsedHps(
    Header Info,
    short[][] Coefs,
    short[][] Pcm);

  private static readonly byte[] MagicBytes = " HALPST\0"u8.ToArray();

  private const int ChannelHeaderSize = 0x38;

  public ParsedHps Read(ReadOnlySpan<byte> data) {
    if (data.Length < 0x10)
      throw new InvalidDataException("HPS too short for header.");
    for (var i = 0; i < 8; ++i)
      if (data[i] != MagicBytes[i])
        throw new InvalidDataException("Missing HALPST magic.");

    var sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
    var channels = (int)BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
    if (channels < 1 || channels > 64)
      throw new InvalidDataException($"Implausible HPS channel count {channels}.");

    var coefs = new short[channels][];
    var headerBase = 0x10;
    var sampleCount = 0;
    for (var c = 0; c < channels; ++c) {
      var o = headerBase + c * ChannelHeaderSize;
      if (o + ChannelHeaderSize > data.Length)
        throw new InvalidDataException("HPS channel header runs past end of file.");
      // endAddress is the last nibble address (0-based) into the channel's coded stream.
      var endAddress = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(o + 8)..]);
      // Nibble address → sample count: each sample is one nibble, minus the per-frame headers.
      var thisCount = NibbleAddressToSamples(endAddress + 1);
      if (thisCount > sampleCount) sampleCount = thisCount;
      var table = new short[16];
      for (var i = 0; i < 16; ++i)
        table[i] = BinaryPrimitives.ReadInt16BigEndian(data[(o + 16 + i * 2)..]);
      coefs[c] = table;
    }

    var blockBase = headerBase + channels * ChannelHeaderSize;
    var perChannelCoded = DeinterleaveBlocks(data, blockBase, channels);

    var pcm = new short[channels][];
    for (var c = 0; c < channels; ++c)
      pcm[c] = Codec.DspAdpcm.DspAdpcmCodec.Decode(perChannelCoded[c], coefs[c], sampleCount);
    return new ParsedHps(new Header(sampleRate, channels, sampleCount), coefs, pcm);
  }

  // Each 8-byte ADPCM frame carries 1 header byte + 14 sample-nibbles; an address counts nibbles
  // including the 2-nibble frame header. samples = nibbles / 16 * 14 (+ remainder beyond header).
  private static int NibbleAddressToSamples(int nibbles) {
    var frames = nibbles / 16;
    var rem = nibbles % 16;
    var samples = frames * 14;
    if (rem > 2) samples += rem - 2;
    return samples;
  }

  private static byte[][] DeinterleaveBlocks(ReadOnlySpan<byte> data, int start, int channels) {
    var streams = Enumerable.Range(0, channels).Select(_ => new MemoryStream()).ToArray();
    try {
      var pos = start;
      var guard = 0;
      while (pos >= 0 && pos + 12 <= data.Length) {
        if (++guard > 1_000_000) break; // defensive against malformed cycles
        var dspLen = (int)BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
        var next = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 8)..]);

        var stateBase = pos + 12;
        var payloadBase = stateBase + channels * 8;
        for (var c = 0; c < channels; ++c) {
          var chStart = payloadBase + c * dspLen;
          if (chStart + dspLen > data.Length)
            break;
          streams[c].Write(data.Slice(chStart, dspLen));
        }

        if (next == -1 || (uint)next == 0xFFFFFFFF)
          break;
        pos = next;
      }
      return streams.Select(s => s.ToArray()).ToArray();
    } finally {
      foreach (var s in streams) s.Dispose();
    }
  }
}
