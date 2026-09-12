#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ghost;

/// <summary>
/// Rebuild-based in-place mutation helpers for the Ghost 11.x / 12.x
/// record container. The modify path is read-extract-rebuild — Ghost's
/// record framing chains compressed-block spans with no per-record
/// length-of-payload field, so per-record patching would require
/// rewriting every downstream offset. Rebuilding from the live entry
/// list against the original compression mode + encryption state is
/// simpler and verifiably preserves the FE EF + 0x012F18D8 framing.
/// </summary>
/// <remarks>
/// <para>
/// Compression mode + encryption state are read off the source image so
/// the rebuilt image keeps the same FastLZ / zlib level / cipher key as
/// the input. The caller's <see cref="GhostFormatDescriptor.Capabilities"/>
/// surface advertises <see cref="FormatCapabilities.CanModify"/>; the
/// interface methods on the descriptor delegate here with
/// <c>password=null</c>, which works for unencrypted images.
/// Password-protected images go through the static overloads here that
/// accept the password explicitly — the IArchiveModifiable contract
/// itself has no password parameter.
/// </para>
/// <para>
/// Synthetic entries surfaced by <see cref="GhostReader"/> for diagnostic
/// purposes — <c>metadata.ini</c>, <c>partitionN.error.txt</c>,
/// <c>ghost-image.gho.bin</c>, <c>ghost-image.ghs.bin</c>,
/// <c>dump-head.bin</c>, <c>dump-body.bin</c> — are filtered out before
/// rebuild so they never round-trip into the new image as payloads. Pre-3.0
/// (Ghost 1.x / 2.x DOS-era) images are rejected up front because the
/// reader exposes only diagnostic Stage-1 surface for them; mutating them
/// would lose data.
/// </para>
/// </remarks>
public static class GhostModifier {

