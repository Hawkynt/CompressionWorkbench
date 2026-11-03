using System.Buffers.Binary;
using Compression.Core.Entropy.Huffman;
using Compression.Registry;

namespace Compression.Core.Entropy.UnixPack;

/// <summary>
/// Unix <c>pack</c> (<c>.z</c>, magic <c>1F 1E</c>): one static Huffman code over the
/// whole file, from System V and still decoded by GNU gzip.
/// </summary>
/// <remarks>
/// <para>Layout (big-endian, bits MSB first):</para>
/// <list type="bullet">
///   <item><c>1F 1E</c>, then the original length as a 32-bit number.</item>
///   <item>The longest code length <c>L</c> (1–24), then one byte per length 1…L with the
///     number of leaves of that length. The byte for length <c>L</c> is stored minus two:
///     it counts the end-of-file code, which is never transmitted, and is offset so 257
///     leaves fit.</item>
///   <item>The leaf bytes, shortest codes first, omitting the end-of-file leaf.</item>
///   <item>The coded data, closed by the end-of-file code.</item>
/// </list>
/// <para>
/// Codes are assigned level by level from the root: at each length the internal nodes
/// take the smallest code values and the leaves the values above them, in the order the
/// leaf bytes are listed; the end-of-file code is the largest value at length <c>L</c>.
/// So the shortest code is all ones.
/// </para>
/// <para>
/// Written from the layout described in the Wikipedia article "Pack (compression)" and
/// measured against GNU gzip, which decodes packed files (<c>gzip -d</c>); gzip's
/// <c>unpack.c</c> (GPL) was read as a description, not converted.
/// </para>
/// </remarks>
public sealed class UnixPackBuildingBlock : IBuildingBlock {

  /// <inheritdoc/>
  public string Id => "BB_UnixPack";
  /// <inheritdoc/>
  public string DisplayName => "Unix pack";
  /// <inheritdoc/>
  public string Description => "System V pack (.z): static Huffman over the whole file with an implicit end-of-file code";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Classic;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => Encode(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => Decode(data);

  /// <summary>The two magic bytes.</summary>
  public static ReadOnlySpan<byte> Magic => [0x1F, 0x1E];

  /// <summary>Longest code length the original <c>pack</c> emits.</summary>
  private const int MaxCodeLength = 24;
  private const int EofSymbol = 256;

  // ── decoder ─────────────────────────────────────────────────────────

  /// <summary>Decodes a complete pack file.</summary>
  /// <exception cref="InvalidDataException">Bad magic, a malformed tree, a code out of range,
  /// truncated data, or a length that disagrees with the header.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    if (data.Length < 7 || data[0] != 0x1F || data[1] != 0x1E)
      throw new InvalidDataException("pack: missing 1F 1E magic or truncated header.");
    var origLen = BinaryPrimitives.ReadUInt32BigEndian(data[2..]);
    int maxLen = data[6];
    if (maxLen is < 1 or > 25)
      throw new InvalidDataException($"pack: code length {maxLen} out of range.");
    var pos = 7;
    if (data.Length < pos + maxLen)
      throw new InvalidDataException("pack: truncated leaf counts.");

    Span<int> leaves = stackalloc int[26];
    var total = 0;
    var available = 1; // nodes still open at the current level
    for (var len = 1; len <= maxLen; ++len) {
      leaves[len] = data[pos++];
      var stored = leaves[len] + (len == maxLen ? 2 : 0);
      available *= 2;
      if (stored > available)
        throw new InvalidDataException("pack: too many leaves in the Huffman tree.");
      available -= stored;
      total += stored;
    }
    if (total > 257)
      throw new InvalidDataException("pack: too many leaves in the Huffman tree.");
    leaves[maxLen] += 2;

    var explicitLeaves = total - 1;
    if (data.Length < pos + explicitLeaves)
      throw new InvalidDataException("pack: truncated leaf table.");
    var literals = data.Slice(pos, explicitLeaves).ToArray();
    pos += explicitLeaves;

    // Internal nodes per level, counted from the deepest level upwards.
    Span<int> parents = stackalloc int[26];
    Span<int> leafBase = stackalloc int[26];
    var nodes = 0;
    for (var len = maxLen; len >= 1; --len) {
      nodes >>= 1;
      parents[len] = nodes;
      nodes += leaves[len];
    }
    var b = 0;
    for (var len = 1; len <= maxLen; ++len) {
      leafBase[len] = b;
      b += leaves[len];
    }
    var eobCode = leaves[maxLen] - 1; // parents[maxLen] is 0

    var output = new byte[origLen];
    var outPos = 0;
    var bitPos = (long)pos * 8;
    var bitEnd = (long)data.Length * 8;
    while (true) {
      var code = 0;
      var len = 0;
      do {
        if (bitPos >= bitEnd)
          throw new InvalidDataException("pack: data ends before the end-of-file code.");
        code = (code << 1) | ((data[(int)(bitPos >> 3)] >> (7 - (int)(bitPos & 7))) & 1);
        ++bitPos;
        if (++len > maxLen)
          throw new InvalidDataException("pack: code longer than the tree.");
      } while (code < parents[len]);

      var leaf = code - parents[len];
      if (leaf >= leaves[len])
        throw new InvalidDataException("pack: code out of range.");
      if (len == maxLen && code == eobCode)
        break;
      if (outPos >= output.Length)
        throw new InvalidDataException("pack: more data than the header length.");
      output[outPos++] = literals[leafBase[len] + leaf];
    }
    if (outPos != output.Length)
      throw new InvalidDataException($"pack: decoded {outPos} bytes, header says {origLen}.");
    return output;
  }

