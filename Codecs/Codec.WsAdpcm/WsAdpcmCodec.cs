#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.WsAdpcm;

/// <summary>
/// Westwood Studios "WS" ADPCM (a.k.a. SND1), the compression carried by Westwood
/// <c>.aud</c> streams in Command &amp; Conquer-era games. The codec operates in the
/// unsigned 8-bit sample domain and supports both command-coded and raw chunks.
/// </summary>
public static class WsAdpcmCodec {

  private static readonly int[] WsTable2Bit = [-2, -1, 0, 1];
  private static readonly int[] WsTable4Bit = [-9, -8, -6, -5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 8];

  /// <summary>
  /// Decodes one WS-ADPCM chunk payload into <paramref name="expectedOut"/> bytes of
  /// 8-bit unsigned PCM. When payload length equals output length the chunk is raw.
  /// </summary>
  public static byte[] Decode(ReadOnlySpan<byte> chunkPayload, int expectedOut) {
    if (expectedOut < 0)
      throw new ArgumentOutOfRangeException(nameof(expectedOut));

    var output = new byte[expectedOut];
    if (chunkPayload.Length == expectedOut) {
      chunkPayload.CopyTo(output);
      return output;
    }

    var sample = 0x80;
    var inPos = 0;
    var outPos = 0;

    while (outPos < expectedOut && inPos < chunkPayload.Length) {
      var command = chunkPayload[inPos++];
      var mode = command >> 6;
      var count = command & 0x3F;

      switch (mode) {
        case 0: {
          if (inPos >= chunkPayload.Length) break;
          var packed = chunkPayload[inPos++];
          for (var i = 0; i < 4 && outPos < expectedOut; ++i) {
            var code = (packed >> (i * 2)) & 0x03;
            sample = Clamp(sample + (WsTable2Bit[code] << count));
            output[outPos++] = (byte)sample;
          }
          break;
        }
        case 1: {
          var bytes = count + 1;
          for (var b = 0; b < bytes && inPos < chunkPayload.Length && outPos < expectedOut; ++b) {
            var packed = chunkPayload[inPos++];
            sample = Clamp(sample + WsTable4Bit[packed & 0x0F]);
            output[outPos++] = (byte)sample;
            if (outPos >= expectedOut) break;
            sample = Clamp(sample + WsTable4Bit[packed >> 4]);
            output[outPos++] = (byte)sample;
          }
          break;
        }
        case 2: {
          if ((command & 0x20) != 0) {
            var delta = command & 0x1F;
            if ((delta & 0x10) != 0) delta -= 0x20;
            sample = Clamp(sample + delta);
            output[outPos++] = (byte)sample;
          } else {
            var bytes = count + 1;
            for (var b = 0; b < bytes && inPos < chunkPayload.Length && outPos < expectedOut; ++b) {
              sample = chunkPayload[inPos++];
              output[outPos++] = (byte)sample;
            }
          }
          break;
        }
        default: {
          var repeats = count + 1;
          for (var r = 0; r < repeats && outPos < expectedOut; ++r)
            output[outPos++] = (byte)sample;
          break;
        }
      }
    }

    return output;
  }

  /// <summary>
  /// Encodes unsigned 8-bit PCM as a lossless WS chunk. The writer uses hold commands
  /// for repeated predictor values, one-byte signed-delta commands for changes in
  /// [-16,+15], and literal runs for everything else. If commands do not beat the raw
  /// representation, the raw bytes are returned because raw chunks are part of the format.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<byte> pcmU8) {
    if (pcmU8.IsEmpty)
      return [];

    using var encoded = new MemoryStream(pcmU8.Length);
    var sample = 0x80;
    var pos = 0;

    while (pos < pcmU8.Length) {
      if (pcmU8[pos] == sample) {
        var run = 1;
        while (run < 64 && pos + run < pcmU8.Length && pcmU8[pos + run] == sample)
          ++run;
        encoded.WriteByte((byte)(0xC0 | (run - 1)));
        pos += run;
        continue;
      }

      var delta = pcmU8[pos] - sample;
      if (delta is >= -16 and <= 15) {
        encoded.WriteByte((byte)(0xA0 | (delta & 0x1F)));
        sample = pcmU8[pos++];
        continue;
      }

      var start = pos;
      var count = 0;
      while (count < 32 && pos < pcmU8.Length) {
        // Stop before a value that can be represented by a cheaper one-byte command
        // relative to the last literal in this run (or the incoming predictor).
        var predecessor = count == 0 ? sample : pcmU8[pos - 1];
        var nextDelta = pcmU8[pos] - predecessor;
        if (count > 0 && (pcmU8[pos] == predecessor || nextDelta is >= -16 and <= 15))
          break;
        ++count;
        ++pos;
      }
      if (count == 0) {
        count = 1;
        ++pos;
      }

      encoded.WriteByte((byte)(0x80 | (count - 1)));
      encoded.Write(pcmU8.Slice(start, count));
      sample = pcmU8[start + count - 1];
    }

    var commands = encoded.ToArray();
    return commands.Length < pcmU8.Length ? commands : pcmU8.ToArray();
  }

  /// <summary>Encodes signed PCM16 after reducing it to WS's native unsigned-8 domain.</summary>
  public static byte[] EncodePcm16(ReadOnlySpan<short> pcm16) {
    var pcmU8 = new byte[pcm16.Length];
    for (var i = 0; i < pcm16.Length; ++i)
      pcmU8[i] = (byte)((pcm16[i] >> 8) + 128);
    return Encode(pcmU8);
  }

  /// <summary>
  /// Converts a buffer of decoded 8-bit unsigned WS samples to signed 16-bit PCM via
  /// <c>(sample - 128) &lt;&lt; 8</c>.
  /// </summary>
  public static short[] ToPcm16(ReadOnlySpan<byte> unsigned8) {
    var pcm = new short[unsigned8.Length];
    for (var i = 0; i < unsigned8.Length; ++i)
      pcm[i] = (short)((unsigned8[i] - 128) << 8);
    return pcm;
  }

  /// <summary>Packs signed 16-bit samples into a little-endian byte buffer.</summary>
  public static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;
}
