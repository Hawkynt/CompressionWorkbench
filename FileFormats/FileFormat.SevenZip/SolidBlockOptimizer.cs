#pragma warning disable CS1591
namespace FileFormat.SevenZip;

/// <summary>
/// Tries multiple solid-block grouping strategies on a 7z archive and returns
/// the one that produces the smallest output. Each strategy extracts all entries,
/// regroups them into solid blocks per the strategy, compresses to measure the
/// result size, and the winning strategy's output is returned.
/// </summary>
public static class SolidBlockOptimizer {

  /// <summary>Result of an optimization trial run.</summary>
  public sealed class OptimizeResult {
    /// <summary>The re-packed archive bytes (winning strategy).</summary>
    public required byte[] Data { get; init; }
    /// <summary>Name of the winning strategy.</summary>
    public required string WinningStrategy { get; init; }
    /// <summary>Per-strategy trial results, ordered by output size ascending.</summary>
    public required IReadOnlyList<TrialResult> Trials { get; init; }
  }

  /// <summary>A single trial run result.</summary>
  public sealed class TrialResult {
        /// <summary>
    /// Gets or sets the strategy name.
    /// </summary>
public required string StrategyName { get; init; }
        /// <summary>
    /// Gets or sets the output size.
    /// </summary>
public required long OutputSize { get; init; }
        /// <summary>
    /// Gets or sets the elapsed.
    /// </summary>
public required TimeSpan Elapsed { get; init; }
  }

  /// <summary>
  /// Callback invoked before each trial starts. Parameters: (strategyIndex, totalStrategies, strategyName).
  /// </summary>
  public delegate void ProgressCallback(int index, int total, string strategyName);

  /// <summary>
  /// Tries up to <paramref name="maxTrials"/> candidate grouping strategies and
  /// returns the one that produces the smallest archive.
  /// </summary>
  /// <param name="archive">Seekable stream containing a valid 7z archive.</param>
  /// <param name="maxTrials">Maximum number of strategies to try (1-5). Default 5.</param>
  /// <param name="onProgress">Optional progress callback invoked before each trial.</param>
  /// <returns>The optimization result with the winning archive bytes and trial report.</returns>
  public static OptimizeResult Optimize(Stream archive, int maxTrials = 5, ProgressCallback? onProgress = null) {
    ArgumentNullException.ThrowIfNull(archive);
    maxTrials = Math.Clamp(maxTrials, 1, Strategies.Count);

    // Step 1: Extract all entries from the input archive
    archive.Position = 0;
    var reader = new SevenZipReader(archive, leaveOpen: true);
    var entries = new List<(string Name, byte[] Data, SevenZipEntry Meta)>();
    for (var i = 0; i < reader.Entries.Count; i++) {
      var e = reader.Entries[i];
      if (e.IsDirectory) continue;
      var data = reader.Extract(i);
      entries.Add((e.Name, data, e));
    }

    // Trivial case: 0 or 1 files cannot benefit from regrouping
    if (entries.Count <= 1) {
      archive.Position = 0;
      var original = new byte[archive.Length];
      archive.ReadExactly(original);
      return new OptimizeResult {
        Data = original,
        WinningStrategy = "original",
        Trials = [new TrialResult { StrategyName = "original", OutputSize = original.Length, Elapsed = TimeSpan.Zero }],
      };
    }

    // Step 2: Run each strategy (up to maxTrials)
    var strategies = Strategies.Take(maxTrials).ToList();
    var trials = new List<(string Name, byte[] Output, TimeSpan Elapsed)>();

    for (var i = 0; i < strategies.Count; i++) {
      var (name, grouper) = strategies[i];
      onProgress?.Invoke(i, strategies.Count, name);

      var sw = System.Diagnostics.Stopwatch.StartNew();
      try {
        var groups = grouper(entries);
        var output = BuildArchive(entries, groups);
        sw.Stop();
        trials.Add((name, output, sw.Elapsed));
      } catch {
        sw.Stop();
        // Strategy failed — skip it
      }
    }

    if (trials.Count == 0) {
      // All strategies failed — return original
      archive.Position = 0;
      var original = new byte[archive.Length];
      archive.ReadExactly(original);
      return new OptimizeResult {
        Data = original,
        WinningStrategy = "original",
        Trials = [],
      };
    }

    // Step 3: Pick the smallest
    var sorted = trials.OrderBy(t => t.Output.Length).ToList();
    var winner = sorted[0];

    return new OptimizeResult {
      Data = winner.Output,
      WinningStrategy = winner.Name,
      Trials = sorted.Select(t => new TrialResult {
        StrategyName = t.Name,
        OutputSize = t.Output.Length,
        Elapsed = t.Elapsed,
      }).ToArray(),
    };
  }

