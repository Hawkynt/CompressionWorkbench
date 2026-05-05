#pragma warning disable CS1591

namespace Codec.Vorbis;

/// <summary>
/// Vorbis residue decoder for types 0, 1 and 2. The residue packet partitions
/// each channel's spectrum into fixed-size blocks, classifies them via a
/// single "classbook" pass, then walks 8 cascade passes over the chosen
/// per-class books to fill in VQ vectors. Type 2 first interleaves all
/// remaining channels into a single virtual stream.
/// </summary>
internal static class VorbisResidue {

  /// <summary>
  /// Decode <paramref name="residue"/> into the given <paramref name="output"/>
  /// per-channel buffers. <paramref name="doNotDecode"/> marks channels that
  /// should be skipped (their output stays at zero).
  /// </summary>
  public static void Decode(
    VorbisBitReader br,
    VorbisSetup.Residue residue,
    VorbisCodebook[] codebooks,
    float[][] output,
    bool[] doNotDecode,
    int n
  ) {
    var channels = output.Length;
    var actualSize = residue.Type == 2 ? n * channels : n;
    var partitionSize = residue.PartitionSize;
    var begin = residue.Begin;
    var end = residue.End;
    if (residue.Type == 2 && end > actualSize) end = actualSize;
    var partitionsToRead = (end - begin) / partitionSize;
    if (partitionsToRead <= 0) return;

    // For type 2 every channel-marker must agree.
    var allSilent = true;
    for (var c = 0; c < channels; ++c) if (!doNotDecode[c]) { allSilent = false; break; }
    if (allSilent) return;

    var classbook = codebooks[residue.Classbook];
    var classDim = classbook.Dimensions;
    var classifications = residue.Classifications;
    var classifyChannels = residue.Type == 2 ? 1 : channels;

    // classifications[channel][partitionIdx]
    var classes = new int[classifyChannels][];
    for (var c = 0; c < classifyChannels; ++c) classes[c] = new int[partitionsToRead + classDim];

    for (var pass = 0; pass < 8; ++pass) {
      var partitionCount = 0;
      while (partitionCount < partitionsToRead) {
        if (pass == 0) {
          for (var c = 0; c < classifyChannels; ++c) {
            if (residue.Type != 2 && doNotDecode[c]) continue;
            var temp = classbook.DecodeScalar(br);
            if (temp < 0) return;
            for (var k = classDim - 1; k >= 0; --k) {
              classes[c][partitionCount + k] = temp % classifications;
              temp /= classifications;
            }
          }
        }
        for (var k = 0; k < classDim && partitionCount < partitionsToRead; ++k, ++partitionCount) {
          for (var c = 0; c < classifyChannels; ++c) {
            if (residue.Type != 2 && doNotDecode[c]) continue;
            var clsVal = classes[c][partitionCount];
            var book = residue.Books[clsVal, pass];
            if (book < 0) continue;
            var dst = begin + partitionCount * partitionSize;
            switch (residue.Type) {
              case 0:
                DecodeType0(br, codebooks[book], output[c], dst, partitionSize);
                break;
              case 1:
                DecodeType1(br, codebooks[book], output[c], dst, partitionSize);
                break;
              case 2:
                DecodeType2(br, codebooks[book], output, dst, partitionSize, channels);
                break;
            }
          }
        }
      }
    }
  }

  private static void DecodeType0(VorbisBitReader br, VorbisCodebook cb, float[] target, int dst, int n) {
    var step = n / cb.Dimensions;
    Span<float> vec = stackalloc float[cb.Dimensions];
    for (var i = 0; i < step; ++i) {
      if (!cb.DecodeVector(br, vec)) return;
      for (var d = 0; d < cb.Dimensions; ++d)
        target[dst + i + d * step] += vec[d];
    }
  }

  private static void DecodeType1(VorbisBitReader br, VorbisCodebook cb, float[] target, int dst, int n) {
    Span<float> vec = stackalloc float[cb.Dimensions];
    var i = 0;
    while (i < n) {
      if (!cb.DecodeVector(br, vec)) return;
      for (var d = 0; d < cb.Dimensions; ++d)
        target[dst + i + d] += vec[d];
      i += cb.Dimensions;
    }
  }

  private static void DecodeType2(
    VorbisBitReader br, VorbisCodebook cb, float[][] target, int dst, int n, int channels
  ) {
    Span<float> vec = stackalloc float[cb.Dimensions];
    var i = 0;
    while (i < n) {
      if (!cb.DecodeVector(br, vec)) return;
      for (var d = 0; d < cb.Dimensions; ++d) {
        var pos = dst + i + d;
        var ch = pos % channels;
        var slot = pos / channels;
        target[ch][slot] += vec[d];
      }
      i += cb.Dimensions;
    }
  }
}
