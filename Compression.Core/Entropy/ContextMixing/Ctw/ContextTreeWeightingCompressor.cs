using System.Buffers.Binary;
using Compression.Core.Entropy.Arithmetic;

namespace Compression.Core.Entropy.ContextMixing.Ctw;

/// <summary>
/// A clean-room implementation of the Context Tree Weighting (CTW) method: a
/// bounded-depth binary context tree in which every node holds a
/// Krichevsky-Trofimov (KT) probability estimator, and the coding probability
/// of each node is the recursive equal-weight mixture of that node's own KT
/// estimate and the product of its two children's weighted probabilities.
/// The resulting per-bit probability drives the repository's existing binary
/// arithmetic coder (<see cref="ArithmeticEncoder"/> / <see cref="ArithmeticDecoder"/>).
/// </summary>
/// <remarks>
/// <para>
/// Implemented from the published description in Willems, Shtarkov &amp;
/// Tjalkens, "The Context-Tree Weighting Method: Basic Properties", IEEE
/// Transactions on Information Theory, vol. 41, no. 3, May 1995 — not ported
/// or paraphrased from any third-party source code.
/// </para>
/// <para>
/// <b>KT estimator.</b> At a node that has observed <c>a</c> zeros and
/// <c>b</c> ones, the KT-estimated probability of the next symbol follows the
/// standard recurrence <c>P_e(a+1,b) / P_e(a,b) = (a+0.5)/(a+b+1)</c> for a
/// zero and the symmetric form for a one, starting from <c>P_e(0,0)=1</c>.
/// </para>
/// <para>
/// <b>Recursive weighting.</b> For a node <c>s</c> at depth <c>d &lt; D</c>
/// with children <c>s0</c> and <c>s1</c> (reached by prepending the next,
/// deeper context bit), the weighted probability is
/// <c>P_w^s = (1/2) P_e^s + (1/2) P_w^{s0} P_w^{s1}</c> — an equal prior
/// between "this context is deep enough" and "split one level deeper". Nodes
/// at the maximum depth <c>D</c> have no children, so <c>P_w = P_e</c> there.
/// </para>
/// <para>
/// <b>Context and depth.</b> The context tree operates directly on the
/// message's bit sequence (MSB-first per byte): the context of a bit is the
/// preceding <see cref="ContextDepthBits"/> bits, so the root is the order-0
/// (no context) node and the deepest nodes correspond to a 16-bit (two-byte)
/// binary history. All orders from 0 to 16 are mixed simultaneously by the
/// recursion above, which is CTW's defining property — unlike a fixed-order
/// model, no single order is chosen ahead of time. History bits before the
/// start of the message are treated as zero (a fixed, deterministic
/// convention applied identically by encoder and decoder); this is a
/// practical boundary choice the 1995 paper leaves open, not part of the
/// estimator itself.
/// </para>
/// <para>
/// <b>Coding.</b> For each bit, the two hypothetical root weighted
/// probabilities (assuming the next bit is 0, and assuming it is 1) are
/// computed without mutating the tree; because the KT estimator and the CTW
/// mixture are both proper (probabilities over 0/1 continuations sum to the
/// parent's probability), <c>P(bit=1) = P_w^{root}(...1) / P_w^{root}(...)</c>
/// reduces to a direct ratio of the two hypothetical values. That probability
/// feeds <see cref="ArithmeticEncoder.EncodeBit"/> / <see cref="ArithmeticDecoder.DecodeBit"/>;
/// only afterwards is the tree updated with the actual bit.
/// </para>
/// <para>
/// The tree is sparse: nodes are created lazily along the single context
/// path actually visited by each bit, so memory stays proportional to the
/// number of distinct contexts encountered rather than to <c>2^D</c>.
/// </para>
/// </remarks>
public static class ContextTreeWeightingCompressor {
  /// <summary>
  /// Depth of the binary context tree, in bits. Sixteen bits mixes every
  /// order from 0 (no context) up to a two-byte binary history.
  /// </summary>
  public const int ContextDepthBits = 16;

  private const double LogHalf = -0.6931471805599453; // ln(0.5)

  /// <summary>
  /// Compresses data by driving a binary arithmetic coder with per-bit
  /// probabilities from a Context Tree Weighting model.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data: a 4-byte LE original-size header followed by the coded bitstream.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();

    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    output.Write(header);

    if (data.Length == 0)
      return output.ToArray();

    var encoder = new ArithmeticEncoder(output);
    var tree = new CtwTree(ContextDepthBits);

    foreach (var value in data)
      for (var bit = 7; bit >= 0; --bit) {
        var bitVal = (value >> bit) & 1;
        var p1 = tree.PredictProbabilityOfOne();
        var prob0 = ToProb0(p1);
        encoder.EncodeBit(bitVal, prob0);
        tree.Update(bitVal);
      }

