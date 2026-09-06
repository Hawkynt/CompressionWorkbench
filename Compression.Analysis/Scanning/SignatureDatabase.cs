using Compression.Registry;

namespace Compression.Analysis.Scanning;

/// <summary>
/// Central registry of all content-identification metadata known to CompressionWorkbench and
/// referenced Hawkynt format packages. Fixed signatures are indexed for byte-granular carving;
/// package-native header detectors cover structural formats at known/aligned candidate starts.
/// </summary>
public static class SignatureDatabase {

  /// <summary>A single magic byte signature entry.</summary>
  public sealed record SignatureEntry(
    string FormatName,
    string DisplayName,
    FormatCategory Category,
    string DefaultExtension,
    byte[] Magic,
    byte[]? Mask,
    int Offset,
    double Confidence,
    string Source
  );

  private static readonly List<SignatureEntry> _entries = [];
  private static readonly Dictionary<int, List<SignatureEntry>> _prefixIndex = [];
  private static readonly Dictionary<byte, List<SignatureEntry>> _singleByteIndex = [];
  private static readonly List<SignatureEntry> _maskedPrefixEntries = [];
  private static readonly Dictionary<string, (FormatCategory Category, string DefaultExtension)> _formatInfo =
    new(StringComparer.OrdinalIgnoreCase);

