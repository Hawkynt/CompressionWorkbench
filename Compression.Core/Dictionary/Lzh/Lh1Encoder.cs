using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Encodes LHarc <c>-lh1-</c> (see <see cref="Lh1Decoder"/>): greedy LZSS matches over a 4 KiB window,
/// coded through the same adaptive <see cref="Lh1Tree"/> the decoder evolves.
/// </summary>
/// <remarks>
/// A code is written as the path from the root to its leaf. Matches reach back only into bytes already
/// emitted. One zero byte is appended, because LHA decoders look ahead eight bits for each position
/// code. Verified by lhasa extracting what this writes.
/// </remarks>
public sealed class Lh1Encoder {
  private const int MaxLength = Lh1Tree.NumCodes - 0x100 + Lh1Tree.CopyThreshold - 1;  // 60

  /// <summary>Encodes <paramref name="data"/>.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    var input = data.ToArray();
    using var ms = new MemoryStream();
    var bits = new BitWriter<MsbBitOrder>(ms);
    var tree = new Lh1Tree();
    var finder = new LhaMatchFinder(input, Lh1Decoder.WindowSize, Lh1Tree.CopyThreshold, MaxLength);
    var path = new Stack<int>(32);

    void WriteCode(int code) {
      for (int node = tree.LeafOf(code); node != 0;) {
        var parent = tree.Parent(node);
        path.Push(tree.Child(parent) - node);
        node = parent;
      }
      while (path.Count > 0)
        bits.WriteBit(path.Pop());
      tree.Increment(code);
    }

    var pos = 0;
    while (pos < input.Length) {
      var (length, distance) = finder.Find(pos);
      if (length == 0) {
        WriteCode(input[pos++]);
        continue;
      }
      WriteCode(0x100 + length - Lh1Tree.CopyThreshold);
      var offset = distance - 1;
      var high = offset >> 6;
      int codeLength = tree.OffsetLengths[high];
      bits.WriteBits((uint)(tree.OffsetCodes[high] >> (8 - codeLength)), codeLength);
      bits.WriteBits((uint)(offset & 0x3F), 6);
      pos += length;
    }
    bits.FlushBits();
    if (input.Length > 0)
      ms.WriteByte(0);
    return ms.ToArray();
  }
}
