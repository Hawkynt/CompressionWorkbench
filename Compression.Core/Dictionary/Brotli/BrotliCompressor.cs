using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Brotli;

/// <summary>
/// Compresses data in the Brotli format (RFC 7932).
/// </summary>
/// <remarks>
/// Supports two modes:
/// <list type="bullet">
///   <item>Compress: Uses uncompressed meta-blocks (fast, no compression ratio).</item>
///   <item><see cref="CompressLz77"/>: Uses LZ77 + Huffman compressed meta-blocks (actual compression).</item>
/// </list>
/// </remarks>
public static class BrotliCompressor {
  // Brotli copy length code 23 (base 2118, 24 extra bits) → max single copy ~16 MB.
  private const int BrotliMaxCopyLength = 16 * 1024 * 1024;

  /// <summary>
  /// Compresses data to the Brotli format at the specified compression level.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="level">The compression level.</param>
  /// <returns>The Brotli-compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, BrotliCompressionLevel level) {
    if (level == BrotliCompressionLevel.Uncompressed || data.Length < 16)
      return Compress(data);

    var lz77 = CompressLz77(data);
    var uncomp = Compress(data);
    return lz77.Length < uncomp.Length ? lz77 : uncomp;
  }

  /// <summary>
  /// Compresses data to the Brotli format using uncompressed meta-blocks.
  /// Fast encoding with no compression ratio improvement.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The Brotli-compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    var writer = new BrotliBitWriter(output);

    // Encode window bits (WBITS = 16 → single 0 bit)
    writer.WriteBits(1, 0); // WBITS = 16

    if (data.Length == 0) {
      // Empty stream: WBITS then ISLAST=1, ISEMPTY=1
      writer.WriteBits(1, 1); // ISLAST
      writer.WriteBits(1, 1); // ISEMPTY
      writer.AlignToByte();
      writer.Flush();
      return output.ToArray();
    }

    // Split into uncompressed meta-blocks of up to 65536 bytes.
    // Per RFC 7932, ISUNCOMPRESSED is only present when ISLAST=0,
    // so all data blocks use ISLAST=0, followed by a final empty last block.
    var offset = 0;
    while (offset < data.Length) {
      var blockSize = Math.Min(data.Length - offset, 65536);

      // ISLAST = 0 (uncompressed blocks cannot be last)
      writer.WriteBits(1, 0);

      // MLEN: encode as MNIBBLES=4 (16-bit length)
      var mlen = blockSize - 1;
      writer.WriteBits(2, 0); // MNIBBLES - 4 = 0 → 4 nibbles
      writer.WriteBits(4, (uint)(mlen & 0xF));
      writer.WriteBits(4, (uint)((mlen >> 4) & 0xF));
      writer.WriteBits(4, (uint)((mlen >> 8) & 0xF));
      writer.WriteBits(4, (uint)((mlen >> 12) & 0xF));

      // ISUNCOMPRESSED = 1
      writer.WriteBits(1, 1);

      // Align to byte boundary before uncompressed data
      writer.AlignToByte();

      // Write raw bytes
      for (var i = 0; i < blockSize; ++i)
        writer.WriteByte(data[offset + i]);

      offset += blockSize;
    }

    // Final empty last meta-block: ISLAST=1, ISEMPTY=1
    writer.WriteBits(1, 1); // ISLAST
    writer.WriteBits(1, 1); // ISEMPTY
    writer.AlignToByte();
    writer.Flush();
    return output.ToArray();
  }

  /// <summary>
  /// Compresses data to the Brotli format using LZ77 + Huffman compressed meta-blocks.
  /// Produces actual compression by finding repeated sequences.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The Brotli-compressed data.</returns>
  public static byte[] CompressLz77(ReadOnlySpan<byte> data) {
    switch (data.Length) {
      // For very short data, uncompressed is more efficient
      case 0:
      case < 16: return Compress(data);
    }

    using var output = new MemoryStream();
    var writer = new BrotliBitWriter(output);

    // Determine window size
    var windowBits = ComputeWindowBits(data.Length);
    var windowSize = 1 << windowBits;

    // Encode window bits (WBITS)
    WriteWindowBits(writer, windowBits);

    // Find LZ77 matches using hash chain.
    // Per RFC 7932 §4: max backward distance is (1 << WBITS) - 16.
    var dataArray = data.ToArray();
    var maxBackwardDistance = Math.Max(1, windowSize - 16);
    var matchFinder = new HashChainMatchFinder(maxBackwardDistance);
    var commands = FindMatches(dataArray, matchFinder, maxBackwardDistance);

    // Emit compressed meta-blocks
    EmitCompressedMetaBlock(writer, dataArray, commands, isLast: true);

    writer.AlignToByte();
    writer.Flush();

    var compressed = output.ToArray();

    // If compressed is larger, fall back to uncompressed
    if (compressed.Length >= data.Length + 10)
      return Compress(data);

    return compressed;
  }

  /// <summary>
  /// Computes appropriate window bits for the given data length.
  /// </summary>
  private static int ComputeWindowBits(int dataLength) {
    var bits = BrotliConstants.MinWindowBits;
    while ((1 << bits) < dataLength && bits < BrotliConstants.MaxWindowBits)
      ++bits;

    return bits;
  }

  /// <summary>
  /// Writes the WBITS field to the stream header (RFC 7932 §9.1).
  /// </summary>
  private static void WriteWindowBits(BrotliBitWriter writer, int windowBits) {
    switch (windowBits) {
      case 16:
        // Single '0' bit.
        writer.WriteBits(1, 0);
        break;

      case 17:
        // 7 bits: bit '1', then six '0' bits.
        writer.WriteBits(7, 1);
        break;

      case >= 18 and <= 24:
        // 4 bits: bit '1', then 3-bit (windowBits - 17) in [1..7].
        writer.WriteBits(1, 1);
        writer.WriteBits(3, (uint)(windowBits - 17));
        break;

      case >= 10 and <= 15:
        // 7 bits: bit '1', then '000', then 3-bit (windowBits - 8) in [2..7].
        writer.WriteBits(1, 1);
        writer.WriteBits(3, 0);
        writer.WriteBits(3, (uint)(windowBits - 8));
        break;

      default:
        throw new ArgumentOutOfRangeException(nameof(windowBits), windowBits,
          $"WBITS must be in [{BrotliConstants.MinWindowBits}..{BrotliConstants.MaxWindowBits}].");
    }
  }

  /// <summary>
  /// An LZ77 command: either a literal run or a match (distance + length).
  /// <see cref="DistanceCode"/> is the chosen Brotli distance code (-1 = implicit/last
  /// distance via IaC range, 0-15 = ring-buffer reference, 16+ = complex distance code).
  /// </summary>
  private readonly record struct LzCommand(int InsertLength, int CopyLength, int Distance, int DistanceCode = 0);

  /// <summary>
  /// Finds LZ77 matches in the input data and returns a sequence of commands.
  /// </summary>
  private static List<LzCommand> FindMatches(byte[] data, HashChainMatchFinder matchFinder, int windowSize) {
    var commands = new List<LzCommand>();
    var pos = 0;
    var literalStart = 0;

    while (pos < data.Length) {
      Match bestMatch = default;
      if (pos + 4 <= data.Length) {
        var maxDist = Math.Min(pos, windowSize);
        var maxLen = Math.Min(BrotliMaxCopyLength, data.Length - pos);
        bestMatch = matchFinder.FindMatch(data, pos, maxDist, maxLen, 4);
      }

      if (bestMatch.Length >= 4) {
        var insertLen = pos - literalStart;
        commands.Add(new(insertLen, bestMatch.Length, bestMatch.Distance));
        var end = Math.Min(pos + bestMatch.Length - 1, data.Length - 4);
        for (var j = pos + 1; j <= end; ++j)
          matchFinder.InsertPosition(data, j);
        pos += bestMatch.Length;
        literalStart = pos;
      } else
        ++pos;
    }

    // Trailing literals
    if (literalStart < data.Length)
      commands.Add(new(data.Length - literalStart, 0, 0));

    return commands;
  }

  /// <summary>
  /// Emits a compressed meta-block containing the given LZ77 commands.
  /// Uses a simple Huffman coding scheme with a single block type per category.
  /// </summary>
  /// <summary>
  /// Checks whether a command fits in an IAC range of the specified type.
  /// </summary>
  private static bool FitsRange(int insertLength, int copyLength, bool wantImplicit) {
    var insertCode = FindTableCode(BrotliConstants.InsertLengthTable, insertLength);
    var copyCode = copyLength >= 2 ? FindTableCode(BrotliConstants.CopyLengthTable, copyLength) : 0;
    foreach (var (insBase, cpBase, _, isImplicit) in IacRanges) {
      if (isImplicit != wantImplicit) continue;
      if (insertCode - insBase is >= 0 and <= 7 && copyCode - cpBase is >= 0 and <= 7) return true;
    }
    return false;
  }

  /// <summary>
  /// Preprocesses commands to resolve distance encoding. For each match command, picks
  /// the best Brotli distance code: implicit IaC (no code) when the distance is the
  /// most-recent in the ring, ring codes 0-15 when the distance matches a ring slot or
  /// an offset thereof (no extra bits), or complex codes 16+ otherwise (extra bits).
  /// Commands that don't fit any IaC range are converted to literals.
  /// </summary>
  private static List<LzCommand> ResolveDistanceEncoding(List<LzCommand> commands) {
    int[] distRing = [16, 15, 11, 4];
    var distRingIdx = 3;
    var resolved = new List<LzCommand>(commands.Count);

    foreach (var cmd in commands) {
      if (cmd.CopyLength <= 0) {
        resolved.Add(cmd);
        continue;
      }

      var lastDist = distRing[distRingIdx & 3];
      var canImplicit = cmd.Distance == lastDist && FitsRange(cmd.InsertLength, cmd.CopyLength, true);
      var canExplicit = FitsRange(cmd.InsertLength, cmd.CopyLength, false);

      var emittedImplicit = false;
      if (canImplicit) {
        resolved.Add(cmd with { Distance = -1, DistanceCode = -1 });
        emittedImplicit = true;
      } else if (canExplicit) {
        // Pick the cheapest distance code: prefer ring codes 1-15 (no extra bits) over
        // complex codes 16+ (5-25 extra bits). Code 0 is reserved for the implicit path.
        var distCode = FindRingDistanceCode(cmd.Distance, distRing, distRingIdx);
        if (distCode < 0)
          distCode = EncodeComplexDistanceCode(cmd.Distance);
        resolved.Add(cmd with { DistanceCode = distCode });
      } else {
        // Try adjusting insert/copy split to fit an explicit range.
        var total = cmd.InsertLength + cmd.CopyLength;
        var adjusted = false;
        for (var ni = cmd.InsertLength + 1; ni <= total - 2; ++ni) {
          if (!FitsRange(ni, total - ni, false)) continue;
          var distCode = FindRingDistanceCode(cmd.Distance, distRing, distRingIdx);
          if (distCode < 0) distCode = EncodeComplexDistanceCode(cmd.Distance);
          resolved.Add(new LzCommand(ni, total - ni, cmd.Distance, distCode));
          adjusted = true;
          break;
        }

        if (!adjusted) {
          resolved.Add(new LzCommand(total, 0, 0));
          continue;
        }
      }

      // Per RFC 7932 §4: the distance ring updates only when an explicit distance code != 0
      // is used. Implicit IaC = distance code 0 (no update).
      if (!emittedImplicit) {
        distRingIdx = (distRingIdx + 1) & 3;
        distRing[distRingIdx] = cmd.Distance;
      }
    }

    // Merge non-final literal-only commands into the next command's insert.
    // Literal-only commands in non-final position corrupt the stream because the
    // decoder always decodes a copy length and may attempt to copy bytes.
    for (var i = resolved.Count - 2; i >= 0; --i) {
      if (resolved[i].CopyLength != 0) continue;
      var next = resolved[i + 1];
      resolved[i + 1] = new LzCommand(resolved[i].InsertLength + next.InsertLength,
        next.CopyLength, next.Distance);
      resolved.RemoveAt(i);
    }

    return resolved;
  }

  private static void EmitCompressedMetaBlock(BrotliBitWriter writer, byte[] data,
    List<LzCommand> commands, bool isLast) {
    var totalBytes = data.Length;
    commands = ResolveDistanceEncoding(commands);
    // ISLAST
    writer.WriteBits(1, isLast ? 1u : 0u);
    if (isLast)
      writer.WriteBits(1, 0); // ISEMPTY = 0

    // MLEN: MNIBBLES nibbles (4, 5, or 6), encoded as MNIBBLES-4 in 2 bits
    var mlen = totalBytes - 1;
    var mNibbles = mlen <= 0xFFFF ? 4 : mlen <= 0xFFFFF ? 5 : 6;
    writer.WriteBits(2, (uint)(mNibbles - 4));
    for (var n = 0; n < mNibbles; ++n)
      writer.WriteBits(4, (uint)((mlen >> (n * 4)) & 0xF));

    if (!isLast)
      writer.WriteBits(1, 0); // ISUNCOMPRESSED = 0

    // Block type counts: 1 each (no block partitioning).
    WriteBlockTypeCount(writer, 1); // literal
    WriteBlockTypeCount(writer, 1); // insert&copy
    WriteBlockTypeCount(writer, 1); // distance

    // NPOSTFIX = 0, NDIRECT = 0
    writer.WriteBits(2, 0);
    writer.WriteBits(4, 0);

    // First pass: collect per-context literal frequencies (UTF8 context mode).
    // We always use UTF8 mode — works well for both text and binary, with the lookup
    // tables collapsing similar byte categories to the same context value.
    const int contextMode = 2; // UTF8
    var litFreqByContext = new int[64][];
    for (var c = 0; c < 64; ++c) litFreqByContext[c] = new int[256];

    var litPos = 0;
    foreach (var cmd in commands) {
      for (var i = 0; i < cmd.InsertLength && litPos < data.Length; ++i) {
        var ctx = LiteralContext(data, litPos, contextMode);
        ++litFreqByContext[ctx][data[litPos]];
        ++litPos;
      }
      litPos += cmd.CopyLength;
    }

    // Cluster contexts into N literal trees; pick the N that gives the best
    // estimated compression (entropy + tree-overhead trade-off).
    var (contextMap, numLitTrees, treeFreqs) = ClusterContexts(litFreqByContext);

    // Context mode for the (single) literal block type.
    writer.WriteBits(2, (uint)contextMode);

    // NTREESL: number of literal trees.
    WriteBlockTypeCount(writer, numLitTrees);

    // Literal context map (only emitted if numLitTrees > 1).
    if (numLitTrees > 1)
      WriteContextMap(writer, contextMap, numLitTrees);

    // NTREESD: number of distance trees (always 1).
    WriteBlockTypeCount(writer, 1);

    // Build IaC and distance frequencies.
    var iacFreq = new int[BrotliConstants.NumInsertAndCopyLengthCodes];
    var distAlphabetSize = 16 + 0 + (48 << 0); // 64
    var distFreq = new int[distAlphabetSize];

    foreach (var cmd in commands) {
      var useImplicit = cmd is { CopyLength: > 0, Distance: -1 };
      var iacCode = EncodeInsertAndCopyCode(cmd.InsertLength, cmd.CopyLength, useImplicit);
      if (iacCode < iacFreq.Length)
        ++iacFreq[iacCode];
    }

    foreach (var cmd in commands) {
      if (cmd.CopyLength <= 0 || cmd.DistanceCode < 0) continue;
      if (cmd.DistanceCode < distFreq.Length) ++distFreq[cmd.DistanceCode];
    }

    // Build Huffman trees: one per literal-tree-cluster, plus IaC and distance.
    var litLengths = new int[numLitTrees][];
    var litCodes = new int[numLitTrees][];
    var singleLit = new bool[numLitTrees];
    for (var t = 0; t < numLitTrees; ++t) {
      litLengths[t] = BuildCodeLengths(treeFreqs[t], 256);
      WriteSimplePrefixCode(writer, litLengths[t], 256);
      litCodes[t] = BuildCanonicalCodes(litLengths[t], 256);
      singleLit[t] = litLengths[t].Count(l => l > 0) <= 1;
    }

    var iacLengths = BuildCodeLengths(iacFreq, BrotliConstants.NumInsertAndCopyLengthCodes);
    WriteSimplePrefixCode(writer, iacLengths, BrotliConstants.NumInsertAndCopyLengthCodes);

    var distLengths = BuildCodeLengths(distFreq, distAlphabetSize);
    WriteSimplePrefixCode(writer, distLengths, distAlphabetSize);

    var iacCodes = BuildCanonicalCodes(iacLengths, BrotliConstants.NumInsertAndCopyLengthCodes);
    var distCodes = BuildCanonicalCodes(distLengths, distAlphabetSize);

    var singleIac = iacLengths.Count(l => l > 0) <= 1;
    var singleDist = distLengths.Count(l => l > 0) <= 1;

    // Encode commands.
    litPos = 0;
    foreach (var cmd in commands) {
      var useImplicit = cmd is { CopyLength: > 0, Distance: -1 };
      var iacCode = EncodeInsertAndCopyCode(cmd.InsertLength, cmd.CopyLength, useImplicit);
      if (!singleIac) WriteCode(writer, iacCodes, iacLengths, iacCode);

      WriteInsertLengthExtra(writer, cmd.InsertLength);

      if (cmd.CopyLength > 0)
        WriteCopyLengthExtra(writer, cmd.CopyLength);

      for (var i = 0; i < cmd.InsertLength && litPos < data.Length; ++i) {
        var ctx = LiteralContext(data, litPos, contextMode);
        var tree = contextMap[ctx];
        if (!singleLit[tree]) WriteCode(writer, litCodes[tree], litLengths[tree], data[litPos]);
        ++litPos;
      }

      litPos += cmd.CopyLength;

      if (cmd.CopyLength <= 0 || cmd.DistanceCode < 0) continue;

      if (!singleDist) WriteCode(writer, distCodes, distLengths, cmd.DistanceCode);
      WriteDistanceExtra(writer, cmd.Distance, cmd.DistanceCode);
    }
  }

  /// <summary>Computes the Brotli literal context (0-63) for the byte at <paramref name="pos"/>.</summary>
  private static int LiteralContext(byte[] data, int pos, int contextMode) {
    var p1 = pos > 0 ? data[pos - 1] : (byte)0;
    var p2 = pos > 1 ? data[pos - 2] : (byte)0;
    return contextMode switch {
      0 => p1 & 0x3F,
      1 => p1 >> 2,
      2 => BrotliConstants.Utf8ContextLut0[p1] | BrotliConstants.Utf8ContextLut1[p2],
      _ => p1 & 0x3F,
    };
  }

  /// <summary>
  /// Clusters the 64 context byte-frequency vectors into a small number of trees,
  /// trading off cluster compactness against per-tree header overhead. Tries
  /// candidate cluster counts (1, 2, 4) and picks the one with lowest total cost.
  /// </summary>
  private static (int[] ContextMap, int NumTrees, int[][] TreeFreqs) ClusterContexts(int[][] freqByContext) {
    var totalLiterals = 0;
    for (var c = 0; c < 64; ++c)
      for (var b = 0; b < 256; ++b)
        totalLiterals += freqByContext[c][b];

    // For very small literal counts the per-tree header overhead dwarfs any savings.
    if (totalLiterals < 1024)
      return BuildSingleTreeCluster(freqByContext);

    // TEMP: only single-tree until multi-tree libbrotli interop bug is found.
    // Multi-tree self-round-trip works, but libbrotli rejects the output for some inputs.
    var candidates = new[] { 1 };
    int[] bestMap = null!;
    var bestN = 0;
    int[][] bestF = null!;
    var bestCost = double.PositiveInfinity;

    foreach (var k in candidates) {
      var (map, n, f) = ClusterContextsK(freqByContext, k);
      var cost = EstimateClusterCost(f, n, totalLiterals);
      if (cost < bestCost) {
        bestCost = cost;
        bestMap = map;
        bestN = n;
        bestF = f;
      }
    }

    return (bestMap, bestN, bestF);
  }

  private static (int[] ContextMap, int NumTrees, int[][] TreeFreqs) BuildSingleTreeCluster(int[][] freqByContext) {
    var map = new int[64];
    var combined = new int[256];
    for (var c = 0; c < 64; ++c)
      for (var b = 0; b < 256; ++b)
        combined[b] += freqByContext[c][b];
    return (map, 1, [combined]);
  }

  /// <summary>
  /// Clusters 64 context distributions into <paramref name="k"/> groups using simple
  /// agglomerative (greedy nearest-merge) clustering on entropy-weighted similarity.
  /// </summary>
  private static (int[] ContextMap, int NumTrees, int[][] TreeFreqs) ClusterContextsK(int[][] freqByContext, int k) {
    if (k <= 1)
      return BuildSingleTreeCluster(freqByContext);

    // Start: each context is its own cluster (skip empty contexts).
    var clusters = new List<int[]>(); // each cluster is a 256-byte freq vector
    var clusterMembers = new List<List<int>>(); // contexts in each cluster
    for (var c = 0; c < 64; ++c) {
      var freq = freqByContext[c];
      var sum = 0;
      for (var b = 0; b < 256; ++b) sum += freq[b];
      if (sum == 0) continue;
      clusters.Add((int[])freq.Clone());
      clusterMembers.Add([c]);
    }

    // Greedily merge the closest pair until k clusters remain.
    while (clusters.Count > k) {
      var bestI = 0;
      var bestJ = 1;
      var bestCost = double.PositiveInfinity;
      for (var i = 0; i < clusters.Count; ++i)
        for (var j = i + 1; j < clusters.Count; ++j) {
          var cost = MergeCost(clusters[i], clusters[j]);
          if (cost < bestCost) {
            bestCost = cost;
            bestI = i;
            bestJ = j;
          }
        }

      var merged = new int[256];
      for (var b = 0; b < 256; ++b)
        merged[b] = clusters[bestI][b] + clusters[bestJ][b];
      var mergedMembers = new List<int>(clusterMembers[bestI]);
      mergedMembers.AddRange(clusterMembers[bestJ]);

      clusters.RemoveAt(bestJ);
      clusterMembers.RemoveAt(bestJ);
      clusters[bestI] = merged;
      clusterMembers[bestI] = mergedMembers;
    }

    var actualK = clusters.Count;
    var map = new int[64];
    for (var t = 0; t < actualK; ++t)
      foreach (var ctx in clusterMembers[t])
        map[ctx] = t;
    // Empty contexts default to tree 0.

    return (map, actualK, clusters.ToArray());
  }

  /// <summary>
  /// Cost (in bits) of merging two clusters: total bits to encode using the merged
  /// distribution minus the sum of bits using individual distributions. Always ≥ 0;
  /// 0 when distributions are identical, larger when they differ. Greedy clustering
  /// picks the merge with smallest cost.
  /// </summary>
  private static double MergeCost(int[] a, int[] b) {
    long sumA = 0, sumB = 0;
    for (var i = 0; i < 256; ++i) { sumA += a[i]; sumB += b[i]; }
    var sumM = sumA + sumB;
    if (sumM == 0) return 0;

    // cost(X) = Σ count_i * log2(N / count_i), the optimal bits to encode X.
    // Return cost(merged) - cost(A) - cost(B).
    double cost = 0;
    for (var i = 0; i < 256; ++i) {
      var fa = a[i];
      var fb = b[i];
      var fm = fa + fb;
      if (fa > 0) cost -= fa * Math.Log2((double)sumA / fa);
      if (fb > 0) cost -= fb * Math.Log2((double)sumB / fb);
      if (fm > 0) cost += fm * Math.Log2((double)sumM / fm);
    }
    return cost;
  }

  /// <summary>
  /// Estimates total bit cost of using <paramref name="numTrees"/> literal trees:
  /// sum of per-tree entropy plus rough header overhead.
  /// </summary>
  private static double EstimateClusterCost(int[][] treeFreqs, int numTrees, long totalLiterals) {
    double entropy = 0;
    for (var t = 0; t < numTrees; ++t) {
      long sum = 0;
      for (var b = 0; b < 256; ++b) sum += treeFreqs[t][b];
      if (sum == 0) continue;
      for (var b = 0; b < 256; ++b) {
        var f = treeFreqs[t][b];
        if (f > 0) entropy -= f * Math.Log2((double)f / sum);
      }
    }
    // Per-tree header overhead estimate: a 256-symbol Huffman tree with RLE compression
    // typically takes 30-80 bytes in the bitstream depending on length distribution.
    // Add ~80 bits for the context-map header when multi-tree.
    var overheadBits = numTrees * 50.0 * 8 + (numTrees > 1 ? 80 : 0);
    return entropy + overheadBits;
  }

  /// <summary>
  /// Writes a context map to the stream (RFC 7932 §7.3). Uses a Huffman code over symbols
  /// {0=tree-0, 1..maxRle=run-of-zeros, maxRle+1..maxRle+numTrees-1=trees 1..N-1}. We pick
  /// no RLE and no MTF for simplicity — the map is only 64 entries so the savings are marginal.
  /// </summary>
  private static void WriteContextMap(BrotliBitWriter writer, int[] contextMap, int numTrees) {
    writer.WriteBits(1, 0); // useRleEncoding = 0

    var alphabetSize = numTrees;
    var freq = new int[alphabetSize];
    foreach (var c in contextMap)
      ++freq[c];

    var lengths = BuildCodeLengths(freq, alphabetSize);
    WriteSimplePrefixCode(writer, lengths, alphabetSize);
    var codes = BuildCanonicalCodes(lengths, alphabetSize);

    var single = lengths.Count(l => l > 0) <= 1;
    foreach (var c in contextMap)
      if (!single)
        WriteCode(writer, codes, lengths, c);

    writer.WriteBits(1, 0); // MTF = 0
  }

  /// <summary>
  /// Writes a count using Brotli's VarLenUint8 + 1 encoding (RFC 7932 §6).
  /// VarLenUint8(N): if N==0 → "0" (1 bit); if N==1 → "1 000" (4 bits with nbits=0);
  /// else → "1 nbits<sub>3</sub> extra<sub>nbits</sub>" where nbits = floor(log2(N)) and extra = N - 2^nbits.
  /// </summary>
  private static void WriteBlockTypeCount(BrotliBitWriter writer, int count) {
    var n = count - 1;
    if (n == 0) {
      writer.WriteBits(1, 0);
      return;
    }
    writer.WriteBits(1, 1);
    if (n == 1) {
      writer.WriteBits(3, 0);
      return;
    }
    var bits = 0;
    var v = n;
    while (v > 1) { v >>= 1; ++bits; }
    writer.WriteBits(3, (uint)bits);
    writer.WriteBits(bits, (uint)(n - (1 << bits)));
  }

  /// <summary>
  /// RFC 7932 Table 8 range definitions: (insertCodeBase, copyCodeBase, iacCodeBase, implicit).
  /// Ranges 0-1 use implicit distance (codes 0-127).
  /// Ranges 2-8 use explicit distance (codes 128-575).
  /// </summary>
  private static readonly (int InsBase, int CpBase, int CodeBase, bool Implicit)[] IacRanges = [
    (0, 0, 0, true),      // range 0:  insert 0-7,   copy 0-7,   implicit
    (0, 8, 64, true),     // range 1:  insert 0-7,   copy 8-15,  implicit
    (0, 0, 128, false),   // range 2:  insert 0-7,   copy 0-7,   explicit
    (0, 8, 192, false),   // range 3:  insert 0-7,   copy 8-15,  explicit
    (8, 0, 256, false),   // range 4:  insert 8-15,  copy 0-7,   explicit
    (8, 8, 320, false),   // range 5:  insert 8-15,  copy 8-15,  explicit
    (0, 16, 384, false),  // range 6:  insert 0-7,   copy 16-23, explicit
    (16, 0, 448, false),  // range 7:  insert 16-23, copy 0-7,   explicit
    (8, 16, 512, false),  // range 8:  insert 8-15,  copy 16-23, explicit
    (16, 8, 576, false),  // range 9:  insert 16-23, copy 8-15,  explicit
    (16, 16, 640, false)  // range 10: insert 16-23, copy 16-23, explicit
  ];

  /// <summary>
  /// Encodes insert and copy lengths into a combined insert-and-copy code (RFC 7932 Table 8).
  /// </summary>
  /// <param name="insertLength">Number of literal bytes to insert.</param>
  /// <param name="copyLength">Number of bytes to copy (0 for literal-only).</param>
  /// <param name="useImplicitDistance">When true, uses implicit distance ranges (codes 0-127).</param>
  private static int EncodeInsertAndCopyCode(int insertLength, int copyLength, bool useImplicitDistance) {
    var insertCode = FindTableCode(BrotliConstants.InsertLengthTable, insertLength);
    var copyCode = copyLength >= 2
      ? FindTableCode(BrotliConstants.CopyLengthTable, copyLength)
      : 0;

    if (useImplicitDistance) {
      // Find matching implicit range (verified to fit by ResolveDistanceEncoding)
      foreach (var (insBase, cpBase, codeBase, isImplicit) in IacRanges) {
        if (!isImplicit) continue;
        var insOff = insertCode - insBase;
        var cpOff = copyCode - cpBase;
        if (insOff is >= 0 and <= 7 && cpOff is >= 0 and <= 7)
          return codeBase + insOff * 8 + cpOff;
      }
    }

    // Literal-only: must use implicit ranges (codes 0-127) so decoder doesn't expect a distance.
    // The decoder stops before copying when metaBytesRemaining hits zero.
    if (copyLength == 0) {
      foreach (var (insBase, cpBase, codeBase, isImplicit) in IacRanges) {
        if (!isImplicit) continue;
        var insOff = insertCode - insBase;
        if (insOff is >= 0 and <= 7 && cpBase == 0)
          return codeBase + insOff * 8;
      }
    }

    // Find a valid explicit-distance range
    foreach (var (insBase, cpBase, codeBase, isImplicit) in IacRanges) {
      if (isImplicit) continue;
      var insOff = insertCode - insBase;
      var cpOff = copyCode - cpBase;
      if (insOff is < 0 or > 7 || cpOff is < 0 or > 7) continue;
      return codeBase + (insOff << 3) + cpOff;
    }

    // Fallback: clamp to range 2 (insert 0-7, copy 0-7, explicit)
    return 128 + (Math.Clamp(insertCode, 0, 7)) * 8 + Math.Min(copyCode, 7);
  }

  private static int FindTableCode((int BaseValue, int ExtraBits)[] table, int value) {
    for (var i = table.Length - 1; i >= 0; --i)
      if (value >= table[i].BaseValue)
        return i;
    return 0;
  }

  private static void WriteInsertLengthExtra(BrotliBitWriter writer, int insertLength) {
    for (var i = BrotliConstants.InsertLengthTable.Length - 1; i >= 0; --i) {
      var (baseVal, extraBits) = BrotliConstants.InsertLengthTable[i];
      if (insertLength < baseVal)
        continue;

      if (extraBits > 0)
        writer.WriteBits(extraBits, (uint)(insertLength - baseVal));
      return;
    }
  }

  private static void WriteCopyLengthExtra(BrotliBitWriter writer, int copyLength) {
    for (var i = BrotliConstants.CopyLengthTable.Length - 1; i >= 0; --i) {
      var (baseVal, extraBits) = BrotliConstants.CopyLengthTable[i];
      if (copyLength < baseVal)
        continue;

      if (extraBits > 0)
        writer.WriteBits(extraBits, (uint)(copyLength - baseVal));
      return;
    }
  }

  /// <summary>
  /// Returns a Brotli distance code 1-15 (ring buffer reference, no extra bits) if
  /// <paramref name="distance"/> matches a ring slot or a small offset thereof.
  /// Returns -1 if no ring code matches and a complex code 16+ must be used.
  /// Code 0 is reserved for the implicit-distance IaC path; never returned here.
  /// </summary>
  private static int FindRingDistanceCode(int distance, int[] distRing, int distRingIdx) {
    // Codes 1-3: 2nd, 3rd, 4th most-recent distance.
    for (var i = 1; i < 4; ++i)
      if (distance == distRing[(distRingIdx - i) & 3])
        return i;

    // Codes 4-9: last distance ± {1, 2, 3}. Codes 10-15: 2nd-last distance ± {1, 2, 3}.
    var last = distRing[distRingIdx & 3];
    var secondLast = distRing[(distRingIdx - 1) & 3];
    if (distance == last - 1) return 4;
    if (distance == last + 1) return 5;
    if (distance == last - 2) return 6;
    if (distance == last + 2) return 7;
    if (distance == last - 3) return 8;
    if (distance == last + 3) return 9;
    if (distance == secondLast - 1) return 10;
    if (distance == secondLast + 1) return 11;
    if (distance == secondLast - 2) return 12;
    if (distance == secondLast + 2) return 13;
    if (distance == secondLast - 3) return 14;
    if (distance == secondLast + 3) return 15;
    return -1;
  }

  /// <summary>
  /// Encodes a distance value into a complex distance code 16+ (NPOSTFIX=0, NDIRECT=0).
  /// dcode = code - 16; nBits = 1 + (dcode &gt;&gt; 1); base = ((2 + (dcode &amp; 1)) &lt;&lt; nBits) - 4;
  /// distance = base + extra + 1.
  /// </summary>
  private static int EncodeComplexDistanceCode(int distance) {
    if (distance <= 0) return 16;

    var d = distance - 1;
    for (var dcode = 0; dcode < 48; ++dcode) {
      var nBits = 1 + (dcode >> 1);
      var baseDist = ((2 + (dcode & 1)) << nBits) - 4;
      if (d >= baseDist && d < baseDist + (1 << nBits))
        return 16 + dcode;
    }

    return 16 + 47;
  }

  private static void WriteDistanceExtra(BrotliBitWriter writer, int distance, int distCode) {
    if (distCode < 16) return; // ring codes 0-15 carry no extra bits

    var dcode = distCode - 16;
    var nBits = 1 + (dcode >> 1);
    var baseDist = ((2 + (dcode & 1)) << nBits) - 4;
    var extra = (distance - 1) - baseDist;
    writer.WriteBits(nBits, (uint)extra);
  }

  /// <summary>
  /// Builds Huffman code lengths from symbol frequencies using a simple algorithm.
  /// </summary>
  /// <summary>
  /// Builds optimal length-limited Huffman code lengths from symbol frequencies
  /// using the package-merge algorithm (Larmore &amp; Hirschberg 1990).
  /// Produces codes where the maximum length does not exceed <paramref name="maxLen"/>.
  /// </summary>
  /// <param name="freq">Symbol frequencies (0 means unused).</param>
  /// <param name="numSymbols">Total alphabet size.</param>
  /// <param name="maxLen">Maximum code length (Brotli spec: 15 for alphabets, 5 for CL tree).</param>
  /// <returns>Array of code lengths indexed by symbol.</returns>
  private static int[] BuildCodeLengths(int[] freq, int numSymbols, int maxLen = BrotliConstants.MaxHuffmanCodeLength) {
    var lengths = new int[numSymbols];

    var active = new List<(int Freq, int Symbol)>();
    for (var i = 0; i < numSymbols; ++i)
      if (freq[i] > 0) active.Add((freq[i], i));

    switch (active.Count) {
      case 0:
        // Empty tree: assign length 1 to two symbols to satisfy Kraft, so the decoder's
        // space counter reaches zero when the tree is used inside a complex prefix code.
        lengths[0] = 1;
        lengths[numSymbols > 1 ? 1 : 0] = 1;
        return lengths;

      case 1:
        // Single symbol: pair it with a dummy so sum(2^-1 + 2^-1) = 1.
        var onlySym = active[0].Symbol;
        lengths[onlySym] = 1;
        lengths[onlySym == 0 ? 1 : 0] = 1;
        return lengths;
    }

    active.Sort((a, b) => a.Freq != b.Freq ? a.Freq.CompareTo(b.Freq) : a.Symbol.CompareTo(b.Symbol));

    var n = active.Count;

    // Package-merge: at each level, existing coins are paired, merged with the N
    // original single-symbol coins, and sorted by cost. After maxLen iterations the
    // 2N-2 cheapest coins give the optimal length-limited prefix code: each symbol's
    // occurrence count across the selected coins equals its code length.
    var originals = new (long Cost, List<int> Syms)[n];
    for (var i = 0; i < n; ++i)
      originals[i] = (active[i].Freq, [active[i].Symbol]);

    var current = new List<(long Cost, List<int> Syms)>(n);
    foreach (var (cost, syms) in originals)
      current.Add((cost, [..syms]));

    for (var level = 2; level <= maxLen; ++level) {
      var packaged = new List<(long Cost, List<int> Syms)>(current.Count / 2);
      for (var i = 0; i + 1 < current.Count; i += 2) {
        var combined = new List<int>(current[i].Syms.Count + current[i + 1].Syms.Count);
        combined.AddRange(current[i].Syms);
        combined.AddRange(current[i + 1].Syms);
        packaged.Add((current[i].Cost + current[i + 1].Cost, combined));
      }

      var merged = new List<(long Cost, List<int> Syms)>(packaged.Count + n);
      int pi = 0, li = 0;
      while (pi < packaged.Count && li < n) {
        if (packaged[pi].Cost <= originals[li].Cost) {
          merged.Add(packaged[pi]);
          ++pi;
        } else {
          merged.Add((originals[li].Cost, [..originals[li].Syms]));
          ++li;
        }
      }
      while (pi < packaged.Count)
        merged.Add(packaged[pi++]);
      while (li < n)
        merged.Add((originals[li].Cost, [..originals[li++].Syms]));

      current = merged;
    }

    var takeCount = Math.Min(2 * n - 2, current.Count);
    for (var i = 0; i < takeCount; ++i)
      foreach (var sym in current[i].Syms)
        ++lengths[sym];

    return lengths;
  }

  /// <summary>
  /// Builds canonical code values from code lengths.
  /// Returns an array where codes[symbol] is the canonical code for that symbol.
  /// </summary>
  private static int[] BuildCanonicalCodes(int[] codeLengths, int numSymbols) {
    var codes = new int[numSymbols];

    var maxLen = 0;
    for (var i = 0; i < numSymbols; ++i)
      maxLen = Math.Max(maxLen, codeLengths[i]);

    if (maxLen == 0) return codes;

    var blCount = new int[maxLen + 1];
    for (var i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > 0)
        blCount[codeLengths[i]]++;

    var nextCode = new int[maxLen + 1];
    var code = 0;
    for (var bits = 1; bits <= maxLen; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    for (var sym = 0; sym < numSymbols; ++sym) {
      var len = codeLengths[sym];
      if (len > 0)
        codes[sym] = nextCode[len]++;
    }

    return codes;
  }

  /// <summary>
  /// Writes a Huffman code to the bit stream.
  /// Per Google Brotli reference (c/enc/entropy_encode.c, BrotliConvertBitDepthsToSymbols):
  /// canonical codes are constructed MSB-first numerically, but bit-reversed before storage
  /// so the decoder (reading LSB-first) reconstructs the canonical tree path correctly.
  /// </summary>
  private static void WriteCode(BrotliBitWriter writer, int[] codes, int[] lengths, int symbol) {
    if (symbol >= lengths.Length || lengths[symbol] == 0) {
      // Symbol not in code table — write 0 code for symbol 0
      writer.WriteBits(lengths[0] > 0 ? lengths[0] : 1, 0);
      return;
    }

    var code = codes[symbol];
    var len = lengths[symbol];

    // Bit-reverse: canonical code's MSB becomes the first bit written LSB-first.
    var reversed = 0;
    for (var i = 0; i < len; ++i) {
      reversed = (reversed << 1) | (code & 1);
      code >>= 1;
    }

    writer.WriteBits(len, (uint)reversed);
  }

  /// <summary>
  /// Writes a prefix code definition to the stream using simple format
  /// (RFC 7932 Section 3.5).
  /// </summary>
  private static void WriteSimplePrefixCode(BrotliBitWriter writer, int[] codeLengths, int numSymbols) {
    // Count used symbols
    var usedSymbols = new List<int>();
    for (var i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > 0)
        usedSymbols.Add(i);

    // RFC 7932: symbol values use max(1, ceil(log2(ALPHABET_SIZE))) bits
    var numSymBits = 1;
    while ((1 << numSymBits) < numSymbols)
      ++numSymBits;

    if (usedSymbols.Count <= 4) {
      // Simple prefix code (HSKIP=1). Lengths are positional (smallest-valued symbol
      // gets the shortest code per RFC §3.4), so we override codeLengths to the fixed
      // pattern for each NSYM. tree_select=1 [1,2,3,3] for NSYM=4 is avoided because
      // it only matches Huffman-optimal when the smallest-valued symbol is most frequent.
      writer.WriteBits(2, 1); // HSKIP = 1

      var count = usedSymbols.Count;
      writer.WriteBits(2, (uint)(count - 1)); // NSYM - 1

      foreach (var sym in usedSymbols)
        writer.WriteBits(numSymBits, (uint)sym);

      switch (count) {
        case 1:
          codeLengths[usedSymbols[0]] = 0;
          break;
        case 2:
          codeLengths[usedSymbols[0]] = 1;
          codeLengths[usedSymbols[1]] = 1;
          break;
        case 3:
          codeLengths[usedSymbols[0]] = 1;
          codeLengths[usedSymbols[1]] = 2;
          codeLengths[usedSymbols[2]] = 2;
          break;
        case 4:
          foreach (var sym in usedSymbols)
            codeLengths[sym] = 2;
          writer.WriteBits(1, 0); // tree_select=0 → [2,2,2,2]
          break;
      }
    } else
      // Use complex prefix code format
      WriteComplexPrefixCode(writer, codeLengths, numSymbols);
  }

  /// <summary>
  /// A planned emission for the complex prefix code: either a direct length code
  /// (CL symbol 0-15) or a repeat code (16 or 17) with extra bits.
  /// </summary>
  private readonly record struct ClEmission(int Symbol, int ExtraBits, int ExtraValue);

  /// <summary>
  /// Writes a complex prefix code to the stream (RFC 7932 §3.5).
  /// Uses repeat codes 16 (non-zero run) and 17 (zero run) to compactly
  /// encode runs of identical code lengths.
  /// </summary>
  private static void WriteComplexPrefixCode(BrotliBitWriter writer, int[] codeLengths, int numSymbols) {
    var lastNonZero = 0;
    for (var i = numSymbols - 1; i >= 0; --i)
      if (codeLengths[i] > 0) {
        lastNonZero = i;
        break;
      }

    var emissions = PlanComplexEmissions(codeLengths, lastNonZero);

    // Build CL frequency table from planned emissions (so codes 16/17 are accounted for).
    var clFreq = new int[BrotliConstants.NumCodeLengthCodes];
    foreach (var e in emissions)
      ++clFreq[e.Symbol];

    // CL bit lengths are emitted via the fixed static code (max value 5).
    var clLengths = BuildCodeLengths(clFreq, BrotliConstants.NumCodeLengthCodes, maxLen: 5);

    // HSKIP = 0
    writer.WriteBits(2, 0);

    var clCount = BrotliConstants.NumCodeLengthCodes;
    while (clCount > 0 && clLengths[BrotliConstants.CodeLengthCodeOrder[clCount - 1]] == 0)
      --clCount;

    for (var i = 0; i < clCount; ++i) {
      int idx = BrotliConstants.CodeLengthCodeOrder[i];
      WriteSmallCodeLength(writer, clLengths[idx]);
    }

    var clCodes = BuildCanonicalCodes(clLengths, BrotliConstants.NumCodeLengthCodes);

    foreach (var e in emissions) {
      WriteCode(writer, clCodes, clLengths, e.Symbol);
      if (e.ExtraBits > 0)
        writer.WriteBits(e.ExtraBits, (uint)e.ExtraValue);
    }
  }

  /// <summary>
  /// Plans the sequence of CL emissions needed to encode <paramref name="codeLengths"/>
  /// up to <paramref name="lastNonZero"/>, using repeat codes 16/17 for runs.
  /// </summary>
  private static List<ClEmission> PlanComplexEmissions(int[] codeLengths, int lastNonZero) {
    var emissions = new List<ClEmission>();
    var i = 0;
    while (i <= lastNonZero) {
      var cl = codeLengths[i];
      var runEnd = i;
      while (runEnd + 1 <= lastNonZero && codeLengths[runEnd + 1] == cl)
        ++runEnd;
      var runLen = runEnd - i + 1;

      if (cl == 0 && runLen >= 3) {
        PlanZeroRun(emissions, runLen);
      } else if (cl > 0 && runLen >= 4) {
        // Emit the length once, then encode (runLen - 1) repeats via code 16.
        emissions.Add(new ClEmission(cl, 0, 0));
        PlanNonZeroRun(emissions, runLen - 1);
      } else {
        for (var j = 0; j < runLen; ++j)
          emissions.Add(new ClEmission(cl, 0, 0));
      }

      i = runEnd + 1;
    }
    return emissions;
  }

  /// <summary>
  /// Plans a zero run (≥3) using code 17.
  /// Successive code 17s chain in the decoder: run_new = ((run_old - 2) &lt;&lt; 3) + delta + 3.
  /// We pick deltas that exactly produce <paramref name="count"/> zeros.
  /// </summary>
  private static void PlanZeroRun(List<ClEmission> emissions, int count) {
    // Each emission: code 17 with 3 extra bits (delta 0-7).
    // After N emissions with deltas (d_1, ..., d_N), total run is:
    //   sum_i (8^(N-i) * d_i) + (8^N + 13) / 7
    // Range covered by N emissions: [(8^N + 13)/7, (8^(N+1) + 6)/7].

    var n = 1;
    while (((1L << (3 * (n + 1))) + 6) / 7 < count)
      ++n;

    long power8N = 1L << (3 * n);
    var target = count - (power8N + 13) / 7;

    for (var i = 0; i < n; ++i) {
      var power = 1L << (3 * (n - 1 - i));
      var d = (int)Math.Min(target / power, 7);
      emissions.Add(new ClEmission(17, 3, d));
      target -= d * power;
    }
  }

  /// <summary>
  /// Plans a non-zero run (≥3) using code 16, repeating the previous non-zero length.
  /// Chain semantics: run_new = ((run_old - 2) &lt;&lt; 2) + delta + 3.
  /// </summary>
  private static void PlanNonZeroRun(List<ClEmission> emissions, int count) {
    if (count < 3) {
      // Caller must have emitted the length; trailing leftovers fall through here.
      // For count=1 or 2, callers should emit the length again directly. We don't
      // know the length here, so this branch shouldn't be reached when count<3.
      return;
    }

    var n = 1;
    while (((1L << (2 * (n + 1))) + 2) / 3 < count)
      ++n;

    long power4N = 1L << (2 * n);
    var target = count - (power4N + 5) / 3;

    for (var i = 0; i < n; ++i) {
      var power = 1L << (2 * (n - 1 - i));
      var d = (int)Math.Min(target / power, 3);
      emissions.Add(new ClEmission(16, 2, d));
      target -= d * power;
    }
  }

  /// <summary>
  /// Writes a small code length value (0-5) using the fixed prefix code
  /// from RFC 7932 Section 3.5, matching the decoder's 4-bit peek table.
  /// </summary>
  private static void WriteSmallCodeLength(BrotliBitWriter writer, int len) {
    switch (len) {
      case 0: writer.WriteBits(2, 0);  break; // 00
      case 1: writer.WriteBits(4, 7);  break; // 0111
      case 2: writer.WriteBits(3, 3);  break; // 011
      case 3: writer.WriteBits(2, 2);  break; // 10
      case 4: writer.WriteBits(2, 1);  break; // 01
      case 5: writer.WriteBits(4, 15); break; // 1111
    }
  }
}