    encoder.Finish();
    return output.ToArray();
  }

  /// <summary>
  /// Decompresses data previously produced by <see cref="Compress"/>.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <returns>The original data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    var size = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    if (size == 0)
      return [];

    using var input = new MemoryStream(compressed[4..].ToArray());
    var decoder = new ArithmeticDecoder(input);
    var tree = new CtwTree(ContextDepthBits);

    var result = new byte[size];
    for (var i = 0; i < size; ++i) {
      var value = 0;
      for (var bit = 7; bit >= 0; --bit) {
        var p1 = tree.PredictProbabilityOfOne();
        var prob0 = ToProb0(p1);
        var bitVal = decoder.DecodeBit(prob0);
        tree.Update(bitVal);
        value = (value << 1) | bitVal;
      }
      result[i] = (byte)value;
    }

    return result;
  }

  /// <summary>
  /// Converts a probability of the bit being 1 into the [1, 65535]-scaled
  /// probability-of-zero argument expected by <see cref="ArithmeticEncoder"/>/<see cref="ArithmeticDecoder"/>.
  /// </summary>
  private static int ToProb0(double p1) {
    var prob0 = (int)Math.Round((1.0 - p1) * 65536.0);
    return Math.Clamp(prob0, 1, 65535);
  }

  /// <summary>
  /// The binary context tree: a lazily-materialised set of <see cref="CtwNode"/>s
  /// reachable from a single root, plus the rolling bit history used to derive
  /// each bit's context path.
  /// </summary>
  private sealed class CtwTree {
    private readonly int _depth;
    private readonly ulong _historyMask;
    private readonly CtwNode _root;
    private readonly CtwNode[] _path;
    private readonly double[] _logPe0;
    private readonly double[] _logPe1;
    private readonly double[] _logPw0;
    private readonly double[] _logPw1;
    private ulong _history;

    public CtwTree(int depth) {
      this._depth = depth;
      this._historyMask = depth >= 64 ? ulong.MaxValue : (1UL << depth) - 1;
      this._root = new CtwNode();
      this._path = new CtwNode[depth + 1];
      this._logPe0 = new double[depth + 1];
      this._logPe1 = new double[depth + 1];
      this._logPw0 = new double[depth + 1];
      this._logPw1 = new double[depth + 1];
    }

    /// <summary>
    /// Walks the context path for the upcoming bit (creating nodes lazily),
    /// evaluates both hypothetical continuations without mutating the tree,
    /// and returns the resulting probability that the next bit is 1.
    /// </summary>
    public double PredictProbabilityOfOne() {
      // Phase 1: materialise the path root..leaf for the current context.
      this._path[0] = this._root;
      var cur = this._root;
      for (var level = 1; level <= this._depth; ++level) {
        // Context bit "level" is the level-th most recent history bit (1 = most recent).
        var contextBit = (int)((this._history >> (level - 1)) & 1UL);
        cur = contextBit == 0
          ? cur.Child0 ??= new CtwNode()
          : cur.Child1 ??= new CtwNode();
        this._path[level] = cur;
      }

      // Phase 2: per-node hypothetical KT increments (own counts only).
      for (var level = 0; level <= this._depth; ++level) {
        var node = this._path[level];
        var total = node.Count0 + node.Count1;
        this._logPe0[level] = node.LogPe + Math.Log((node.Count0 + 0.5) / (total + 1));
        this._logPe1[level] = node.LogPe + Math.Log((node.Count1 + 0.5) / (total + 1));
      }

      // Phase 3: bottom-up recursive weighting, reusing cached sibling Pw.
      this._logPw0[this._depth] = this._logPe0[this._depth];
      this._logPw1[this._depth] = this._logPe1[this._depth];
      for (var level = this._depth - 1; level >= 0; --level) {
        var node = this._path[level];
        var child = this._path[level + 1];
        var sibling = ReferenceEquals(child, node.Child0) ? node.Child1 : node.Child0;
        var siblingLogPw = sibling?.LogPw ?? 0.0;

        this._logPw0[level] = LogAddExp(this._logPe0[level] + LogHalf, this._logPw0[level + 1] + siblingLogPw + LogHalf);
        this._logPw1[level] = LogAddExp(this._logPe1[level] + LogHalf, this._logPw1[level + 1] + siblingLogPw + LogHalf);
      }

      var logPwRoot0 = this._logPw0[0];
      var logPwRoot1 = this._logPw1[0];
      return 1.0 / (1.0 + Math.Exp(logPwRoot0 - logPwRoot1));
    }

    /// <summary>
    /// Commits the actual observed bit: updates every node on the path most
    /// recently evaluated by <see cref="PredictProbabilityOfOne"/> and rolls
    /// the bit into the history used for the next context.
    /// </summary>
    public void Update(int bit) {
      for (var level = 0; level <= this._depth; ++level) {
        var node = this._path[level];
        if (bit == 0) {
          node.LogPe = this._logPe0[level];
          node.LogPw = this._logPw0[level];
          ++node.Count0;
        } else {
          node.LogPe = this._logPe1[level];
          node.LogPw = this._logPw1[level];
          ++node.Count1;
        }
      }

      this._history = ((this._history << 1) | (ulong)(uint)bit) & this._historyMask;
    }

    private static double LogAddExp(double a, double b) {
      if (a > b)
        return a + Math.Log(1.0 + Math.Exp(b - a));
      return b + Math.Log(1.0 + Math.Exp(a - b));
    }
  }

  /// <summary>
  /// A single node of the binary context tree: symbol counts, the cumulative
  /// KT log-probability from those counts alone, the cumulative recursively
  /// weighted log-probability, and the (lazily created) two children.
  /// </summary>
  private sealed class CtwNode {
    public int Count0;
    public int Count1;
    public double LogPe;
    public double LogPw;
    public CtwNode? Child0;
    public CtwNode? Child1;
  }
}
