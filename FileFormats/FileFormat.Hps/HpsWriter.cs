#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Hps;

/// <summary>
/// Writes a big-endian GameCube <c>.hps</c> (HAL HALPST) carrying DSP-ADPCM, laid out per the public
/// HALPST specification so it round-trips through <see cref="HpsReader"/>. Channels are DSP-ADPCM
/// encoded independently (see <c>Codec.DspAdpcm</c>); the coded data is emitted as a single block
/// whose <c>nextBlockOffset</c> is <c>0xFFFFFFFF</c> (end of list). Per-channel decoder-state records
/// are zeroed (the encoder starts each channel from silence, matching the decoder's initial history).
/// <para>
/// SIMPLIFICATION (see <see cref="HpsReader"/>): <c>dspDataLength</c> is written as the per-channel
/// coded length and the whole stream is a single block. The header, channel-header and block-record
/// layout otherwise follow the documented format; <c>endAddress</c> encodes the sample count so the
/// reader recovers it exactly.
/// </para>
/// </summary>
public sealed class HpsWriter {

  private const int SamplesPerFrame = Codec.DspAdpcm.DspAdpcmCodec.SamplesPerFrame; // 14
  private const int BytesPerFrame = Codec.DspAdpcm.DspAdpcmCodec.BytesPerFrame;      // 8
  private const int ChannelHeaderSize = 0x38;

  private static readonly byte[] MagicBytes = " HALPST\0"u8.ToArray();

  /// <summary>
  /// Encodes per-channel mono PCM16 to DSP-ADPCM and serialises a complete HPS. All channels must
  /// share the same sample count.
  /// </summary>
  public byte[] Write(IReadOnlyList<short[]> channels, int sampleRate, int loopStart = 0) {
    if (channels.Count == 0)
      throw new ArgumentException("HPS needs at least one channel.", nameof(channels));
    var sampleCount = channels[0].Length;
    if (channels.Any(c => c.Length != sampleCount))
      throw new ArgumentException("All channels must have the same sample count.");

    var numChannels = channels.Count;
    var encoded = new byte[numChannels][];
    var coefs = new short[numChannels][];
    for (var c = 0; c < numChannels; ++c) {
      var (adpcm, table) = Codec.DspAdpcm.DspAdpcmCodec.Encode(channels[c]);
      encoded[c] = adpcm;
      coefs[c] = table;
    }

    // Coded data length per channel (all equal for equal sample counts).
    var frames = sampleCount == 0 ? 0 : (sampleCount + SamplesPerFrame - 1) / SamplesPerFrame;
    var dspLen = frames * BytesPerFrame;
    for (var c = 0; c < numChannels; ++c)
      if (encoded[c].Length < dspLen)
        Array.Resize(ref encoded[c], dspLen);

    var endAddress = SamplesToEndNibbleAddress(sampleCount);

    using var ms = new MemoryStream();
    // Header.
    var header = new byte[0x10];
    MagicBytes.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)sampleRate);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)numChannels);
    ms.Write(header);

    // Per-channel headers.
    for (var c = 0; c < numChannels; ++c) {
      var ch = new byte[ChannelHeaderSize];
      BinaryPrimitives.WriteUInt32BigEndian(ch.AsSpan(0), (uint)dspLen);          // maxBlockSize
      BinaryPrimitives.WriteUInt32BigEndian(ch.AsSpan(4), (uint)loopStart);       // loopStart
      BinaryPrimitives.WriteUInt32BigEndian(ch.AsSpan(8), (uint)endAddress);      // endAddress (nibble)
      BinaryPrimitives.WriteUInt32BigEndian(ch.AsSpan(12), 2u);                   // curAddress (first sample)
      for (var i = 0; i < 16; ++i)
        BinaryPrimitives.WriteInt16BigEndian(ch.AsSpan(16 + i * 2), coefs[c][i]);
      // gain / initial ps / hist1 / hist2 / pad all zero.
      ms.Write(ch);
    }

    // Single block: dspDataLength | pad | nextBlockOffset(-1) | per-channel state | per-channel data.
    var block = new byte[12 + numChannels * 8];
    BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(0), (uint)dspLen);
    BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4), 0u);                   // pad
    BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(8), 0xFFFFFFFF);           // end of list
    // Per-channel decoder state (zeroed — starts from silence).
    ms.Write(block);
    for (var c = 0; c < numChannels; ++c)
      ms.Write(encoded[c], 0, dspLen);

    return ms.ToArray();
  }

  // Inverse of HpsReader.NibbleAddressToSamples: nibble address of the LAST sample nibble.
  private static int SamplesToEndNibbleAddress(int sampleCount) {
    if (sampleCount == 0) return 0;
    var fullFrames = sampleCount / SamplesPerFrame;
    var rem = sampleCount % SamplesPerFrame;
    var nibbles = fullFrames * 16 + (rem > 0 ? rem + 2 : 0);
    return nibbles - 1;
  }
}
