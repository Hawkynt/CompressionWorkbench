#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.WsAdpcm;

/// <summary>
/// Westwood Studios "WS" ADPCM (a.k.a. SND1), the compression carried by Westwood
/// <c>.aud</c> streams in Command &amp; Conquer-era games. The codec works entirely in
/// the 8-bit unsigned sample domain; callers convert the bytes to 16-bit PCM as
/// <c>(sample - 128) &lt;&lt; 8</c>.
/// <para>
/// A WS stream is a sequence of chunks. The container supplies, per chunk, the number
/// of compressed input bytes and the number of decompressed output bytes. When the two
/// are equal the chunk is a raw 8-bit copy; otherwise it is a stream of commands. Each
/// command byte's top two bits select a mode and its low six bits carry a count:
/// <list type="bullet">
///   <item><b>0</b> — four 2-bit deltas follow packed in one byte, scaled by
///     <c>2 &lt;&lt; shift</c> where <c>shift = count</c>, each looked up in the 4-entry
///     table <c>{-2,-1,0,1}</c>;</item>
///   <item><b>1</b> — <c>(count + 1)</c> bytes follow, each holding two 4-bit deltas
///     (low nibble first), scaled by <c>2 &lt;&lt; shift</c> via the 16-entry WS table;</item>
///   <item><b>2</b> — if bit 5 of the byte is set the low five bits are a signed delta
///     applied to the current sample; otherwise <c>(count + 1)</c> raw bytes follow and
///     are copied verbatim;</item>
///   <item><b>3</b> — repeat (hold) the current sample <c>(count + 1)</c> times.</item>
/// </list>
/// The running sample is clamped to <c>[0, 255]</c> after every update.
/// </para>
/// This implementation is decode-only; Westwood <c>.aud</c> files are authored through
/// the IMA path instead (see <c>FileFormat.Aud</c>).
/// </summary>
public static class WsAdpcmCodec {

  // 2-bit delta table (mode 0).
  private static readonly int[] WsTable2Bit = [-2, -1, 0, 1];

  // 4-bit delta table (mode 1).
  private static readonly int[] WsTable4Bit = [-9, -8, -6, -5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 8];

  /// <summary>
  /// Decodes one WS-ADPCM chunk payload into <paramref name="expectedOut"/> bytes of
  /// 8-bit unsigned PCM. <paramref name="chunkPayload"/> is the compressed body only
  /// (the container's per-chunk size header is already consumed). When the payload
  /// length equals <paramref name="expectedOut"/> the chunk is a verbatim 8-bit copy.
  /// </summary>
  public static byte[] Decode(ReadOnlySpan<byte> chunkPayload, int expectedOut) {
    if (expectedOut < 0)
      throw new ArgumentOutOfRangeException(nameof(expectedOut));

    var output = new byte[expectedOut];

    // Raw chunk: a straight copy, no command decoding.
    if (chunkPayload.Length == expectedOut) {
      chunkPayload.CopyTo(output);
      return output;
    }

    var sample = 0x80; // WS streams start centred at 128 (silence).
    var inPos = 0;
    var outPos = 0;

    while (outPos < expectedOut && inPos < chunkPayload.Length) {
      var command = chunkPayload[inPos++];
      var mode = command >> 6;
      var count = command & 0x3F;

      switch (mode) {
        case 0: {
          // count = shift for the 2-bit deltas; one following byte packs four deltas.
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
          // (count + 1) bytes, each two 4-bit deltas (low nibble first).
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
            // Small signed delta carried in the low five bits (sign-extended).
            var delta = (command & 0x1F);
            if ((delta & 0x10) != 0) delta -= 0x20;
            sample = Clamp(sample + delta);
            output[outPos++] = (byte)sample;
          } else {
            // (count + 1) raw bytes copied verbatim; the last becomes the new sample.
            var bytes = count + 1;
            for (var b = 0; b < bytes && inPos < chunkPayload.Length && outPos < expectedOut; ++b) {
              sample = chunkPayload[inPos++];
              output[outPos++] = (byte)sample;
            }
          }
          break;
        }
        default: { // mode 3: hold the current sample (count + 1) times.
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
