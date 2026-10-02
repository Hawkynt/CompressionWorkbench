using System.Security.Cryptography;

namespace Compression.Registry;

/// <summary>
/// Everything a container's own reader says about its contents — every entry's name,
/// kind, length, SHA-256, modification time, encryption and link target, plus the
/// container-level properties a descriptor publishes — captured so that a maintenance
/// operation can be held to keeping all of it.
/// </summary>
/// <remarks>
/// <para>Comparing payloads alone lets an operation swap two equal-sized files, move a
/// file to another folder, drop an empty folder or reset every timestamp and still pass.
/// The manifest is keyed by path and carries the metadata, so none of that does.</para>
///
/// <para>It sees what the reader exposes and no more. A property the reader does not
/// report — a DOS attribute bit, an owner — is outside it, which is why the formats with
/// such properties are also checked against volumes the real tools made
/// (<c>MaintenancePreservesRealVolumesTests</c>).</para>
/// </remarks>
public sealed class ArchiveSemanticManifest {

  /// <summary>One entry as the reader reports it, plus the SHA-256 of its bytes (null for a folder).</summary>
  public sealed record Entry(
    string Name,
    bool IsDirectory,
    long Length,
    string? Sha256,
    DateTime? LastModified,
    bool IsEncrypted,
    bool IsSymlink,
    string? LinkTarget);

  private ArchiveSemanticManifest(IReadOnlyList<Entry> entries, IReadOnlyDictionary<string, string> containerProperties) {
    this.Entries = entries;
    this.ContainerProperties = containerProperties;
  }

  /// <summary>The entries, sorted by path (ordinal).</summary>
  public IReadOnlyList<Entry> Entries { get; }

  /// <summary>
  /// Container-level properties from <see cref="IArchiveSemanticMetadataProvider"/>
  /// (volume label, serial, UUID, …); empty when the format publishes none.
  /// </summary>
  public IReadOnlyDictionary<string, string> ContainerProperties { get; }