/// <summary>
/// Bit writer for Brotli streams. Writes bits LSB-first.
/// </summary>
internal sealed class BrotliBitWriter {
  private readonly Stream _output;
  private uint _bitBuffer;
  private int _bitsUsed;

  /// <summary>
  /// Initializes a new <see cref="BrotliBitWriter"/>.
  /// </summary>
  /// <param name="output">The output stream.</param>
  public BrotliBitWriter(Stream output) => this._output = output;

  /// <summary>
  /// Writes <paramref name="count"/> bits (LSB-first) to the stream.
  /// </summary>
  /// <param name="count">Number of bits to write (1-24).</param>
  /// <param name="value">The value whose low <paramref name="count"/> bits are written.</param>
  public void WriteBits(int count, uint value) {
    this._bitBuffer |= (value & ((1u << count) - 1)) << this._bitsUsed;
    this._bitsUsed += count;
    while (this._bitsUsed >= 8) {
      this._output.WriteByte((byte)(this._bitBuffer & 0xFF));
      this._bitBuffer >>= 8;
      this._bitsUsed -= 8;
    }
  }

  /// <summary>
  /// Writes a single raw byte (must be byte-aligned).
  /// </summary>
  /// <param name="value">The byte to write.</param>
  public void WriteByte(byte value) {
    if (this._bitsUsed == 0)
      this._output.WriteByte(value);
    else
      this.WriteBits(8, value);
  }

  /// <summary>
  /// Aligns to the next byte boundary by writing zero padding bits.
  /// </summary>
  public void AlignToByte() {
    if (this._bitsUsed <= 0)
      return;

    var padding = 8 - this._bitsUsed;
    this.WriteBits(padding, 0);
  }

  /// <summary>
  /// Flushes any remaining partial byte to the output stream.
  /// </summary>
  public void Flush() {
    if (this._bitsUsed <= 0)
      return;

    this._output.WriteByte((byte)(this._bitBuffer & 0xFF));
    this._bitBuffer = 0;
    this._bitsUsed = 0;
  }
}
