using Compression.Registry;

namespace Compression.Analysis.Scanning;

/// <summary>
/// Central registry of all known format magic byte patterns.
/// Patterns are derived from <see cref="FormatRegistry"/> descriptors at startup,
/// organized for efficient O(n) scanning using a two-byte prefix hash table.
/// </summary>
public static class SignatureDatabase {

  /// <summary>A single magic byte signature entry.</summary>
  public sealed record SignatureEntry(
    string FormatName,
    byte[] Magic,
    int Offset,
    double Confidence
  );

  private static readonly List<SignatureEntry> _entries = [];
  private static readonly Dictionary<int, List<SignatureEntry>> _prefixIndex = [];

  static SignatureDatabase() {
    // Initialize registry so all descriptors are available
    Compression.Lib.FormatRegistration.EnsureInitialized();

    // Derive all signatures from registry descriptors
    foreach (var desc in FormatRegistry.All) {
      foreach (var sig in desc.MagicSignatures) {
        Add(desc.Id, sig.Bytes, sig.Offset, sig.Confidence);
      }
    }

    BuildIndex();
  }

  private static void Add(string name, byte[] magic, int offset, double confidence) {
    _entries.Add(new SignatureEntry(name, magic, offset, confidence));
  }

  private static void BuildIndex() {
    foreach (var entry in _entries) {
      // Key by first 2 bytes of magic (or first byte if magic is 1 byte)
      var key = entry.Magic.Length >= 2
        ? (entry.Magic[0] << 8) | entry.Magic[1]
        : entry.Magic[0] << 8;

      if (!_prefixIndex.TryGetValue(key, out var list)) {
        list = [];
        _prefixIndex[key] = list;
      }
      list.Add(entry);
    }
  }

  /// <summary>All registered signature entries.</summary>
  public static IReadOnlyList<SignatureEntry> Entries => _entries;

  /// <summary>
  /// Returns signature entries whose first two bytes match the given prefix.
  /// Used for O(1) lookup during scanning.
  /// </summary>
  public static IReadOnlyList<SignatureEntry> GetByPrefix(byte b0, byte b1) {
    var key = (b0 << 8) | b1;
    return _prefixIndex.TryGetValue(key, out var list) ? list : [];
  }
}
