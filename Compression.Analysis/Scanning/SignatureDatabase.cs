namespace Compression.Analysis.Scanning;

/// <summary>
/// Central registry of all known format magic byte patterns.
/// Patterns are organized for efficient O(n) scanning using a two-byte prefix hash table.
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
    // Archive formats
    Add("ZIP",         [0x50, 0x4B, 0x03, 0x04], 0, 0.95);
    Add("RAR",         [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07], 0, 0.95);
    Add("7z",          [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], 0, 0.98);
    Add("CAB",         [0x4D, 0x53, 0x43, 0x46], 0, 0.90);
    Add("TAR",         [(byte)'u', (byte)'s', (byte)'t', (byte)'a', (byte)'r'], 257, 0.90);
    Add("AR",          [0x21, 0x3C, 0x61, 0x72, 0x63, 0x68, 0x3E, 0x0A], 0, 0.92);
    Add("RPM",         [0xED, 0xAB, 0xEE, 0xDB], 0, 0.90);
    Add("WIM",         [0x4D, 0x53, 0x57, 0x49, 0x4D, 0x00, 0x00, 0x00], 0, 0.95);
    Add("ACE",         [0x2A, 0x2A, 0x41, 0x43, 0x45, 0x2A, 0x2A], 7, 0.92);
    Add("ALZip",       [0x41, 0x4C, 0x5A, 0x01], 0, 0.92);
    Add("DMS",         [0x44, 0x4D, 0x53, 0x21], 0, 0.90);
    Add("LZX Amiga",   [0x4C, 0x5A, 0x58], 0, 0.85);
    Add("StuffIt",     [0x53, 0x49, 0x54, 0x21], 0, 0.90);
    Add("StuffIt5",    [0x53, 0x74, 0x75, 0x66, 0x66, 0x49, 0x74], 0, 0.92);
    Add("ZPAQ",        [0x7A, 0x50, 0x51], 0, 0.88);
    Add("Ha",          [0x48, 0x41], 0, 0.60);
    Add("SquashFS",    [0x68, 0x73, 0x71, 0x73], 0, 0.90);
    Add("CramFS",      [0x45, 0x3D, 0xCD, 0x28], 0, 0.90);
    Add("UHARC",       [0x55, 0x48, 0x41], 0, 0.85);
    Add("WAD",         [0x49, 0x57, 0x41, 0x44], 0, 0.85);
    Add("WAD (PWAD)",  [0x50, 0x57, 0x41, 0x44], 0, 0.85);
    Add("Spark",       [(byte)'A', (byte)'r', (byte)'c', 0x00], 0, 0.70);
    Add("ARC",         [0x1A], 0, 0.30);

    // Stream/compression formats
    Add("Gzip",        [0x1F, 0x8B], 0, 0.80);
    Add("Bzip2",       [0x42, 0x5A, 0x68], 0, 0.85);
    Add("XZ",          [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], 0, 0.98);
    Add("Zstd",        [0x28, 0xB5, 0x2F, 0xFD], 0, 0.95);
    Add("LZ4",         [0x04, 0x22, 0x4D, 0x18], 0, 0.92);
    Add("Snappy",      [0xFF, 0x06, 0x00, 0x00, 0x73, 0x4E, 0x61, 0x50], 0, 0.95);
    Add("LZOP",        [0x89, 0x4C, 0x5A, 0x4F], 0, 0.90);
    Add("Compress",    [0x1F, 0x9D], 0, 0.80);
    Add("SZDD",        [0x53, 0x5A, 0x44, 0x44], 0, 0.90);
    Add("KWAJ",        [0x4B, 0x57, 0x41, 0x4A], 0, 0.90);
    Add("Lzip",        [0x4C, 0x5A, 0x49, 0x50], 0, 0.92);
    Add("RZIP",        [0x52, 0x5A, 0x49, 0x50], 0, 0.90);
    Add("PowerPacker", [0x50, 0x50, 0x32, 0x30], 0, 0.90);
    Add("ICE Packer",  [0x49, 0x63, 0x65, 0x21], 0, 0.90);
    Add("ICE Packer",  [0x49, 0x43, 0x45, 0x21], 0, 0.90);
    Add("Squeeze",     [0x76, 0xFF], 0, 0.75);
    Add("Zlib",        [0x78, 0x01], 0, 0.60);
    Add("Zlib",        [0x78, 0x5E], 0, 0.60);
    Add("Zlib",        [0x78, 0x9C], 0, 0.65);
    Add("Zlib",        [0x78, 0xDA], 0, 0.65);

    // New stream/game/retro formats
    Add("Yaz0",       [0x59, 0x61, 0x7A, 0x30], 0, 0.92);
    Add("BriefLZ",    [0x62, 0x6C, 0x7A, 0x1A], 0, 0.90);
    Add("RNC",        [0x52, 0x4E, 0x43, 0x01], 0, 0.92);
    Add("RNC",        [0x52, 0x4E, 0x43, 0x02], 0, 0.92);
    Add("LZFSE",      [0x62, 0x76, 0x78, 0x31], 0, 0.90);
    Add("LZFSE",      [0x62, 0x76, 0x78, 0x32], 0, 0.90);
    Add("LZVN",       [0x62, 0x76, 0x78, 0x6E], 0, 0.90);
    Add("LZFSE",      [0x62, 0x76, 0x78, 0x2D], 0, 0.85);
    Add("XAR",        [0x78, 0x61, 0x72, 0x21], 0, 0.90);
    Add("Freeze",     [0x1F, 0x9E], 0, 0.80);
    Add("Freeze",     [0x1F, 0x9F], 0, 0.80);
    Add("RefPack",    [0x10, 0xFB], 0, 0.75);
    Add("aPLib",      [0x41, 0x50, 0x33, 0x32], 0, 0.85);
    Add("PackBits",   [0x50, 0x4B, 0x42, 0x54], 0, 0.85);

    // Game archives
    Add("VPK",        [0x34, 0x12, 0xAA, 0x55], 0, 0.92);
    Add("BSA",        [0x42, 0x53, 0x41, 0x00], 0, 0.88);
    Add("BA2",        [0x42, 0x54, 0x44, 0x58], 0, 0.90);
    Add("MPQ",        [0x4D, 0x50, 0x51, 0x1A], 0, 0.95);

    // Encoding formats
    Add("UUEncoding", [(byte)'b', (byte)'e', (byte)'g', (byte)'i', (byte)'n', (byte)' '], 0, 0.70);
    Add("yEnc",       [(byte)'=', (byte)'y', (byte)'b', (byte)'e', (byte)'g', (byte)'i', (byte)'n', (byte)' '], 0, 0.85);
    Add("Density",    [(byte)'D', (byte)'E', (byte)'N', (byte)'S'], 0, 0.85);

    // Text-based
    Add("Shar",        [(byte)'#', (byte)'!', (byte)'/', (byte)'b', (byte)'i', (byte)'n', (byte)'/'], 0, 0.75);
    Add("BinHex",      [(byte)'(', (byte)'T', (byte)'h', (byte)'i'], 0, 0.70);

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
