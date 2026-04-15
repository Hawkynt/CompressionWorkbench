using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes Dynamic Markov Compression as a benchmarkable building block.
/// Bit-level finite-context modeling with state cloning: a finite automaton
/// where each state predicts the next bit. States that become too frequent
/// are cloned to create higher-order contexts, improving prediction.
/// Uses an arithmetic coder for the output bitstream.
/// </summary>
public sealed class DmcBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Dmc";
  /// <inheritdoc/>
  public string DisplayName => "DMC";
  /// <inheritdoc/>
  public string Description => "Dynamic Markov Compression, bit-level FSM with state cloning";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  // Binary tree: nodes 1..255 (internal), 256..511 (leaves for each byte value).
  private const int InitialStates = 512;
  private const int MaxStates = 1 << 18;     // 256K states max
  private const int CloneThreshold = 128;     // Min count before cloning
  private const uint Top = 1u << 24;
  private const uint Bottom = 1u << 16;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write 4-byte LE uncompressed size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var next0 = new int[MaxStates];
    var next1 = new int[MaxStates];
    var count0 = new int[MaxStates];
    var count1 = new int[MaxStates];
    var stateCount = InitializeModel(next0, next1, count0, count1);

    // Arithmetic encoder state.
    var low = 0u;
    var range = uint.MaxValue;
    var state = 1; // Root of binary tree.

    for (var i = 0; i < data.Length; i++) {
      var b = data[i];
      for (var bit = 7; bit >= 0; bit--) {
        var bitVal = (b >> bit) & 1;
        var total = count0[state] + count1[state];
        var p0 = (uint)((long)range * count0[state] / total);
        if (p0 < 1) p0 = 1;
        if (p0 >= range) p0 = range - 1;

        if (bitVal == 0) {
          range = p0;
          count0[state]++;
        } else {
          low += p0;
          range -= p0;
          count1[state]++;
        }

        // Cloning: if this transition is popular enough, split the target state.
        if (stateCount < MaxStates) {
          var targetCount = bitVal == 0 ? count0[state] : count1[state];
          if (targetCount >= CloneThreshold) {
            var target = bitVal == 0 ? next0[state] : next1[state];
            var targetTotal = count0[target] + count1[target];
            if (targetTotal > targetCount + 2) {
              // Clone the target state.
              var clone = stateCount++;
              next0[clone] = next0[target];
              next1[clone] = next1[target];

              // Split counts proportionally.
              var ratio = (double)targetCount / targetTotal;
              count0[clone] = Math.Max(1, (int)(count0[target] * ratio));
              count1[clone] = Math.Max(1, (int)(count1[target] * ratio));
              count0[target] = Math.Max(1, count0[target] - count0[clone] + 1);
              count1[target] = Math.Max(1, count1[target] - count1[clone] + 1);

              // Redirect this transition to the clone.
              if (bitVal == 0)
                next0[state] = clone;
              else
                next1[state] = clone;
            }
          }
        }

        // Transition to next state.
        state = bitVal == 0 ? next0[state] : next1[state];

        // Carryless normalization.
        while (true) {
          if ((low ^ (low + range)) >= Top) {
            if (range >= Bottom) break;
            range = ((uint)(-(int)low)) & (Bottom - 1);
          }
          ms.WriteByte((byte)(low >> 24));
          low <<= 8;
          range <<= 8;
        }
      }
    }

    // Flush encoder.
    ms.WriteByte((byte)(low >> 24)); low <<= 8;
    ms.WriteByte((byte)(low >> 24)); low <<= 8;
    ms.WriteByte((byte)(low >> 24)); low <<= 8;
    ms.WriteByte((byte)(low >> 24));

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var src = data[4..];
    var next0 = new int[MaxStates];
    var next1 = new int[MaxStates];
    var count0 = new int[MaxStates];
    var count1 = new int[MaxStates];
    var stateCount = InitializeModel(next0, next1, count0, count1);

    // Arithmetic decoder state.
    var low = 0u;
    var range = uint.MaxValue;
    var code = 0u;
    var srcPos = 0;

    // Prime the code register.
    for (var i = 0; i < 4 && srcPos < src.Length; i++)
      code = (code << 8) | src[srcPos++];

    var result = new byte[originalSize];
    var state = 1; // Root of binary tree.

    for (var i = 0; i < originalSize; i++) {
      var b = 0;
      for (var bit = 7; bit >= 0; bit--) {
        var total = count0[state] + count1[state];
        var p0 = (uint)((long)range * count0[state] / total);
        if (p0 < 1) p0 = 1;
        if (p0 >= range) p0 = range - 1;

        int bitVal;
        if (code - low < p0) {
          bitVal = 0;
          range = p0;
          count0[state]++;
        } else {
          bitVal = 1;
          low += p0;
          range -= p0;
          count1[state]++;
        }

        // Cloning (must mirror encoder exactly).
        if (stateCount < MaxStates) {
          var targetCount = bitVal == 0 ? count0[state] : count1[state];
          if (targetCount >= CloneThreshold) {
            var target = bitVal == 0 ? next0[state] : next1[state];
            var targetTotal = count0[target] + count1[target];
            if (targetTotal > targetCount + 2) {
              var clone = stateCount++;
              next0[clone] = next0[target];
              next1[clone] = next1[target];

              var ratio = (double)targetCount / targetTotal;
              count0[clone] = Math.Max(1, (int)(count0[target] * ratio));
              count1[clone] = Math.Max(1, (int)(count1[target] * ratio));
              count0[target] = Math.Max(1, count0[target] - count0[clone] + 1);
              count1[target] = Math.Max(1, count1[target] - count1[clone] + 1);

              if (bitVal == 0)
                next0[state] = clone;
              else
                next1[state] = clone;
            }
          }
        }

        state = bitVal == 0 ? next0[state] : next1[state];

        b |= (bitVal << bit);

        // Carryless normalization (must match encoder exactly).
        while (true) {
          if ((low ^ (low + range)) >= Top) {
            if (range >= Bottom) break;
            range = ((uint)(-(int)low)) & (Bottom - 1);
          }
          code = (code << 8) | (srcPos < src.Length ? src[srcPos++] : 0u);
          low <<= 8;
          range <<= 8;
        }
      }
      result[i] = (byte)b;
    }

    return result;
  }

  private static int InitializeModel(int[] next0, int[] next1, int[] count0, int[] count1) {
    // Complete binary tree: state 1 is root, state k → left child 2k, right child 2k+1.
    // Internal nodes: 1..255. Leaves: 256..511 (leaf 256+b represents byte value b).
    // After reaching a leaf, transition back to root (state 1) for order-0 context.
    // Cloning will dynamically build higher-order contexts.
    var stateCount = InitialStates;

    for (var s = 1; s < InitialStates; s++) {
      count0[s] = 1;
      count1[s] = 1;

      if (s < 256) {
        // Internal node: children are 2s and 2s+1.
        next0[s] = 2 * s;
        next1[s] = 2 * s + 1;
      } else {
        // Leaf node: go back to root (order-0).
        next0[s] = 1;
        next1[s] = 1;
      }
    }

    // State 0 is unused; set defaults so accidental access is safe.
    count0[0] = 1;
    count1[0] = 1;
    next0[0] = 1;
    next1[0] = 1;

    return stateCount;
  }
}