  /// <summary>
  /// Adds (or replaces by name) entries inside an existing Ghost image.
  /// The compression mode + encryption state of the source are preserved.
  /// </summary>
  /// <param name="archive">Stream to rewrite. Must be readable, writable and seekable.</param>
  /// <param name="inputs">Entries to add. Existing entries with the same name are replaced.</param>
  /// <param name="password">Required when the source image is encrypted. Ignored otherwise.</param>
  public static void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs, string? password = null) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    var snapshot = ReadSnapshot(archive, password);
    var newPayloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var appendOrder = new List<string>();
    foreach (var (name, data) in FilesOnly(inputs)) {
      if (!newPayloads.ContainsKey(name)) appendOrder.Add(name);
      newPayloads[name] = data;
    }

    // Replace-in-place when an existing entry's name matches — preserves the
    // partition ordering so Ghost's positional partitionN.bin labels track
    // the user's intent. Names not present in the existing list are appended.
    var combined = new List<(string Name, byte[] Data)>(snapshot.Entries.Count + newPayloads.Count);
    var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in snapshot.Entries) {
      if (newPayloads.TryGetValue(e.Name, out var replacement)) {
        combined.Add((e.Name, replacement));
        consumed.Add(e.Name);
      } else {
        combined.Add(e);
      }
    }
    foreach (var name in appendOrder) {
      if (consumed.Contains(name)) continue;
      combined.Add((name, newPayloads[name]));
    }

    var rebuilt = BuildImage(snapshot, combined);
    OverwriteStream(archive, rebuilt);
  }

  /// <summary>
  /// Removes named entries from an existing Ghost image. The rebuild starts
  /// from a fresh byte buffer so the old payload bytes leave no forensic
  /// trace; the compression mode + encryption state of the source are
  /// preserved.
  /// </summary>
  public static void Remove(Stream archive, string[] entryNames, string? password = null) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);

    var snapshot = ReadSnapshot(archive, password);
    var skip = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);

    var kept = new List<(string Name, byte[] Data)>(snapshot.Entries.Count);
    foreach (var e in snapshot.Entries) {
      if (skip.Contains(e.Name)) continue;
      kept.Add(e);
    }

    var rebuilt = BuildImage(snapshot, kept);
    OverwriteStream(archive, rebuilt);
  }

  /// <summary>
  /// Replaces the named entry's payload with <paramref name="newContent"/>.
  /// Sugar for <see cref="Remove"/> followed by <see cref="Add"/>. The named
  /// entry must already exist or the call is treated as an Add.
  /// </summary>
  public static void Replace(Stream archive, string entryName, byte[] newContent, string? password = null) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    ArgumentNullException.ThrowIfNull(newContent);

    var snapshot = ReadSnapshot(archive, password);
    var replaced = new List<(string Name, byte[] Data)>(snapshot.Entries.Count);
    var found = false;
    foreach (var e in snapshot.Entries) {
      if (string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) {
        replaced.Add((entryName, newContent));
        found = true;
      } else {
        replaced.Add(e);
      }
    }
    if (!found) replaced.Add((entryName, newContent));

    var rebuilt = BuildImage(snapshot, replaced);
    OverwriteStream(archive, rebuilt);
  }

  // ── Internal pipeline ──────────────────────────────────────────────

  /// <summary>
  /// Snapshot of a parsed Ghost image: the live entries plus the codec
  /// settings the rebuilt image must keep using.
  /// </summary>
  private sealed class GhostImageSnapshot {
    public byte Compression { get; init; }
    public bool IsEncrypted { get; init; }
    public string? Password { get; init; }
    public List<(string Name, byte[] Data)> Entries { get; init; } = [];
  }

  private static GhostImageSnapshot ReadSnapshot(Stream archive, string? password) {
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("Ghost modify: archive stream must be readable, writable and seekable.", nameof(archive));

    archive.Position = 0;
    var r = new GhostReader(archive, password: password);

    if (r.GenerationHint != GhostGenerationHint.Modern11Plus)
      throw new NotSupportedException(
        "Ghost modify: only Ghost 3.0+ (modern record container) images are mutable. " +
        "Pre-3.0 and unknown FE EF variants are R/O — use Symantec Ghost Explorer to convert.");

    if (r.IsEncrypted && string.IsNullOrEmpty(password))
      throw new InvalidDataException(
        "Ghost modify: image is encrypted; supply the password via the GhostModifier overload that accepts one.");

    var entries = new List<(string Name, byte[] Data)>();
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (IsSyntheticEntryName(e.Name)) continue;
      entries.Add((e.Name, e.Data));
    }

    return new GhostImageSnapshot {
      Compression = r.HeaderCompression,
      IsEncrypted = r.IsEncrypted,
      Password = password,
      Entries = entries
    };
  }

  /// <summary>
  /// Names <see cref="GhostReader"/> synthesises for diagnostic purposes —
  /// not real payloads, must not round-trip back into the image.
  /// </summary>
  private static bool IsSyntheticEntryName(string name)
    => name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)
       || name.Equals("ghost-image.gho.bin", StringComparison.OrdinalIgnoreCase)
       || name.Equals("ghost-image.ghs.bin", StringComparison.OrdinalIgnoreCase)
       || name.Equals("dump-head.bin", StringComparison.OrdinalIgnoreCase)
       || name.Equals("dump-body.bin", StringComparison.OrdinalIgnoreCase)
       || name.EndsWith(".error.txt", StringComparison.OrdinalIgnoreCase);

  private static byte[] BuildImage(GhostImageSnapshot snapshot, IReadOnlyList<(string Name, byte[] Data)> entries) {
    using var ms = new MemoryStream();
    using (var w = new GhostWriter(ms, snapshot.Compression,
                                   password: snapshot.IsEncrypted ? snapshot.Password : null,
                                   leaveOpen: true)) {
      // Track 0 first if present; everything else is a partition record. This
      // mirrors the descriptor's Create() classification so the rebuilt image
      // re-lists with the same track0.bin / partitionN.bin entry names.
      (string Name, byte[] Data)? track0 = null;
      var partitions = new List<(string Name, byte[] Data)>();
      foreach (var e in entries) {
        if (e.Name.Equals("track0.bin", StringComparison.OrdinalIgnoreCase) && track0 == null)
          track0 = e;
        else
          partitions.Add(e);
      }

      if (track0.HasValue)
        w.WriteTrack0(track0.Value.Data, sectors: 63);

      foreach (var p in partitions)
        w.WritePartition(p.Data);

      w.WriteEnd();
    }
    return ms.ToArray();
  }

  private static void OverwriteStream(Stream archive, byte[] rebuilt) {
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }
}