  // ── Strategy registry ──────────────────────────────────────────────

  /// <summary>
  /// A grouping strategy: given a list of entries, returns a list of groups
  /// (each group is a list of indices into the entries list).
  /// </summary>
  private delegate IReadOnlyList<int[]> GroupingStrategy(
    IReadOnlyList<(string Name, byte[] Data, SevenZipEntry Meta)> entries);

  private static readonly List<(string Name, GroupingStrategy Grouper)> Strategies = [
    ("By extension", GroupByExtension),
    ("By similarity hash", GroupBySimilarityHash),
    ("By size buckets", GroupBySizeBuckets),
    ("Single solid block", GroupSingleBlock),
    ("By file header magic", GroupByHeaderMagic),
  ];

  // ── Strategy 1: By extension ───────────────────────────────────────

  private static IReadOnlyList<int[]> GroupByExtension(
      IReadOnlyList<(string Name, byte[] Data, SevenZipEntry Meta)> entries) {
    var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < entries.Count; i++) {
      var ext = Path.GetExtension(entries[i].Name);
      if (string.IsNullOrEmpty(ext)) ext = ".noext";
      if (!groups.TryGetValue(ext, out var list)) {
        list = [];
        groups[ext] = list;
      }
      list.Add(i);
    }
    return groups.Values.Select(g => g.ToArray()).ToArray();
  }

  // ── Strategy 2: By similarity hash ─────────────────────────────────

  private static IReadOnlyList<int[]> GroupBySimilarityHash(
      IReadOnlyList<(string Name, byte[] Data, SevenZipEntry Meta)> entries) {
    // Compute a fingerprint from the first 4KB of each file using a
    // simple rolling hash, then cluster files with similar fingerprints.
    const int FingerprintSize = 4096;
    const int NumBuckets = 16; // cluster into 16 buckets by hash

    var groups = new Dictionary<int, List<int>>();
    for (var i = 0; i < entries.Count; i++) {
      var data = entries[i].Data;
      var len = Math.Min(data.Length, FingerprintSize);
      var hash = ComputeRollingHash(data.AsSpan(0, len));
      var bucket = (int)((uint)hash % NumBuckets);
      if (!groups.TryGetValue(bucket, out var list)) {
        list = [];
        groups[bucket] = list;
      }
      list.Add(i);
    }
    return groups.Values.Select(g => g.ToArray()).ToArray();
  }

  /// <summary>
  /// Computes a simple rolling hash fingerprint over data. Uses a polynomial
  /// hash with a small window to produce a content-dependent signature.
  /// </summary>
  private static int ComputeRollingHash(ReadOnlySpan<byte> data) {
    if (data.IsEmpty) return 0;
    // Use byte frequency distribution as the fingerprint — files with similar
    // byte distributions will hash to similar values.
    Span<int> freq = stackalloc int[256];
    freq.Clear();
    foreach (var b in data)
      freq[b]++;

    // Hash the frequency table
    var hash = 0x811C9DC5u; // FNV-1a offset basis
    for (var i = 0; i < 256; i++) {
      // Quantize frequency to reduce noise: bucket into 8 levels
      var quantized = (byte)Math.Min(7, freq[i] * 8 / Math.Max(1, data.Length));
      hash ^= quantized;
      hash *= 0x01000193u; // FNV-1a prime
    }
    return (int)hash;
  }

  // ── Strategy 3: By size buckets ────────────────────────────────────

  private static IReadOnlyList<int[]> GroupBySizeBuckets(
      IReadOnlyList<(string Name, byte[] Data, SevenZipEntry Meta)> entries) {
    var small = new List<int>();  // < 4KB
    var medium = new List<int>(); // 4KB - 64KB
    var large = new List<int>();  // > 64KB

    for (var i = 0; i < entries.Count; i++) {
      var len = entries[i].Data.Length;
      if (len < 4096) small.Add(i);
      else if (len < 65536) medium.Add(i);
      else large.Add(i);
    }

    var result = new List<int[]>();
    if (small.Count > 0) result.Add(small.ToArray());
    if (medium.Count > 0) result.Add(medium.ToArray());
    // Large files each get their own block for better random access
    foreach (var idx in large)
      result.Add([idx]);
    return result;
  }

  // ── Strategy 4: Single solid block ─────────────────────────────────

  private static IReadOnlyList<int[]> GroupSingleBlock(
      IReadOnlyList<(string Name, byte[] Data, SevenZipEntry Meta)> entries) {
    var all = new int[entries.Count];
    for (var i = 0; i < entries.Count; i++) all[i] = i;
    return [all];
  }

  // ── Strategy 5: By file header magic ───────────────────────────────

  private static IReadOnlyList<int[]> GroupByHeaderMagic(
      IReadOnlyList<(string Name, byte[] Data, SevenZipEntry Meta)> entries) {
    var groups = new Dictionary<string, List<int>>();
    for (var i = 0; i < entries.Count; i++) {
      var kind = DetectFileKind(entries[i].Data);
      if (!groups.TryGetValue(kind, out var list)) {
        list = [];
        groups[kind] = list;
      }
      list.Add(i);
    }
    return groups.Values.Select(g => g.ToArray()).ToArray();
  }

  /// <summary>
  /// Detects the file type from magic bytes in the header. Returns a
  /// short category string for grouping purposes.
  /// </summary>
  private static string DetectFileKind(byte[] data) {
    if (data.Length < 4) return "tiny";

    var b = data.AsSpan();
    // PDF
    if (b.Length >= 5 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F') return "pdf";
    // PNG
    if (b.Length >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return "image";
    // JPEG
    if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image";
    // GIF
    if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "image";
    // BMP
    if (b[0] == 'B' && b[1] == 'M') return "image";
    // WebP
    if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' &&
        b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image";
    // PE executable (MZ header)
    if (b[0] == 'M' && b[1] == 'Z') return "executable";
    // ELF
    if (b[0] == 0x7F && b[1] == 'E' && b[2] == 'L' && b[3] == 'F') return "executable";
    // Mach-O
    if ((b[0] == 0xFE && b[1] == 0xED && b[2] == 0xFA) ||
        (b[0] == 0xCF && b[1] == 0xFA && b[2] == 0xED)) return "executable";
    // ZIP / JAR / Office (PK)
    if (b[0] == 'P' && b[1] == 'K' && b[2] == 3 && b[3] == 4) return "archive";
    // Gzip
    if (b[0] == 0x1F && b[1] == 0x8B) return "archive";
    // 7z
    if (b[0] == '7' && b[1] == 'z' && b[2] == 0xBC && b[3] == 0xAF) return "archive";
    // RAR
    if (b[0] == 'R' && b[1] == 'a' && b[2] == 'r' && b[3] == '!') return "archive";
    // XML / HTML / SVG (angle bracket start)
    if (b[0] == '<') return "markup";
    // JSON (object or array)
    if (b[0] == '{' || b[0] == '[') return "structured";
    // UTF-8 BOM
    if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return "text";
    // Heuristic: mostly printable ASCII → text
    var printable = 0;
    var sample = Math.Min(data.Length, 512);
    for (var i = 0; i < sample; i++)
      if (data[i] is >= 0x20 and < 0x7F or (byte)'\n' or (byte)'\r' or (byte)'\t')
        printable++;
    if (printable * 100 / sample > 85) return "text";

    return "binary";
  }

  // ── Archive builder ────────────────────────────────────────────────

  /// <summary>
  /// Builds a 7z archive from the given entries with the specified block grouping.
  /// Each group becomes one solid block.
  /// </summary>
  private static byte[] BuildArchive(
      IReadOnlyList<(string Name, byte[] Data, SevenZipEntry Meta)> entries,
      IReadOnlyList<int[]> groups) {

    using var ms = new MemoryStream();
    var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true);

    // Add all entries in group order (entries within each group stay together
    // in the solid block)
    var entryIndexMap = new int[entries.Count]; // original index → add-order index
    var addOrder = 0;
    foreach (var group in groups)
      foreach (var idx in group) {
        var (name, data, meta) = entries[idx];
        writer.AddEntry(new SevenZipEntry {
          Name = name,
          LastWriteTime = meta.LastWriteTime,
          CreationTime = meta.CreationTime,
          Attributes = meta.Attributes,
        }, data);
        entryIndexMap[idx] = addOrder++;
      }

    // Build block descriptors mapping add-order indices to groups
    var blockDescs = new List<SevenZipWriter.BlockDescriptor>();
    foreach (var group in groups) {
      blockDescs.Add(new SevenZipWriter.BlockDescriptor {
        EntryIndices = group.Select(idx => entryIndexMap[idx]).ToArray(),
      });
    }

    writer.FinishWithBlocks(blockDescs);
    return ms.ToArray();
  }
}
