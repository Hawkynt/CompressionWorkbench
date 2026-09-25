using System.Security.Cryptography;

namespace Compression.Registry;

/// <summary>
/// Semantic snapshot used to prove that a maintenance rebuild preserved the
/// logical archive rather than merely producing another listable container.
/// </summary>
public sealed class ArchiveSemanticManifest {
  private ArchiveSemanticManifest(
      IReadOnlyList<Entry> entries,
      IReadOnlyDictionary<string, string> containerFlags) {
    this.Entries = entries;
    this.ContainerFlags = containerFlags;
  }

  /// <summary>Semantic entries sorted by exact archive path.</summary>
  public IReadOnlyList<Entry> Entries { get; }

  /// <summary>Container-level semantic flags supplied by the descriptor.</summary>
  public IReadOnlyDictionary<string, string> ContainerFlags { get; }

  /// <summary>A normalized entry plus the SHA-256 of its logical file bytes.</summary>
  public sealed record Entry(
    string Name,
    bool IsDirectory,
    long Length,
    string? Sha256,
    bool IsEncrypted,
    DateTime? LastModified,
    DateTime? CreationTime,
    uint? Attributes,
    bool IsSymlink,
    string? LinkTarget,
    long? TargetSize,
    string? HardLinkTarget,
    IReadOnlyList<ArchiveSparseExtent> SparseExtents,
    IReadOnlyDictionary<string, string> SemanticFlags);

  /// <summary>
  /// Captures the complete semantic surface exposed by <paramref name="ops"/>.
  /// Duplicate file paths are refused because the generic name-based entry-open
  /// API cannot prove which duplicate occurrence it hashed.
  /// </summary>
  public static ArchiveSemanticManifest Capture(
      Stream archive,
      IArchiveFormatOperations ops,
      string? password = null,
      CancellationToken cancellationToken = default,
      IReadOnlySet<string>? excludedNames = null) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(ops);
    if (!archive.CanRead || !archive.CanSeek)
      throw new ArgumentException("Semantic verification requires a readable, seekable stream.", nameof(archive));

    cancellationToken.ThrowIfCancellationRequested();
    archive.Position = 0;
    var listed = ops.List(archive, password)
      .Where(e => excludedNames is null || !excludedNames.Contains(e.Name))
      .ToList();
    var duplicate = listed
      .Where(static e => !e.IsDirectory)
      .GroupBy(static e => e.Name, StringComparer.Ordinal)
      .FirstOrDefault(static g => g.Count() > 1);
    if (duplicate is not null)
      throw new NotSupportedException(
        $"Cannot prove rebuild identity for duplicate archive path '{duplicate.Key}': " +
        "the generic entry-open API addresses entries by path rather than occurrence.");

    var entries = new List<Entry>(listed.Count);
    foreach (var source in listed) {
      cancellationToken.ThrowIfCancellationRequested();
      string? digest = null;
      if (!source.IsDirectory) {
        archive.Position = 0;
        using var entry = ops.OpenEntry(archive, source.Name, password);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (true) {
          cancellationToken.ThrowIfCancellationRequested();
          var read = entry.Read(buffer);
          if (read <= 0)
            break;
          hash.AppendData(buffer.AsSpan(0, read));
        }
        digest = Convert.ToHexString(hash.GetHashAndReset());
      }

      var sparse = source.SparseExtents?.ToArray() ?? [];
      var flags = source.SemanticFlags is null
        ? new Dictionary<string, string>(StringComparer.Ordinal)
        : new Dictionary<string, string>(source.SemanticFlags, StringComparer.Ordinal);
      entries.Add(new Entry(
        source.Name,
        source.IsDirectory,
        source.OriginalSize,
        digest,
        source.IsEncrypted,
        source.LastModified,
        source.CreationTime,
        source.Attributes,
        source.IsSymlink,
        source.LinkTarget,
        source.TargetSize,
        source.HardLinkTarget,
        sparse,
        flags));
    }

