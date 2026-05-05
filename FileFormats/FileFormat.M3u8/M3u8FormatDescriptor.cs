#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.M3u8;

/// <summary>
/// Pseudo-archive descriptor for HTTP Live Streaming M3U8 playlists (RFC 8216).
/// Surfaces the parsed manifest as <c>metadata.ini</c>, <c>playlist.txt</c>
/// (verbatim source), and <c>segments.txt</c> (one entry per variant or segment).
/// </summary>
public sealed class M3u8FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "M3u8";
  public string DisplayName => "HLS Playlist (M3U8)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".m3u8";
  public IReadOnlyList<string> Extensions => [".m3u8", ".m3u"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("#EXTM3U"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "HTTP Live Streaming playlist manifest (master variant list or media segment list).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false,
      LastModified: null, Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input))
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var text = Encoding.UTF8.GetString(ms.ToArray());
    var playlist = M3u8Reader.Read(text);

    return [
      ("metadata.ini", "Tag", BuildMetadata(playlist)),
      ("playlist.txt", "Tag", Encoding.UTF8.GetBytes(playlist.RawText)),
      ("segments.txt", "Tag", BuildSegmentsList(playlist)),
    ];
  }

  private static byte[] BuildMetadata(M3u8Reader.Playlist p) {
    var sb = new StringBuilder();
    sb.AppendLine("[m3u8]");
    sb.Append("variant = ").AppendLine(p.IsMaster ? "master" : "media");
    if (p.Version.HasValue) sb.Append("version = ").Append(p.Version.Value).Append('\n');
    if (p.TargetDurationSeconds.HasValue) sb.Append("target_duration_seconds = ").Append(p.TargetDurationSeconds.Value).Append('\n');
    if (p.MediaSequence.HasValue) sb.Append("media_sequence = ").Append(p.MediaSequence.Value).Append('\n');
    if (!string.IsNullOrEmpty(p.PlaylistType)) sb.Append("playlist_type = ").AppendLine(p.PlaylistType);
    sb.Append("end_list = ").AppendLine(p.EndList ? "true" : "false");

    if (p.IsMaster) {
      sb.Append("variant_count = ").Append(p.Variants.Count).Append('\n');
      for (var i = 0; i < p.Variants.Count; i++) {
        var v = p.Variants[i];
        sb.Append(CultureInfo.InvariantCulture, $"\n[variant_{i}]\n");
        sb.Append("uri = ").AppendLine(v.Uri);
        foreach (var (k, val) in v.Attributes)
          sb.Append(k.ToLowerInvariant()).Append(" = ").AppendLine(val);
      }
    } else {
      sb.Append("segment_count = ").Append(p.Segments.Count).Append('\n');
      var total = p.Segments.Sum(s => s.DurationSeconds);
      sb.Append(CultureInfo.InvariantCulture, $"total_duration_seconds = {total:F3}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildSegmentsList(M3u8Reader.Playlist p) {
    var sb = new StringBuilder();
    if (p.IsMaster) {
      // Master: one line per variant with a key attribute summary.
      foreach (var v in p.Variants) {
        v.Attributes.TryGetValue("BANDWIDTH", out var bw);
        v.Attributes.TryGetValue("RESOLUTION", out var res);
        v.Attributes.TryGetValue("CODECS", out var codecs);
        sb.Append(v.Uri);
        sb.Append("\tbandwidth=").Append(bw ?? "");
        sb.Append("\tresolution=").Append(res ?? "");
        sb.Append("\tcodecs=").Append(codecs ?? "");
        sb.Append('\n');
      }
    } else {
      foreach (var s in p.Segments)
        sb.Append(s.Uri).Append('\n');
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