  // ── encoder ─────────────────────────────────────────────────────────

  /// <summary>Encodes <paramref name="data"/> as a pack file.</summary>
  /// <exception cref="ArgumentException">Input of 4 GiB or more (the length field is 32 bits).</exception>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    if ((ulong)data.Length > uint.MaxValue)
      throw new ArgumentException("pack: input too large for the 32-bit length field.", nameof(data));

    var freq = new long[257];
    foreach (var v in data) ++freq[v];
    var dataBits = 0L;
    freq[EofSymbol] = 1;
    var weights = (long[])freq.Clone();
    if (data.Length == 0)
      weights[0] = 1; // a lone end-of-file code cannot form a tree; add an unused leaf

    var lengths = DeterministicHuffman.BuildCodeLengths(weights);
    HuffmanTree.LimitCodeLengths(lengths, MaxCodeLength);
    HuffmanTree.CompleteCodeLengths(lengths, MaxCodeLength);
    var maxLen = lengths.Max();

    // The end-of-file leaf has to be the last leaf of the deepest level.
    if (lengths[EofSymbol] != maxLen) {
      var swap = Array.FindIndex(lengths, l => l == maxLen);
      (lengths[swap], lengths[EofSymbol]) = (lengths[EofSymbol], lengths[swap]);
    }

    // Leaves per level, ordered by byte value, EOF last.
    var byLevel = new List<int>[maxLen + 1];
    for (var l = 1; l <= maxLen; ++l) byLevel[l] = [];
    for (var s = 0; s < 256; ++s)
      if (lengths[s] > 0) byLevel[lengths[s]].Add(s);
    byLevel[maxLen].Add(EofSymbol);

    // Code values: internal nodes first, then leaves in listed order.
    var codes = new int[257];
    var parents = new int[maxLen + 1];
    var nodes = 0;
    for (var l = maxLen; l >= 1; --l) {
      nodes >>= 1;
      parents[l] = nodes;
      nodes += byLevel[l].Count;
    }
    if (nodes != 2 && !(nodes == 1 && maxLen == 0))
      throw new InvalidOperationException("pack: internal error, Huffman tree is not complete.");
    for (var l = 1; l <= maxLen; ++l)
      for (var i = 0; i < byLevel[l].Count; ++i)
        codes[byLevel[l][i]] = parents[l] + i;

    var header = new List<byte>(7 + maxLen + 257) { 0x1F, 0x1E };
    Span<byte> len32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(len32, (uint)data.Length);
    header.AddRange(len32.ToArray());
    header.Add((byte)maxLen);
    for (var l = 1; l <= maxLen; ++l)
      header.Add((byte)(byLevel[l].Count - (l == maxLen ? 2 : 0)));
    for (var l = 1; l <= maxLen; ++l)
      foreach (var s in byLevel[l])
        if (s != EofSymbol) header.Add((byte)s);

    for (var s = 0; s < 256; ++s) dataBits += freq[s] * lengths[s];
    dataBits += lengths[EofSymbol];
    var result = new byte[header.Count + (int)((dataBits + 7) / 8)];
    header.CopyTo(result);

    var outPos = header.Count;
    ulong acc = 0;
    var accBits = 0;
    void Put(int code, int len) {
      acc = (acc << len) | (uint)code;
      accBits += len;
      while (accBits >= 8) {
        accBits -= 8;
        result[outPos++] = (byte)(acc >> accBits);
      }
    }
    foreach (var v in data) Put(codes[v], lengths[v]);
    Put(codes[EofSymbol], lengths[EofSymbol]);
    if (accBits > 0) result[outPos] = (byte)(acc << (8 - accBits));
    return result;
  }
}
