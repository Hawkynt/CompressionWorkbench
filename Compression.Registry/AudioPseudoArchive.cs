namespace Compression.Registry;

/// <summary>
/// Shared plumbing for audio containers surfaced as pseudo-archives. The model
/// separates the CONTAINER from the DATA it carries: the pseudo-archive is the
/// container format itself, and every listed entry is a pseudo-file of carried
/// data. Kinds encode that distinction —
/// <list type="bullet">
///   <item><c>Container</c> — the byte-exact original container (<c>FULL.&lt;ext&gt;</c>);
///     round-trips the file unchanged.</item>
///   <item><c>Stream</c> — a carried elementary bitstream (e.g. an Ogg logical
///     stream's packets) still in its coded form.</item>
///   <item><c>Track</c> — a carried audio/video track in multi-track containers.</item>
///   <item><c>Channel</c> — one decoded speaker as a playable mono PCM WAV
///     (named per <c>Codec.Pcm.ChannelLayout</c>, mono through 22.2 and beyond).</item>
///   <item><c>Tag</c> — carried metadata (comments, ID3, bext, …).</item>
/// </list>
/// A descriptor builds the <see cref="Entry"/> list (the format-specific part) and
/// delegates listing, on-disk extraction and single-entry streaming here.
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
