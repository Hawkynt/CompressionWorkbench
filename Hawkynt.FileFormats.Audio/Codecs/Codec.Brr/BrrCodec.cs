#pragma warning disable CS1591
namespace Codec.Brr;

/// <summary>
/// Nintendo SNES S-DSP BRR (Bit Rate Reduction) encoder and decoder. The S-DSP stores
/// audio in fixed 9-byte blocks, each yielding 16 mono PCM samples:
/// <list type="bullet">
///   <item>byte 0 — high nibble = range (0..15), bits 2..3 = filter (0..3),
///     bit 1 = loop flag, bit 0 = end flag.</item>
///   <item>bytes 1..8 — sixteen signed 4-bit nibbles, high nibble first.</item>
/// </list>
/// Internally the predictor runs in the S-DSP's signed 15-bit domain. Public PCM samples are
/// that reconstructed value multiplied by two, matching the sample values produced by BRRtools.
/// Arithmetic right shifts are intentional: replacing them with integer division changes negative
/// predictor histories.
/// </summary>
public static class BrrCodec {

  /// <summary>Size in bytes of one BRR block (1 header byte + 8 data bytes).</summary>
  public const int BlockSize = 9;

  /// <summary>Number of PCM samples carried by one BRR block.</summary>
  public const int SamplesPerBlock = 16;

  /// <summary>Highest legal range value. Ranges 13..15 use the S-DSP invalid-range path.</summary>
  public const int MaxRange = 12;

  /// <summary>
  /// Decodes a BRR stream to full-scale 16-bit PCM. Decoding stops after the first block whose
  /// end flag is set, or when the input runs out of whole 9-byte blocks. A trailing partial block
  /// is ignored and predictor history starts at zero.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> blocks) {
    var blockCount = blocks.Length / BlockSize;
    if (blockCount == 0)
      return [];

    var output = new short[blockCount * SamplesPerBlock];
    var produced = 0;
    var hist1 = 0;
    var hist2 = 0;

    for (var blockIndex = 0; blockIndex < blockCount; ++blockIndex) {
      var offset = blockIndex * BlockSize;
      var header = blocks[offset];
      var range = header >> 4;
      var filter = (header >> 2) & 0x03;

      for (var sampleIndex = 0; sampleIndex < SamplesPerBlock; ++sampleIndex) {
        var packed = blocks[offset + 1 + (sampleIndex >> 1)];
        var nibble = (sampleIndex & 1) == 0 ? packed >> 4 : packed & 0x0F;
        var signedNibble = SignExtend4(nibble);
        var reconstructed = DecodeInternalSample(signedNibble, range, filter, ref hist1, ref hist2);
        output[produced++] = (short)(reconstructed << 1);
      }

      if ((header & 0x01) != 0)
        break;
    }

