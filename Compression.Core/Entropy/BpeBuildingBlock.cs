using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>Controls how a byte-pair grammar is constructed.</summary>
public enum BpeConstructionStrategy {
  /// <summary>Repeatedly replace the currently most frequent profitable pair.</summary>
  Greedy,

  /// <summary>Explore every profitable merge sequence within each search block and keep the smallest result.</summary>
  Exhaustive,
}

/// <summary>
/// Exposes Philip Gage's byte-pair compression as a benchmarkable building block.
/// Repeated adjacent byte pairs are replaced by byte values that do not occur in the
/// current block; the replacement table is stored with the encoded bytes.
/// </summary>
/// <remarks>
/// <para>
/// Greedy construction uses blocks of at most 65,535 bytes and repeatedly takes the
/// most frequent profitable pair. Exhaustive construction is combinatorial, so it uses
/// 64-byte search blocks and evaluates every profitable merge sequence in each block.
/// The wire format is identical for both strategies and the decoder is strategy-agnostic.
/// </para>
/// <para>
/// Wire format: 4-byte little-endian original length, followed by blocks. Each block
/// starts with its 2-byte original length and 2-byte stored length. Equal lengths mean
/// the block is raw. A compressed block contains one byte rule count, then
/// <c>(code,left,right)</c> triples in creation order, followed by the encoded byte
/// sequence. A replacement is profitable when its actual non-overlapping occurrence
/// count exceeds the three-byte rule cost.
/// </para>
/// <para>
/// Reference: Philip Gage, "A New Algorithm for Data Compression", Dr. Dobb's Journal,
/// February 1994.
/// </para>
/// </remarks>
public sealed class BpeBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_BPE";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Byte Pair Encoding";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Philip Gage byte-pair compression using unused byte symbols";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <summary>The grammar-construction strategy used by <see cref="Compress"/>.</summary>
  public BpeConstructionStrategy ConstructionStrategy { get; }

  private const int MaxBlockLength = ushort.MaxValue;
  private const int ExhaustiveBlockLength = 64;
  private const int AlphabetSize = 256;
  private const int PairCount = AlphabetSize * AlphabetSize;
  private const int RuleSize = 3;

  /// <summary>Creates a BPE building block using greedy grammar construction.</summary>
  public BpeBuildingBlock() : this(BpeConstructionStrategy.Greedy) { }

  /// <summary>Creates a BPE building block using the requested grammar-construction strategy.</summary>
  /// <param name="constructionStrategy">How pair substitutions are selected.</param>
  public BpeBuildingBlock(BpeConstructionStrategy constructionStrategy) {
    if (!Enum.IsDefined(constructionStrategy))
      throw new ArgumentOutOfRangeException(nameof(constructionStrategy));
    this.ConstructionStrategy = constructionStrategy;
  }

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    Span<byte> integer = stackalloc byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(integer, data.Length);
    output.Write(integer);

    var maximumBlockLength = this.ConstructionStrategy == BpeConstructionStrategy.Exhaustive
      ? ExhaustiveBlockLength
      : MaxBlockLength;

    for (var offset = 0; offset < data.Length;) {
      var blockLength = Math.Min(maximumBlockLength, data.Length - offset);
      WriteBlock(output, data.Slice(offset, blockLength), this.ConstructionStrategy);
      offset += blockLength;
    }

    return output.ToArray();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < sizeof(int))
      throw new InvalidDataException("BPE stream is missing its original-length header.");

    var originalLength = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalLength < 0)
      throw new InvalidDataException("BPE stream declares a negative original length.");
    if (originalLength == 0) {
      if (data.Length != sizeof(int))
        throw new InvalidDataException("BPE empty stream contains trailing data.");
      return [];
    }

    var result = new byte[originalLength];
    var inputOffset = sizeof(int);
    var outputOffset = 0;

    while (outputOffset < originalLength) {
      if (inputOffset + 4 > data.Length)
        throw new InvalidDataException("BPE stream ends inside a block header.");

      var blockLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(inputOffset, 2));
      var storedLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(inputOffset + 2, 2));
      inputOffset += 4;

      if (blockLength == 0)
        throw new InvalidDataException("BPE stream contains a zero-length block.");
      if (blockLength > originalLength - outputOffset)
        throw new InvalidDataException("BPE block expands past the declared original length.");
      if (storedLength == 0 || inputOffset + storedLength > data.Length)
        throw new InvalidDataException("BPE stream ends inside a block payload.");

      var block = data.Slice(inputOffset, storedLength);
      inputOffset += storedLength;

      if (storedLength == blockLength) {
        block.CopyTo(result.AsSpan(outputOffset, blockLength));
      } else {
        DecodeBlock(block, result.AsSpan(outputOffset, blockLength));
      }

      outputOffset += blockLength;
    }

    if (inputOffset != data.Length)
      throw new InvalidDataException("BPE stream contains trailing data.");

    return result;
  }

  private static void WriteBlock(Stream output, ReadOnlySpan<byte> block, BpeConstructionStrategy strategy) {
    var freeCodes = FindFreeCodes(block);
    var encoded = strategy == BpeConstructionStrategy.Exhaustive
      ? BuildExhaustive(block, freeCodes)
      : BuildGreedy(block, freeCodes);

    var compressedLength = 1 + encoded.Rules.Length * RuleSize + encoded.Sequence.Length;
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(header, checked((ushort)block.Length));

    if (compressedLength >= block.Length) {
      BinaryPrimitives.WriteUInt16LittleEndian(header[2..], checked((ushort)block.Length));
      output.Write(header);
      output.Write(block);
      return;
    }

    BinaryPrimitives.WriteUInt16LittleEndian(header[2..], checked((ushort)compressedLength));
    output.Write(header);
    output.WriteByte(checked((byte)encoded.Rules.Length));
    foreach (var rule in encoded.Rules) {
      output.WriteByte(rule.Code);
      output.WriteByte(rule.Left);
      output.WriteByte(rule.Right);
    }
    output.Write(encoded.Sequence);
  }

  private static byte[] FindFreeCodes(ReadOnlySpan<byte> block) {
    Span<bool> unavailable = stackalloc bool[AlphabetSize];
    foreach (var value in block)
      unavailable[value] = true;

    var result = new byte[AlphabetSize];
    var count = 0;
    for (var value = AlphabetSize - 1; value >= 0; --value)
      if (!unavailable[value])
        result[count++] = (byte)value;

    return result.AsSpan(0, count).ToArray();
  }

  private static EncodedBlock BuildGreedy(ReadOnlySpan<byte> block, byte[] freeCodes) {
    var sequence = block.ToArray();
    var length = sequence.Length;
    var freeCount = freeCodes.Length;
    var rules = new List<Rule>(freeCount);
    var counts = new int[PairCount];
    var earliest = new int[PairCount];
    var lastEnd = new int[PairCount];

    while (freeCount > 0 && length >= 2) {
      Array.Clear(counts);
      Array.Fill(earliest, int.MaxValue);
      Array.Fill(lastEnd, -1);

      for (var i = 0; i + 1 < length; ++i) {
        var pair = sequence[i] << 8 | sequence[i + 1];
        if (lastEnd[pair] >= i)
          continue;
        lastEnd[pair] = i + 1;
        ++counts[pair];
        if (earliest[pair] == int.MaxValue)
          earliest[pair] = i;
      }

      var bestPair = -1;
      var bestCount = RuleSize;
      var bestPosition = int.MaxValue;
      for (var pair = 0; pair < PairCount; ++pair) {
        var count = counts[pair];
        if (count < bestCount || count == bestCount && earliest[pair] >= bestPosition)
          continue;
        bestPair = pair;
        bestCount = count;
        bestPosition = earliest[pair];
      }

      if (bestPair < 0 || bestCount <= RuleSize)
        break;

      var left = (byte)(bestPair >> 8);
      var right = (byte)bestPair;
      var code = freeCodes[--freeCount];
      rules.Add(new Rule(code, left, right));

      var write = 0;
      for (var read = 0; read < length;) {
        if (read + 1 < length && sequence[read] == left && sequence[read + 1] == right) {
          sequence[write++] = code;
          read += 2;
        } else {
          sequence[write++] = sequence[read++];
        }
      }
      length = write;
    }

    return new EncodedBlock([.. rules], sequence.AsSpan(0, length).ToArray());
  }

  private static EncodedBlock BuildExhaustive(ReadOnlySpan<byte> block, byte[] freeCodes) {
    var search = new ExhaustiveSearch(freeCodes);
    var result = search.FindBest(block.ToArray(), freeCodes.Length);
    return new EncodedBlock(result.Rules, result.Sequence);
  }

  private static List<PairCandidate> FindProfitablePairs(ReadOnlySpan<byte> sequence) {
    var pairStats = new Dictionary<int, PairStats>(Math.Max(0, sequence.Length - 1));

    for (var i = 0; i + 1 < sequence.Length; ++i) {
      var pair = sequence[i] << 8 | sequence[i + 1];
      if (!pairStats.TryGetValue(pair, out var stats))
        stats = new PairStats(0, i, -1);

      if (stats.LastEnd >= i)
        continue;

      ++stats.Count;
      stats.LastEnd = i + 1;
      pairStats[pair] = stats;
    }

    var result = new List<PairCandidate>(pairStats.Count);
    foreach (var (pair, stats) in pairStats) {
      // A code created by k substitutions occurs exactly k times. Any later rule
      // containing that code can therefore occur at most k times as well. A pair
      // used three times or fewer can never lead to a future rule that recovers
      // its three-byte rule cost, so excluding it does not prune an optimal grammar.
      if (stats.Count <= RuleSize)
        continue;
      result.Add(new PairCandidate((byte)(pair >> 8), (byte)pair, stats.Count, stats.Earliest));
    }

    result.Sort(static (left, right) => {
      var order = right.Count.CompareTo(left.Count);
      if (order != 0)
        return order;
      order = left.Earliest.CompareTo(right.Earliest);
      if (order != 0)
        return order;
      order = left.Left.CompareTo(right.Left);
      return order != 0 ? order : left.Right.CompareTo(right.Right);
    });
    return result;
  }

  private static byte[] ReplacePair(ReadOnlySpan<byte> sequence, PairCandidate pair, byte code) {
    var result = new byte[sequence.Length - pair.Count];
    var write = 0;

    for (var read = 0; read < sequence.Length;) {
      if (read + 1 < sequence.Length && sequence[read] == pair.Left && sequence[read + 1] == pair.Right) {
        result[write++] = code;
        read += 2;
      } else {
        result[write++] = sequence[read++];
      }
    }

    return result;
  }

  private static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> destination) {
    if (block.IsEmpty)
      throw new InvalidDataException("BPE compressed block is empty.");

    var ruleCount = block[0];
    var rulesLength = 1 + ruleCount * RuleSize;
    if (rulesLength >= block.Length)
      throw new InvalidDataException("BPE compressed block has no encoded payload.");

    Span<Rule> rules = stackalloc Rule[AlphabetSize];
    Span<int> ruleIndex = stackalloc int[AlphabetSize];
    ruleIndex.Fill(-1);

    var offset = 1;
    for (var index = 0; index < ruleCount; ++index) {
      var code = block[offset++];
      var left = block[offset++];
      var right = block[offset++];
      if (ruleIndex[code] >= 0)
        throw new InvalidDataException("BPE compressed block defines a replacement code twice.");
      rules[index] = new Rule(code, left, right);
      ruleIndex[code] = index;
    }

    for (var index = 0; index < ruleCount; ++index) {
      var rule = rules[index];
      var leftRule = ruleIndex[rule.Left];
      var rightRule = ruleIndex[rule.Right];
      if (leftRule >= index || rightRule >= index)
        throw new InvalidDataException("BPE compressed block contains a forward or cyclic rule reference.");
    }

    Span<byte> expansion = stackalloc byte[AlphabetSize];
    var written = 0;
    while (offset < block.Length) {
      var stackLength = 1;
      expansion[0] = block[offset++];

      while (stackLength > 0) {
        var symbol = expansion[--stackLength];
        var index = ruleIndex[symbol];
        if (index < 0) {
          if (written >= destination.Length)
            throw new InvalidDataException("BPE block expands past its declared length.");
          destination[written++] = symbol;
          continue;
        }

        if (stackLength + 2 > expansion.Length)
          throw new InvalidDataException("BPE rule expansion is deeper than the byte alphabet permits.");
        var rule = rules[index];
        expansion[stackLength++] = rule.Right;
        expansion[stackLength++] = rule.Left;
      }
    }

    if (written != destination.Length)
      throw new InvalidDataException("BPE block does not expand to its declared length.");
  }

  private sealed class ExhaustiveSearch(byte[] freeCodes) {
    private readonly Dictionary<(int FreeCount, string Sequence), SearchResult> _memo = [];

    public SearchResult FindBest(byte[] sequence, int freeCount) {
      var key = (freeCount, Convert.ToHexString(sequence));
      if (this._memo.TryGetValue(key, out var memoized))
        return memoized;

      var best = new SearchResult(sequence.Length, [], sequence);
      if (freeCount == 0 || sequence.Length < 2) {
        this._memo[key] = best;
        return best;
      }

      var candidates = FindProfitablePairs(sequence);
      if (candidates.Count == 0) {
        this._memo[key] = best;
        return best;
      }

      var code = freeCodes[freeCount - 1];
      foreach (var candidate in candidates) {
        var replaced = ReplacePair(sequence, candidate, code);
        var child = this.FindBest(replaced, freeCount - 1);
        var cost = RuleSize + child.Cost;
        if (cost >= best.Cost)
          continue;

        var rules = new Rule[child.Rules.Length + 1];
        rules[0] = new Rule(code, candidate.Left, candidate.Right);
        child.Rules.CopyTo(rules, 1);
        best = new SearchResult(cost, rules, child.Sequence);
      }

      this._memo[key] = best;
      return best;
    }
  }

  private readonly record struct EncodedBlock(Rule[] Rules, byte[] Sequence);
  private readonly record struct Rule(byte Code, byte Left, byte Right);
  private readonly record struct PairCandidate(byte Left, byte Right, int Count, int Earliest);
  private record struct PairStats(int Count, int Earliest, int LastEnd);
  private sealed record SearchResult(int Cost, Rule[] Rules, byte[] Sequence);
}
