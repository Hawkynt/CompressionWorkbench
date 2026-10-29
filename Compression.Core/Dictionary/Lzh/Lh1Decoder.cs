namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes LHarc <c>-lh1-</c>: LZSS over a 4 KiB window with an adaptive Huffman tree for literals and
/// copy lengths, and a fixed code for the high six bits of the copy distance.
/// </summary>
/// <remarks>
/// See <see cref="Lh1Tree"/> for the tree and the position code. The window starts filled with spaces.
/// A copy of length <c>n</c> (codes 256-313 → 3-60) starts <c>offset + 1</c> bytes back.
/// Sourcing: converted from lhasa's <c>lh1_decoder.c</c> (ISC, Simon Howard); see
/// THIRD-PARTY-NOTICE.lhasa.txt beside this file. Checked against the <c>-lh1-</c> archives of lhasa's
/// test suite.
/// </remarks>
public sealed class Lh1Decoder {
  internal const int WindowSize = 4096;
  private readonly byte[] _data;

  /// <summary>Initializes a new <see cref="Lh1Decoder"/> over the rest of <paramref name="input"/>.</summary>
  public Lh1Decoder(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    this._data = ms.ToArray();
  }

  /// <summary>Decodes <paramref name="originalSize"/> bytes.</summary>
  public byte[] Decode(int originalSize) {
    ArgumentOutOfRangeException.ThrowIfNegative(originalSize);
    var output = new byte[originalSize];
    var tree = new Lh1Tree();
    var bits = new LhaBitInput(this._data);
    var ring = new byte[WindowSize];
    Array.Fill(ring, (byte)' ');
    var ringPos = 0;
    var outPos = 0;

    while (outPos < originalSize) {
      if (bits.Exhausted)
        throw new InvalidDataException("-lh1- stream ends before the member is complete.");
      var node = 0;
      while (!tree.IsLeaf(node))
        node = tree.Child(node) - bits.ReadBit();
      var code = tree.Child(node);
      tree.Increment(code);

      if (code < 0x100) {
        output[outPos++] = ring[ringPos] = (byte)code;
        ringPos = (ringPos + 1) % WindowSize;
        continue;
      }

      int high = tree.OffsetLookup[bits.PeekBits(8)];
      bits.Skip(tree.OffsetLengths[high]);
      var offset = (high << 6) | bits.ReadBits(6);
      var count = code - 0x100 + Lh1Tree.CopyThreshold;
      var start = ringPos - offset + WindowSize - 1;
      for (var i = 0; i < count && outPos < originalSize; ++i) {
        var b = ring[(start + i) % WindowSize];
        output[outPos++] = ring[ringPos] = b;
        ringPos = (ringPos + 1) % WindowSize;
      }
    }
    return output;
  }
}