    return produced == output.Length ? output : output[..produced];
  }

  /// <summary>
  /// Encodes mono 16-bit PCM into BRR blocks. The stream uses BRRtools-compatible framing:
  /// a partial first group is zero-padded at the beginning and, when that first aligned group is
  /// non-zero, a silent predictor-primer block is emitted before the audio. The final data block
  /// carries the end flag.
  /// </summary>
  /// <remarks>
  /// Block selection is independent of BRRtools' encoder implementation. Every legal range/filter
  /// pair is evaluated against the exact decoder path, and each 4-bit code is selected from all
  /// sixteen possibilities by minimum squared reconstruction error. This keeps encoder and decoder
  /// interoperability grounded in the wire format rather than in a shared inverse approximation.
  /// </remarks>
  public static byte[] Encode(ReadOnlySpan<short> pcm) {
    if (pcm.IsEmpty)
      return [];

    var leadingPadding = (SamplesPerBlock - pcm.Length % SamplesPerBlock) % SamplesPerBlock;
    var dataBlockCount = (pcm.Length + leadingPadding) / SamplesPerBlock;
    var hasPrimer = NeedsPrimer(pcm, leadingPadding);
    var output = new byte[(dataBlockCount + (hasPrimer ? 1 : 0)) * BlockSize];
    var outputOffset = hasPrimer ? BlockSize : 0;

    var hist1 = 0;
    var hist2 = 0;

    Span<short> source = stackalloc short[SamplesPerBlock];
    Span<byte> bestNibbles = stackalloc byte[SamplesPerBlock];
    Span<byte> trialNibbles = stackalloc byte[SamplesPerBlock];

    for (var blockIndex = 0; blockIndex < dataBlockCount; ++blockIndex) {
      for (var sampleIndex = 0; sampleIndex < SamplesPerBlock; ++sampleIndex)
        source[sampleIndex] = GetAlignedSample(pcm, blockIndex * SamplesPerBlock + sampleIndex, leadingPadding);

      var bestError = long.MaxValue;
      var bestRange = 0;
      var bestFilter = 0;
      var bestHist1 = hist1;
      var bestHist2 = hist2;

      for (var range = 0; range <= MaxRange; ++range) {
        for (var filter = 0; filter < 4; ++filter) {
          var error = EncodeCandidate(
            source,
            range,
            filter,
            hist1,
            hist2,
            bestError,
            trialNibbles,
            out var trialHist1,
            out var trialHist2
          );

          if (error >= bestError)
            continue;

          bestError = error;
          bestRange = range;
          bestFilter = filter;
          bestHist1 = trialHist1;
          bestHist2 = trialHist2;
          trialNibbles.CopyTo(bestNibbles);
        }
      }

      var header = (bestRange << 4) | (bestFilter << 2);
      if (blockIndex == dataBlockCount - 1)
        header |= 0x01;
      output[outputOffset] = (byte)header;

      for (var sampleIndex = 0; sampleIndex < SamplesPerBlock; sampleIndex += 2)
        output[outputOffset + 1 + (sampleIndex >> 1)] =
          (byte)((bestNibbles[sampleIndex] << 4) | bestNibbles[sampleIndex + 1]);

      outputOffset += BlockSize;
      hist1 = bestHist1;
      hist2 = bestHist2;
    }

    return output;
  }

  private static long EncodeCandidate(
    ReadOnlySpan<short> source,
    int range,
    int filter,
    int initialHist1,
    int initialHist2,
    long abortAt,
    Span<byte> nibbles,
    out int finalHist1,
    out int finalHist2
  ) {
    var hist1 = initialHist1;
    var hist2 = initialHist2;
    long totalError = 0;

    for (var sampleIndex = 0; sampleIndex < SamplesPerBlock; ++sampleIndex) {
      var bestSampleError = long.MaxValue;
      var bestNibble = 0;
      var bestHist1 = hist1;
      var bestHist2 = hist2;

      for (var signedNibble = -8; signedNibble <= 7; ++signedNibble) {
        var trialHist1 = hist1;
        var trialHist2 = hist2;
        var reconstructed = DecodeInternalSample(
          signedNibble,
          range,
          filter,
          ref trialHist1,
          ref trialHist2
        ) << 1;

        var difference = (long)reconstructed - source[sampleIndex];
        var sampleError = difference * difference;
        if (sampleError >= bestSampleError)
          continue;

        bestSampleError = sampleError;
        bestNibble = signedNibble & 0x0F;
        bestHist1 = trialHist1;
        bestHist2 = trialHist2;
      }

      nibbles[sampleIndex] = (byte)bestNibble;
      hist1 = bestHist1;
      hist2 = bestHist2;
      totalError += bestSampleError;

      if (totalError >= abortAt)
        break;
    }

    finalHist1 = hist1;
    finalHist2 = hist2;
    return totalError;
  }

  private static int DecodeInternalSample(
    int signedNibble,
    int range,
    int filter,
    ref int hist1,
    ref int hist2
  ) {
    var value = range <= MaxRange
      ? (signedNibble << range) >> 1
      : signedNibble >= 0 ? 2048 : -2048;

    value += Predict(filter, hist1, hist2);
    value = Clamp16(value);

    if (value > 0x3FFF)
      value -= 0x8000;
    else if (value < -0x4000)
      value += 0x8000;

    hist2 = hist1;
    hist1 = value;
    return value;
  }

  private static int Predict(int filter, int hist1, int hist2) => filter switch {
    1 => hist1 - (hist1 >> 4),
    2 => (hist1 << 1)
      + (-(hist1 + (hist1 << 1)) >> 5)
      - hist2
      + (hist2 >> 4),
    3 => (hist1 << 1)
      + (-(hist1 + (hist1 << 2) + (hist1 << 3)) >> 6)
      - hist2
      + ((hist2 + (hist2 << 1)) >> 4),
    _ => 0,
  };

  private static bool NeedsPrimer(ReadOnlySpan<short> pcm, int leadingPadding) {
    for (var sampleIndex = 0; sampleIndex < SamplesPerBlock; ++sampleIndex)
      if (GetAlignedSample(pcm, sampleIndex, leadingPadding) != 0)
        return true;

    return false;
  }

  private static short GetAlignedSample(ReadOnlySpan<short> pcm, int alignedIndex, int leadingPadding) {
    var sourceIndex = alignedIndex - leadingPadding;
    return (uint)sourceIndex < (uint)pcm.Length ? pcm[sourceIndex] : (short)0;
  }

  private static int SignExtend4(int nibble) => (nibble & 0x08) != 0 ? nibble - 16 : nibble;

  private static int Clamp16(int value) => Math.Clamp(value, short.MinValue, short.MaxValue);
}
