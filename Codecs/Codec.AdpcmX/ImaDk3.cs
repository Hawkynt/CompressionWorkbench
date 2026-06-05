#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.AdpcmX;

/// <summary>
/// Duck DK3 IMA ADPCM (ffmpeg <c>adpcm_ima_dk3</c>, the audio inside Duck/On2 AVI clips).
/// The stream is always stereo but is carried as two coupled IMA channels — a "sum" and a
/// "diff" predictor — interleaved nibble-by-nibble. The left/right output is recovered as
/// <c>(sum + diff)</c> / <c>(sum - diff)</c>.
/// <para>
/// Each block begins with a 16-byte header: a 10-byte preamble (skipped), then the two
/// little-endian 16-bit start predictors (sum, diff) and the two step indices (sum, diff).
/// The remaining bytes are decoded a nibble at a time, low nibble of a byte first. The
/// pattern is: sum-nibble, diff-nibble (emit one stereo pair), sum-nibble (emit a second
/// pair reusing the previous diff), repeat. All expands use the DK shift of 3.
/// </para>
/// </summary>
public static class ImaDk3 {

  /// <summary>Bytes of per-block header before the nibble payload.</summary>
  public const int HeaderSize = 16;

  /// <summary>
  /// Decodes one DK3 block (header + nibbles) into interleaved stereo PCM16 (L,R,L,R…).
  /// <paramref name="block"/> must include the 16-byte header. A trailing odd nibble is
  /// ignored.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> block) {
    if (block.Length < HeaderSize)
      throw new ArgumentException("DK3 block shorter than its 16-byte header.", nameof(block));

    var sumPred = (int)(short)BinaryPrimitives.ReadUInt16LittleEndian(block[10..]);
    var diffPred = (int)(short)BinaryPrimitives.ReadUInt16LittleEndian(block[12..]);
    var sumIndex = (int)block[14];
    var diffIndex = (int)block[15];
    if (sumIndex > 88) sumIndex = 88;
    if (diffIndex > 88) diffIndex = 88;

    // Expand the payload nibbles (low nibble of each byte first) into a flat list.
    var payload = block[HeaderSize..];
    var nibbles = new int[payload.Length * 2];
    for (var i = 0; i < payload.Length; ++i) {
      nibbles[i * 2] = payload[i] & 0x0F;
      nibbles[i * 2 + 1] = payload[i] >> 4;
    }

    var output = new List<short>();
    var n = 0;
    // Each iteration consumes 3 nibbles (sum, diff, sum) and emits two stereo pairs.
    while (n + 2 < nibbles.Length) {
      ImaCore.Expand(nibbles[n++], ref sumPred, ref sumIndex, 3);
      ImaCore.Expand(nibbles[n++], ref diffPred, ref diffIndex, 3);
      output.Add((short)ImaCore.Clamp16(sumPred + diffPred));
      output.Add((short)ImaCore.Clamp16(sumPred - diffPred));

      ImaCore.Expand(nibbles[n++], ref sumPred, ref sumIndex, 3);
      output.Add((short)ImaCore.Clamp16(sumPred + diffPred));
      output.Add((short)ImaCore.Clamp16(sumPred - diffPred));
    }

    return [.. output];
  }
}
