#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// E-AC-3 coupling / spectral-extension band-structure decode (ATSC A/52 Annex E §E.1.3.1.3, FFmpeg
/// <c>decode_band_structure</c>). A run of 12-sample sub-bands is grouped into coding bands: a
/// per-boundary "merge" bit (or the default banding) joins a sub-band to the previous band. The
/// result is the number of bands and each band's size (a multiple of 12), plus the sub-band → band
/// index map used to look up coupling coordinates.
/// </summary>
public static class Ac3EnhancedBandStructure {

  /// <summary>The decoded banding: band count, per-band sizes and the sub-band → band index map.</summary>
  public readonly record struct Result(int NumBands, int[] BandSizes, int[] SubbandToBand);

  /// <summary>
  /// Decodes the band structure for <paramref name="numSubbands"/> sub-bands given the explicit merge
  /// bits in <paramref name="mergeBits"/> (length ≥ numSubbands-1). <c>mergeBits[i]</c> set joins
  /// sub-band <c>i+1</c> into the current band; cleared starts a new band. Sub-band size is 12
  /// transform bins (mirrors FFmpeg <c>decode_band_structure</c> with <c>ecpl=0</c>).
  /// </summary>
  public static Result Decode(int numSubbands, ReadOnlySpan<byte> mergeBits) {
    var bandSizes = new int[numSubbands < 1 ? 1 : numSubbands];
    var subbandToBand = new int[numSubbands < 1 ? 1 : numSubbands];
    if (numSubbands <= 0)
      return new Result(0, bandSizes, subbandToBand);

    var band = 0;
    bandSizes[0] = 12;
    subbandToBand[0] = 0;
    for (var sb = 1; sb < numSubbands; ++sb) {
      if (mergeBits[sb - 1] != 0) {
        bandSizes[band] += 12;                  // merge sub-band into the current band
      } else {
        bandSizes[++band] = 12;                 // start a new band
      }
      subbandToBand[sb] = band;
    }
    return new Result(band + 1, bandSizes, subbandToBand);
  }
}