    entries.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));

    IReadOnlyDictionary<string, string> containerFlags =
      new Dictionary<string, string>(StringComparer.Ordinal);
    if (ops is IArchiveSemanticMetadataProvider provider) {
      archive.Position = 0;
      containerFlags = new Dictionary<string, string>(
        provider.GetContainerSemanticFlags(archive, password),
        StringComparer.Ordinal);
    }

    archive.Position = 0;
    return new ArchiveSemanticManifest(entries, containerFlags);
  }

  /// <summary>
  /// Throws when <paramref name="other"/> differs in any exposed semantic field.
  /// Compression sizes/method names and physical entry order are deliberately
  /// absent: maintenance verbs are allowed to change representation, not meaning.
  /// </summary>
  public void RequireEquivalentTo(ArchiveSemanticManifest other) {
    ArgumentNullException.ThrowIfNull(other);
    if (this.Entries.Count != other.Entries.Count)
      throw new InvalidOperationException(
        $"Rebuild changed the entry count ({this.Entries.Count} -> {other.Entries.Count}).");

    RequireDictionaryEqual("container flags", this.ContainerFlags, other.ContainerFlags);

    for (var i = 0; i < this.Entries.Count; ++i) {
      var expected = this.Entries[i];
      var actual = other.Entries[i];
      if (!string.Equals(expected.Name, actual.Name, StringComparison.Ordinal))
        throw Mismatch(expected.Name, "path/name", expected.Name, actual.Name);
      if (expected.IsDirectory != actual.IsDirectory)
        throw Mismatch(expected.Name, "directory kind", expected.IsDirectory, actual.IsDirectory);
      if (expected.Length != actual.Length)
        throw Mismatch(expected.Name, "logical length", expected.Length, actual.Length);
      if (!string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal))
        throw Mismatch(expected.Name, "content hash", expected.Sha256, actual.Sha256);
      if (expected.IsEncrypted != actual.IsEncrypted)
        throw Mismatch(expected.Name, "encryption flag", expected.IsEncrypted, actual.IsEncrypted);
      if (expected.LastModified != actual.LastModified)
        throw Mismatch(expected.Name, "last-modified timestamp", expected.LastModified, actual.LastModified);
      if (expected.CreationTime != actual.CreationTime)
        throw Mismatch(expected.Name, "creation timestamp", expected.CreationTime, actual.CreationTime);
      if (expected.Attributes != actual.Attributes)
        throw Mismatch(expected.Name, "attributes", expected.Attributes, actual.Attributes);
      if (expected.IsSymlink != actual.IsSymlink
          || !string.Equals(expected.LinkTarget, actual.LinkTarget, StringComparison.Ordinal)
          || expected.TargetSize != actual.TargetSize)
        throw new InvalidOperationException($"Rebuild changed symbolic-link semantics for '{expected.Name}'.");
      if (!string.Equals(expected.HardLinkTarget, actual.HardLinkTarget, StringComparison.Ordinal))
        throw Mismatch(expected.Name, "hard-link target", expected.HardLinkTarget, actual.HardLinkTarget);
      if (!expected.SparseExtents.SequenceEqual(actual.SparseExtents))
        throw new InvalidOperationException($"Rebuild changed sparse extents for '{expected.Name}'.");
      RequireDictionaryEqual($"entry flags for '{expected.Name}'", expected.SemanticFlags, actual.SemanticFlags);
    }
  }

  private static InvalidOperationException Mismatch<T>(string name, string field, T expected, T actual)
    => new($"Rebuild changed {field} for '{name}' ({expected} -> {actual}).");

  private static void RequireDictionaryEqual(
      string label,
      IReadOnlyDictionary<string, string> expected,
      IReadOnlyDictionary<string, string> actual) {
    if (expected.Count != actual.Count)
      throw new InvalidOperationException($"Rebuild changed {label}.");
    foreach (var (key, value) in expected)
      if (!actual.TryGetValue(key, out var other)
          || !string.Equals(value, other, StringComparison.Ordinal))
        throw new InvalidOperationException($"Rebuild changed {label} ('{key}').");
  }
}
