namespace Compression.Analysis.Scanning;

/// <summary>
/// Deep-scans binary data for known format magic byte signatures at every offset.
/// Uses a hash table keyed by the first 2 bytes of each magic pattern for O(n) performance.
/// </summary>
public static class SignatureScanner {

  /// <summary>
  /// Scans the entire data for known format signatures.
  /// </summary>
  /// <param name="data">Binary data to scan.</param>
  /// <param name="maxResults">Maximum number of results to return (default 100).</param>
  /// <returns>List of scan results sorted by confidence descending, then offset ascending.</returns>
  public static List<ScanResult> Scan(ReadOnlySpan<byte> data, int maxResults = 100) {
    var results = new List<ScanResult>();

    if (data.Length < 2) return results;

    // Separate handling for signatures with non-zero offsets (like TAR at 257, ACE at 7)
    var offsetEntries = new List<SignatureDatabase.SignatureEntry>();
    foreach (var entry in SignatureDatabase.Entries) {
      if (entry.Offset > 0)
        offsetEntries.Add(entry);
    }

    // Single-pass scan using prefix index
    for (var i = 0; i < data.Length - 1; i++) {
      var candidates = SignatureDatabase.GetByPrefix(data[i], data[i + 1]);
      foreach (var entry in candidates) {
        if (entry.Offset != 0) continue; // handled separately
        if (MatchesMagic(data, i, entry.Magic)) {
          results.Add(CreateResult(data, i, entry));
          if (results.Count >= maxResults * 10) break; // safety limit
        }
      }

      // Check offset-based entries: the magic at (i) means the format header starts at (i - entry.Offset)
      foreach (var entry in offsetEntries) {
        var headerStart = i - entry.Offset;
        if (headerStart < 0) continue;
        if (i + entry.Magic.Length > data.Length) continue;
        if (MatchesMagic(data, i, entry.Magic)) {
          results.Add(CreateResult(data, headerStart, entry));
        }
      }
    }

    // Sort by confidence descending, then offset ascending
    results.Sort((a, b) => {
      var c = b.Confidence.CompareTo(a.Confidence);
      return c != 0 ? c : a.Offset.CompareTo(b.Offset);
    });

    // Deduplicate: same format at same offset
    var seen = new HashSet<(long, string)>();
    var unique = new List<ScanResult>();
    foreach (var r in results) {
      if (seen.Add((r.Offset, r.FormatName)))
        unique.Add(r);
    }

    return unique.Count > maxResults ? unique[..maxResults] : unique;
  }

  private static bool MatchesMagic(ReadOnlySpan<byte> data, int offset, byte[] magic) {
    if (offset + magic.Length > data.Length) return false;
    for (var i = 0; i < magic.Length; i++) {
      if (data[offset + i] != magic[i]) return false;
    }
    return true;
  }

  private static ScanResult CreateResult(ReadOnlySpan<byte> data, long headerOffset, SignatureDatabase.SignatureEntry entry) {
    var previewLen = Math.Min(16, data.Length - (int)headerOffset);
    var preview = Convert.ToHexString(data.Slice((int)headerOffset, previewLen).ToArray());
    return new ScanResult(headerOffset, entry.FormatName, entry.Confidence, entry.Magic.Length, preview);
  }
}
