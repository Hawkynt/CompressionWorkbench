namespace Compression.Registry;

/// <summary>
/// Shared plumbing for audio containers surfaced as pseudo-archives. Every audio
/// descriptor exposes the same entry shape — a <c>FULL.&lt;ext&gt;</c> blob (Kind
/// <c>Track</c>), one mono <c>&lt;CHANNEL&gt;.wav</c> per decoded channel (Kind
/// <c>Channel</c>), and ancillary tag/metadata blobs (Kind <c>Tag</c>) — so the
/// listing, on-disk extraction and single-entry streaming are identical. A descriptor
/// builds the <see cref="Entry"/> list (the format-specific part) and delegates the
/// rest here.
/// </summary>
public static class AudioPseudoArchive {

  /// <summary>One surfaced pseudo-archive entry with its display <paramref name="Kind"/> and codec <paramref name="Method"/>.</summary>
  public readonly record struct Entry(string Name, string Kind, byte[] Data, string Method = "stored");

  /// <summary>Projects built entries into <see cref="ArchiveEntryInfo"/> rows for listing.</summary>
  public static List<ArchiveEntryInfo> List(IReadOnlyList<Entry> entries)
    => entries.Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: e.Method, IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>Writes the entries to <paramref name="outputDir"/>, honouring an optional name filter.</summary>
  public static void Extract(IReadOnlyList<Entry> entries, string outputDir, string[]? files) {
    foreach (var e in entries) {
      if (files is { Length: > 0 } && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>Streams a single named entry to <paramref name="output"/>.</summary>
  public static void ExtractEntry(IReadOnlyList<Entry> entries, string entryName, Stream output) {
    foreach (var e in entries)
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }
}
