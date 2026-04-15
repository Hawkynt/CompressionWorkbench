using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.RePair;

/// <summary>
/// Exposes Re-Pair (Recursive Pairing) as a benchmarkable building block.
/// An offline grammar-based compression algorithm that repeatedly replaces the most
/// frequent pair of adjacent symbols with a new non-terminal, building a straight-line
/// grammar. The grammar rules and final sequence are then serialized.
/// </summary>
public sealed class RePairBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_RePair";
  /// <inheritdoc/>
  public string DisplayName => "Re-Pair";
  /// <inheritdoc/>
  public string Description => "Recursive Pairing, offline grammar-based compression";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  // Non-terminals start at 256 (above byte range).
  private const int FirstNonTerminal = 256;
  private const int MaxRules = 65536;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write 4-byte LE uncompressed size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    // Work on a mutable list of symbols.
    var symbols = new List<int>(data.Length);
    foreach (var b in data)
      symbols.Add(b);

    // Grammar rules: rule[i] = (left, right) for non-terminal (FirstNonTerminal + i).
    var rules = new List<(int Left, int Right)>();

    // Repeatedly find the most frequent pair and replace it.
    while (rules.Count < MaxRules) {
      // Count pair frequencies.
      var pairFreq = new Dictionary<long, int>();
      for (var i = 0; i < symbols.Count - 1; i++) {
        var key = ((long)symbols[i] << 32) | (uint)symbols[i + 1];
        pairFreq.TryGetValue(key, out var c);
        pairFreq[key] = c + 1;
      }

      // Find most frequent pair (must appear >= 2 times).
      var bestKey = 0L;
      var bestCount = 1;
      foreach (var (key, count) in pairFreq) {
        if (count > bestCount) {
          bestCount = count;
          bestKey = key;
        }
      }

      if (bestCount < 2)
        break;

      var left = (int)(bestKey >> 32);
      var right = (int)(bestKey & 0xFFFFFFFF);
      var newSymbol = FirstNonTerminal + rules.Count;
      rules.Add((left, right));

      // Replace all non-overlapping occurrences of the pair.
      var i2 = 0;
      while (i2 < symbols.Count - 1) {
        if (symbols[i2] == left && symbols[i2 + 1] == right) {
          symbols[i2] = newSymbol;
          symbols.RemoveAt(i2 + 1);
          // Don't advance i2 — check for further replacement starting at this position.
        } else {
          i2++;
        }
      }
    }

    // Serialize: number of rules, then each rule (left, right as uint16),
    // then final sequence length, then each symbol as uint16.
    Span<byte> buf = stackalloc byte[4];

    BinaryPrimitives.WriteInt32LittleEndian(buf, rules.Count);
    ms.Write(buf);

    Span<byte> pairBuf = stackalloc byte[4];
    foreach (var (left, right) in rules) {
      BinaryPrimitives.WriteUInt16LittleEndian(pairBuf, (ushort)left);
      BinaryPrimitives.WriteUInt16LittleEndian(pairBuf[2..], (ushort)right);
      ms.Write(pairBuf);
    }

    BinaryPrimitives.WriteInt32LittleEndian(buf, symbols.Count);
    ms.Write(buf);

    Span<byte> symBuf = stackalloc byte[2];
    foreach (var sym in symbols) {
      BinaryPrimitives.WriteUInt16LittleEndian(symBuf, (ushort)sym);
      ms.Write(symBuf);
    }

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var offset = 4;

    // Read rules.
    var ruleCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    offset += 4;

    var rules = new (int Left, int Right)[ruleCount];
    for (var i = 0; i < ruleCount; i++) {
      var left = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
      var right = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 2, 2));
      rules[i] = (left, right);
      offset += 4;
    }

    // Read final sequence.
    var seqLen = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    offset += 4;

    var result = new List<byte>(originalSize);
    var expandStack = new Stack<int>();

    for (var i = 0; i < seqLen; i++) {
      var sym = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
      offset += 2;

      // Expand symbol (iterative to avoid stack overflow on deep grammars).
      expandStack.Push(sym);
      while (expandStack.Count > 0) {
        var s = expandStack.Pop();
        if (s < FirstNonTerminal) {
          result.Add((byte)s);
        } else {
          var ruleIdx = s - FirstNonTerminal;
          // Push right first so left is processed first (stack is LIFO).
          expandStack.Push(rules[ruleIdx].Right);
          expandStack.Push(rules[ruleIdx].Left);
        }
      }
    }

    return [.. result];
  }
}
