using System.Security.Cryptography;

namespace Compression.Registry;

/// <summary>
/// Immutable semantic snapshot used to prove that a destructive rebuild did not
/// change the logical archive/filesystem model. Physical layout and compression
/// method are deliberately excluded; names, directory presence, logical lengths,
/// payload hashes, timestamps, encryption/link flags and descriptor-specific
/// semantic metadata are not.
/// </summary>
public sealed class SemanticPreservationManifest {
  private sealed record Entry(
    string Name,
    bool IsDirectory,
    long Length,
    DateTime? LastModified,
    bool IsEncrypted,
    string? Kind,
    bool IsSymlink,
    string? LinkTarget,
    long? TargetSize,
    string? Sha256
  );

  private readonly Entry[] _entries;
  private readonly KeyValuePair<string, string>[] _extended;

  private SemanticPreservationManifest(
      Entry[] entries,
      KeyValuePair<string, string>[] extended) {
    this._entries = entries;
    this._extended = extended;
  }

  /// <summary>Number of logical entries, including empty directories.</summary>
  public int EntryCount => this._entries.Length;

  /// <summary>
  /// Captures all semantics exposed by <paramref name="operations"/>. Entries in
  /// <paramref name="ignoredNames"/> are descriptor-declared synthetic views and
  /// are excluded because they are generated rather than stored content.
  /// </summary>
  public static SemanticPreservationManifest Capture(
      Stream archive,
      IArchiveFormatOperations operations,
      string? password = null,
      IReadOnlySet<string>? ignoredNames = null) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(operations);
    if (!archive.CanRead || !archive.CanSeek)
      throw new ArgumentException("Semantic manifest capture requires a readable, seekable stream.", nameof(archive));

    archive.Position = 0;
    var listed = operations.List(archive, password);
    var entries = new List<Entry>(listed.Count);
    foreach (var item in listed) {
      if (ignoredNames?.Contains(item.Name) == true) continue;

      string? hash = null;
      if (!item.IsDirectory && !item.IsSymlink) {
        archive.Position = 0;
        using var payload = operations.OpenEntry(archive, item.Name, password);
        using var sha = SHA256.Create();
        hash = Convert.ToHexString(sha.ComputeHash(payload));
      }

      entries.Add(new Entry(
        item.Name,
        item.IsDirectory,
        item.OriginalSize,
        item.LastModified,
        item.IsEncrypted,
        item.Kind,
        item.IsSymlink,
        item.LinkTarget,
        item.TargetSize,
        hash));
    }

    entries.Sort(CompareEntries);

    KeyValuePair<string, string>[] extended = [];
    if (operations is IArchiveSemanticMetadataProvider provider) {
      archive.Position = 0;
      extended = provider.CaptureSemanticMetadata(archive)
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .ThenBy(pair => pair.Value, StringComparer.Ordinal)
        .ToArray();
    }

    archive.Position = 0;
    return new SemanticPreservationManifest([.. entries], extended);
  }

  /// <summary>
  /// Throws when <paramref name="other"/> differs in any captured semantic.
  /// Directory ordering and physical allocation are intentionally irrelevant.
  /// </summary>
  public void VerifyEquivalent(SemanticPreservationManifest other) {
    ArgumentNullException.ThrowIfNull(other);

    if (this._entries.Length != other._entries.Length)
      throw new InvalidOperationException(
        $"Rebuild changed the logical entry count ({this._entries.Length} -> {other._entries.Length}).");

    for (var i = 0; i < this._entries.Length; ++i) {
      var expected = this._entries[i];
      var actual = other._entries[i];
      if (expected == actual) continue;

      var changed = DescribeDifference(expected, actual);
      throw new InvalidOperationException(
        $"Rebuild changed archive semantics at entry {i} ('{expected.Name}'): {changed}.");
    }

    if (!this._extended.SequenceEqual(other._extended))
      throw new InvalidOperationException(
        "Rebuild changed format-specific semantic metadata (attributes, links, sparse extents or container flags).");
  }

  private static int CompareEntries(Entry left, Entry right) {
    var result = StringComparer.Ordinal.Compare(left.Name, right.Name);
    if (result != 0) return result;
    result = left.IsDirectory.CompareTo(right.IsDirectory);
    if (result != 0) return result;
    result = left.Length.CompareTo(right.Length);
    if (result != 0) return result;
    return StringComparer.Ordinal.Compare(left.Sha256, right.Sha256);
  }

  private static string DescribeDifference(Entry expected, Entry actual) {
    if (!string.Equals(expected.Name, actual.Name, StringComparison.Ordinal))
      return $"path/name changed to '{actual.Name}'";
    if (expected.IsDirectory != actual.IsDirectory)
      return "file/directory kind changed";
    if (expected.Length != actual.Length)
      return $"logical length changed ({expected.Length} -> {actual.Length})";
    if (!string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal))
      return "payload hash changed";
    if (expected.LastModified != actual.LastModified)
      return $"timestamp changed ({expected.LastModified:O} -> {actual.LastModified:O})";
    if (expected.IsEncrypted != actual.IsEncrypted)
      return $"encryption flag changed ({expected.IsEncrypted} -> {actual.IsEncrypted})";
    if (!string.Equals(expected.Kind, actual.Kind, StringComparison.Ordinal))
      return $"entry kind metadata changed ('{expected.Kind}' -> '{actual.Kind}')";
    if (expected.IsSymlink != actual.IsSymlink)
      return $"symlink flag changed ({expected.IsSymlink} -> {actual.IsSymlink})";
    if (!string.Equals(expected.LinkTarget, actual.LinkTarget, StringComparison.Ordinal))
      return $"link target changed ('{expected.LinkTarget}' -> '{actual.LinkTarget}')";
    if (expected.TargetSize != actual.TargetSize)
      return $"link target size changed ({expected.TargetSize} -> {actual.TargetSize})";
    return "entry metadata changed";
  }
}