  /// <summary>
  /// Reads <paramref name="archive"/> through <paramref name="ops"/> and hashes every file.
  /// Entries the descriptor declares synthetic (<see cref="ISyntheticEntryNames"/>), entries
  /// of kind <c>Container</c> (a pseudo-archive's <c>FULL.*</c> rendering of the whole file)
  /// and <paramref name="excludedNames"/> are left out: they are views of the container, not
  /// contents of it, and an operation that rewrites the container changes them by definition.
  /// </summary>
  /// <exception cref="NotSupportedException">The reader lists one path twice; the
  /// name-addressed entry API cannot tell which of the two it hashed.</exception>
  public static ArchiveSemanticManifest Capture(
      Stream archive,
      IArchiveFormatOperations ops,
      string? password = null,
      IReadOnlySet<string>? excludedNames = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(ops);
    if (!archive.CanRead || !archive.CanSeek)
      throw new ArgumentException("Capturing a manifest needs a readable, seekable stream.", nameof(archive));

    var synthetic = (ops as ISyntheticEntryNames)?.SyntheticEntryNames;
    bool Excluded(string name) => (synthetic?.Contains(name) ?? false) || (excludedNames?.Contains(name) ?? false);

    archive.Position = 0;
    var listed = ops.List(archive, password)
      .Where(e => !Excluded(e.Name) && !string.Equals(e.Kind, ContainerKind, StringComparison.OrdinalIgnoreCase))
      .ToList();

    var entries = new List<Entry>(listed.Count);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var buffer = new byte[64 * 1024];
    foreach (var source in listed) {
      cancellationToken.ThrowIfCancellationRequested();
      var name = Normalize(source.Name);
      if (!seen.Add((source.IsDirectory ? "d:" : "f:") + name))
        throw new NotSupportedException(
          $"The container lists '{name}' twice; an entry addressed by name cannot be proven to survive.");

      string? digest = null;
      if (!source.IsDirectory) {
        archive.Position = 0;
        using var content = ops.OpenEntry(archive, source.Name, password);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int read;
        while ((read = content.Read(buffer, 0, buffer.Length)) > 0) {
          cancellationToken.ThrowIfCancellationRequested();
          hash.AppendData(buffer, 0, read);
        }
        digest = Convert.ToHexString(hash.GetHashAndReset());
      }

      entries.Add(new Entry(name, source.IsDirectory, source.IsDirectory ? 0 : source.OriginalSize, digest,
        source.LastModified, source.IsEncrypted, source.IsSymlink, source.LinkTarget));
    }
    entries.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name) is var c && c != 0
      ? c : a.IsDirectory.CompareTo(b.IsDirectory));

    IReadOnlyDictionary<string, string> properties = new Dictionary<string, string>(StringComparer.Ordinal);
    if (ops is IArchiveSemanticMetadataProvider provider) {
      archive.Position = 0;
      properties = new Dictionary<string, string>(provider.GetContainerProperties(archive), StringComparer.Ordinal);
    }

    archive.Position = 0;
    return new ArchiveSemanticManifest(entries, properties);
  }

  /// <summary>
  /// Every way <paramref name="after"/> differs from this manifest, one line each; empty
  /// when nothing the reader can see has changed. Encoding sizes, method names and entry
  /// order are not part of the comparison — those are what a maintenance operation may change.
  /// </summary>
  public IReadOnlyList<string> DifferencesTo(ArchiveSemanticManifest after) {
    ArgumentNullException.ThrowIfNull(after);
    var differences = new List<string>();

    foreach (var (key, value) in this.ContainerProperties)
      if (!after.ContainerProperties.TryGetValue(key, out var other))
        differences.Add($"container {key}: '{value}' is gone");
      else if (!string.Equals(value, other, StringComparison.Ordinal))
        differences.Add($"container {key}: '{value}' -> '{other}'");
    foreach (var key in after.ContainerProperties.Keys.Where(k => !this.ContainerProperties.ContainsKey(k)))
      differences.Add($"container {key}: appeared");

    static string Key(Entry e) => (e.IsDirectory ? "d:" : "f:") + e.Name;
    var before = this.Entries.ToDictionary(Key, StringComparer.Ordinal);
    var now = after.Entries.ToDictionary(Key, StringComparer.Ordinal);
    foreach (var (key, expected) in before) {
      if (!now.TryGetValue(key, out var actual)) {
        differences.Add($"{expected.Name}: gone");
        continue;
      }
      if (expected.Length != actual.Length) differences.Add($"{expected.Name}: length {expected.Length} -> {actual.Length}");
      if (!string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal)) differences.Add($"{expected.Name}: content changed");
      if (expected.LastModified is { } time && actual.LastModified != time)
        differences.Add($"{expected.Name}: modified {time:O} -> {actual.LastModified?.ToString("O") ?? "(none)"}");
      if (expected.IsEncrypted != actual.IsEncrypted) differences.Add($"{expected.Name}: encryption {expected.IsEncrypted} -> {actual.IsEncrypted}");
      if (expected.IsSymlink != actual.IsSymlink || !string.Equals(expected.LinkTarget, actual.LinkTarget, StringComparison.Ordinal))
        differences.Add($"{expected.Name}: link '{expected.LinkTarget}' -> '{actual.LinkTarget}'");
    }
    foreach (var (key, actual) in now)
      if (!before.ContainsKey(key))
        differences.Add($"{actual.Name}: appeared");
    return differences;
  }

  /// <summary>Throws <see cref="NotSupportedException"/> naming what changed when <paramref name="after"/> differs.</summary>
  public void RequireSameAs(ArchiveSemanticManifest after, string operation) {
    var differences = this.DifferencesTo(after);
    if (differences.Count == 0) return;
    const int Shown = 8;
    var listed = string.Join("; ", differences.Take(Shown)) + (differences.Count > Shown ? $"; and {differences.Count - Shown} more" : "");
    throw new NotSupportedException($"{operation} would not keep the contents intact ({listed}); refused, nothing was changed.");
  }

  /// <summary>The <see cref="ArchiveEntryInfo.Kind"/> pseudo-archives give the entry that renders the whole file.</summary>
  private const string ContainerKind = "Container";

  private static string Normalize(string name) => name.Replace('\\', '/').Trim('/');
}

/// <summary>
/// Optional: container-level properties a maintenance operation must keep — the volume
/// label, serial number or UUID — which belong to no single entry and so are invisible
/// to an entry listing.
/// </summary>
public interface IArchiveSemanticMetadataProvider {
  /// <summary>Stable key/value pairs describing <paramref name="archive"/>'s identity.</summary>
  IReadOnlyDictionary<string, string> GetContainerProperties(Stream archive);
}
