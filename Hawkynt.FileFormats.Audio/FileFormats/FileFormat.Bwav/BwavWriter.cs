#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.DspAdpcm;

namespace FileFormat.Bwav;

/// <summary>
/// Builds a Nintendo Switch <c>.bwav</c> from one or more mono PCM16 channels, DSP-ADPCM-encoding
/// each channel independently and laying its coded data out contiguously (non-interleaved) after
/// the channel-info table. The header CRC is written as <c>0</c> (the field is informational for
/// playback) — see the metadata note surfaced by the descriptor. The result round-trips through
/// <see cref="BwavReader"/>.
/// </summary>
public sealed class BwavWriter {

  private const int ChannelInfoSize = 0x4C;

  /// <summary>Writes a non-looping DSP-ADPCM BWAV from the given mono channels.</summary>
  public byte[] Write(IReadOnlyList<short[]> channels, int sampleRate) {
    ArgumentNullException.ThrowIfNull(channels);
    if (channels.Count == 0)
      throw new ArgumentException("BWAV needs at least one channel.", nameof(channels));

    var sampleCount = channels[0].Length;
    foreach (var ch in channels)
      if (ch.Length != sampleCount)
        throw new ArgumentException("All BWAV channels must share the same sample count.");

    // Encode each channel up front so we know its coded length and coefficient table.
    var coded = new byte[channels.Count][];
    var coefs = new short[channels.Count][];
    var initialScale = new int[channels.Count];
    for (var c = 0; c < channels.Count; ++c) {
      var (adpcm, table) = DspAdpcmCodec.Encode(channels[c]);
      coded[c] = adpcm;
      coefs[c] = table;
      initialScale[c] = adpcm.Length > 0 ? adpcm[0] & 0x0F : 0;
    }

    var headerSize = 0x10 + channels.Count * ChannelInfoSize;
    var dataStarts = new int[channels.Count];
    var pos = headerSize;
    for (var c = 0; c < channels.Count; ++c) {
      dataStarts[c] = pos;
      pos += coded[c].Length;
    }

    var buffer = new byte[pos];
    var s = buffer.AsSpan();

    "BWAV"u8.CopyTo(s);
    BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 0xFEFF); // BOM
    BinaryPrimitives.WriteUInt16LittleEndian(s[6..], 2);      // version
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0);      // crc (informational; left zero)
    BinaryPrimitives.WriteUInt16LittleEndian(s[0x0C..], 0);   // prefetch flag
    BinaryPrimitives.WriteUInt16LittleEndian(s[0x0E..], (ushort)channels.Count);

    for (var c = 0; c < channels.Count; ++c) {
      var o = 0x10 + c * ChannelInfoSize;
      BinaryPrimitives.WriteUInt16LittleEndian(s[o..], 1);                    // codec = DSP-ADPCM
      BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 2)..], (ushort)c);      // channelPan
      BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 4)..], (uint)sampleRate);
      BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 8)..], (uint)sampleCount);  // nonPrefetch sampleCount
      BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 0x0C)..], (uint)sampleCount);
      for (var i = 0; i < 16; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(s[(o + 0x10 + i * 2)..], coefs[c][i]);
      BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 0x30)..], (uint)dataStarts[c]); // nonPrefetch start
      BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 0x34)..], (uint)dataStarts[c]);
      BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 0x38)..], 0);           // isLooping
      BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 0x3C)..], (uint)sampleCount); // loopEnd
      BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 0x40)..], 0);           // loopStart
      BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 0x44)..], (ushort)initialScale[c]);
      BinaryPrimitives.WriteInt16LittleEndian(s[(o + 0x46)..], 0);            // hist1
      BinaryPrimitives.WriteInt16LittleEndian(s[(o + 0x48)..], 0);            // hist2
      // 0x4A..0x4B: pad (left zero)

      coded[c].CopyTo(s[dataStarts[c]..]);
    }

    return buffer;
  }
}