  static SignatureDatabase() {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    foreach (var desc in FormatRegistry.All) {
      RegisterFormatInfo(desc.Id, desc.Category, desc.DefaultExtension);
      foreach (var sig in desc.MagicSignatures)
        Add(desc.Id, desc.DisplayName, desc.Category, desc.DefaultExtension, sig, "descriptor");
    }

    foreach (var source in FormatRegistry.DetectionSources) {
      foreach (var item in source.Signatures) {
        RegisterFormatInfo(item.FormatId, item.Category, item.DefaultExtension);
        Add(
          item.FormatId,
          item.DisplayName,
          item.Category,
          item.DefaultExtension,
          item.Signature,
          source.GetType().Name);
      }
    }

    // Preserve historically useful carving signatures even when no operational descriptor exposes
    // one. They live here (not in individual carvers) so every analysis path sees one source of truth.
    AddFallback("Jpeg", "JPEG image", FormatCategory.Image, ".jpg", [0xFF, 0xD8, 0xFF, 0xE0], 0.90);
    AddFallback("Jpeg", "JPEG image", FormatCategory.Image, ".jpg", [0xFF, 0xD8, 0xFF, 0xE1], 0.90);
    AddFallback("Jpeg", "JPEG image", FormatCategory.Image, ".jpg", [0xFF, 0xD8, 0xFF, 0xDB], 0.85);
    AddFallback("Png", "PNG image", FormatCategory.Image, ".png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0.99);
    AddFallback("Gif", "GIF image", FormatCategory.Image, ".gif", "GIF87a"u8.ToArray(), 0.97);
    AddFallback("Gif", "GIF image", FormatCategory.Image, ".gif", "GIF89a"u8.ToArray(), 0.97);
    AddFallback("Pdf", "PDF document", FormatCategory.DetectionOnly, ".pdf", "%PDF-"u8.ToArray(), 0.97);
    AddFallback("Pe", "PE executable", FormatCategory.DetectionOnly, ".exe", [0x4D, 0x5A], 0.60);
    AddFallback("Elf", "ELF executable", FormatCategory.DetectionOnly, ".elf", [0x7F, 0x45, 0x4C, 0x46], 0.95);
    AddFallback("Sqlite", "SQLite database", FormatCategory.DetectionOnly, ".sqlite", "SQLite format 3\0"u8.ToArray(), 0.99);
    AddFallback("Mp3", "MP3 audio", FormatCategory.Audio, ".mp3", "ID3"u8.ToArray(), 0.70);
    AddFallback("Bmp", "BMP image", FormatCategory.Image, ".bmp", [0x42, 0x4D], 0.55);

    // FAT's universal 0x55AA boot trailer is far too generic for raw carving. The optional
    // BS_FilSysType labels are weaker than a native driver probe but strong enough to seed one.
    AddFallback("Fat", "FAT filesystem", FormatCategory.Archive, ".img", "FAT32   "u8.ToArray(), 0.90, offset: 82);
    AddFallback("Fat", "FAT filesystem", FormatCategory.Archive, ".img", "FAT12   "u8.ToArray(), 0.90, offset: 54);
    AddFallback("Fat", "FAT filesystem", FormatCategory.Archive, ".img", "FAT16   "u8.ToArray(), 0.90, offset: 54);

    BuildIndex();
  }

  private static void Add(
    string name,
    string displayName,
    FormatCategory category,
    string defaultExtension,
    MagicSignature signature,
    string source) {
    if (signature.Bytes.Length == 0 || signature.Offset < 0)
      return;
    if (signature.Mask is { } mask && mask.Length != signature.Bytes.Length)
      return;

    if (_entries.Any(existing =>
      existing.Offset == signature.Offset
      && string.Equals(existing.FormatName, name, StringComparison.OrdinalIgnoreCase)
      && existing.Magic.AsSpan().SequenceEqual(signature.Bytes)
      && MasksEqual(existing.Mask, signature.Mask)))
      return;

    _entries.Add(new SignatureEntry(
      name,
      displayName,
      category,
      defaultExtension,
      signature.Bytes,
      signature.Mask,
      signature.Offset,
      signature.Confidence,
      source));
  }

  private static void AddFallback(
    string name,
    string displayName,
    FormatCategory category,
    string defaultExtension,
    byte[] magic,
    double confidence,
    int offset = 0) {
    RegisterFormatInfo(name, category, defaultExtension);
    Add(name, displayName, category, defaultExtension, new MagicSignature(magic, offset, confidence), "forensic-fallback");
  }

  private static void RegisterFormatInfo(string id, FormatCategory category, string defaultExtension) {
    if (_formatInfo.TryGetValue(id, out var existing)) {
      if (string.IsNullOrEmpty(existing.DefaultExtension) && !string.IsNullOrEmpty(defaultExtension))
        _formatInfo[id] = (category, defaultExtension);
      return;
    }
    _formatInfo[id] = (category, defaultExtension);
  }

  private static bool MasksEqual(byte[]? left, byte[]? right)
    => left is null
      ? right is null
      : right is not null && left.AsSpan().SequenceEqual(right);

  private static void BuildIndex() {
    foreach (var entry in _entries) {
      if (entry.Mask is { } mask && HasMaskedPrefix(mask, entry.Magic.Length)) {
        _maskedPrefixEntries.Add(entry);
        continue;
      }

      if (entry.Magic.Length == 1) {
        if (!_singleByteIndex.TryGetValue(entry.Magic[0], out var singles))
          _singleByteIndex[entry.Magic[0]] = singles = [];
        singles.Add(entry);
        continue;
      }

      var key = (entry.Magic[0] << 8) | entry.Magic[1];
      if (!_prefixIndex.TryGetValue(key, out var list))
        _prefixIndex[key] = list = [];
      list.Add(entry);
    }
  }

  private static bool HasMaskedPrefix(byte[] mask, int magicLength) {
    if (magicLength == 0) return false;
    if (mask[0] != 0xFF) return true;
    return magicLength >= 2 && mask[1] != 0xFF;
  }

  /// <summary>All registered signature entries.</summary>
  public static IReadOnlyList<SignatureEntry> Entries => _entries;

  /// <summary>Entries whose first two bytes can be resolved by exact prefix lookup.</summary>
  public static IReadOnlyList<SignatureEntry> GetByPrefix(byte b0, byte b1) {
    var key = (b0 << 8) | b1;
    return _prefixIndex.TryGetValue(key, out var list) ? list : [];
  }

  /// <summary>Exact one-byte signatures beginning with <paramref name="b0"/>.</summary>
  public static IReadOnlyList<SignatureEntry> GetByFirstByte(byte b0)
    => _singleByteIndex.TryGetValue(b0, out var list) ? list : [];

  /// <summary>Signatures whose first one/two bytes use a mask and therefore cannot use exact hashing.</summary>
  public static IReadOnlyList<SignatureEntry> MaskedPrefixEntries => _maskedPrefixEntries;

  /// <summary>Package-native structural header detectors.</summary>
  public static IReadOnlyList<IFormatDetectionSource> HeaderDetectionSources => FormatRegistry.DetectionSources;

  /// <summary>Canonical extension for a detected format, or <c>.bin</c> if no source declares one.</summary>
  public static string GetDefaultExtension(string formatId)
    => _formatInfo.TryGetValue(formatId, out var info) && !string.IsNullOrWhiteSpace(info.DefaultExtension)
      ? info.DefaultExtension
      : FormatRegistry.GetById(formatId)?.DefaultExtension is { Length: > 0 } extension ? extension : ".bin";

  /// <summary>Best-known category for a detected format.</summary>
  public static FormatCategory GetCategory(string formatId)
    => _formatInfo.TryGetValue(formatId, out var info)
      ? info.Category
      : FormatRegistry.GetById(formatId)?.Category ?? FormatCategory.DetectionOnly;
}
