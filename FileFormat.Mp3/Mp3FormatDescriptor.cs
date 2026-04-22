#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Mp3;

/// <summary>
/// Surfaces an MP3 file as an archive whose layout is shaped for human/tool use:
/// one <c>FULL.mp3</c>, one <c>metadata.ini</c> carrying all text/URL/comment
/// fields as <c>key=value</c>, one <c>cover.&lt;ext&gt;</c> per APIC picture, and
/// <c>lyrics.txt</c> for USLT. When both ID3v1 and ID3v2 are present the archive
/// surfaces <c>id3v1/metadata.ini</c> + <c>id3v2/metadata.ini</c> so callers can
/// see which fields come from which tag version.
/// <para>
/// Audio decode (Layer III IMDCT + polyphase synthesis) is intentionally out of
/// scope — that's a separate multi-week project. The <c>FULL.mp3</c> entry carries
/// the original audio frames unchanged.
/// </para>
/// </summary>
public sealed class Mp3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {
  public string Id => "Mp3";
  public string DisplayName => "MP3 (MPEG audio)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mp3";
  public IReadOnlyList<string> Extensions => [".mp3", ".mp2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ID3"u8.ToArray(), Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "MP3 audio; ID3v1/v2 surfaced as metadata.ini + cover.*";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.mp3", "Track", blob),
    };

    var (_, v2Frames) = new Id3v2Reader().Read(blob);
    var v1Tag = new Id3v1Reader().Read(blob);
    var hasV2 = v2Frames.Count > 0;
    var hasV1 = v1Tag != null;

    // Choose where the "primary" metadata.ini lives: at the root for single-tag files,
    // and duplicated under id3v1/ + id3v2/ sub-folders for dual-tagged files (where v2
    // is the authoritative root metadata.ini).
    if (hasV1 && hasV2) {
      entries.Add(("id3v2/metadata.ini", "Tag", BuildV2MetadataIni(v2Frames)));
      entries.Add(("id3v1/metadata.ini", "Tag", BuildV1MetadataIni(v1Tag!)));
      entries.Add(("metadata.ini", "Tag", BuildV2MetadataIni(v2Frames)));
    } else if (hasV2) {
      entries.Add(("metadata.ini", "Tag", BuildV2MetadataIni(v2Frames)));
    } else if (hasV1) {
      entries.Add(("metadata.ini", "Tag", BuildV1MetadataIni(v1Tag!)));
    }

    // Extract APIC cover images.
    var apics = v2Frames.Where(f => f.Id == "APIC").ToList();
    for (var i = 0; i < apics.Count; ++i) {
      var a = apics[i];
      var ext = MimeToExtension(a.MimeType);
      var name = i == 0
        ? $"cover{ext}"
        : $"cover_{SanitizeForPath(a.Description, fallback: $"image_{i}")}{ext}";
      entries.Add((name, "Tag", a.Payload));
    }

    // USLT (unsynchronised lyrics) — COMM also carries text but that stays in metadata.ini.
    var lyrics = v2Frames.FirstOrDefault(f => f.Id == "USLT");
    if (lyrics != null)
      entries.Add(("lyrics.txt", "Tag", lyrics.Payload));

    return entries;
  }

  private static byte[] BuildV2MetadataIni(IReadOnlyList<Id3v2Reader.Frame> frames) {
    var sb = new StringBuilder();
    sb.AppendLine("; ID3v2 metadata");
    foreach (var f in frames) {
      if (f.Id == "APIC" || f.Id == "USLT") continue;  // have dedicated entries
      // Text frames (TIT2, TPE1, TALB, TDRC, TCON, TRCK, …) carry UTF-8 text bytes.
      // URL frames (WOAF, WORS, …) carry a URL as ASCII/UTF-8.
      // Other binary frames are skipped (not expressible as ini).
      if (f.Id.StartsWith('T') || f.Id.StartsWith('W')) {
        var text = Encoding.UTF8.GetString(f.Payload).TrimEnd('\0').Trim();
        sb.Append(f.Id).Append('=').AppendLine(text);
      } else if (f.Id == "COMM") {
        var text = Encoding.UTF8.GetString(f.Payload).TrimEnd('\0').Trim();
        var descKey = string.IsNullOrEmpty(f.Description) ? "COMM" : $"COMM/{f.Description}";
        sb.Append(descKey).Append('=').AppendLine(text);
      }
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildV1MetadataIni(Id3v1Reader.Tag t) {
    var sb = new StringBuilder();
    sb.AppendLine("; ID3v1 metadata");
    if (!string.IsNullOrEmpty(t.Title)) sb.Append("title=").AppendLine(t.Title);
    if (!string.IsNullOrEmpty(t.Artist)) sb.Append("artist=").AppendLine(t.Artist);
    if (!string.IsNullOrEmpty(t.Album)) sb.Append("album=").AppendLine(t.Album);
    if (!string.IsNullOrEmpty(t.Year)) sb.Append("year=").AppendLine(t.Year);
    if (!string.IsNullOrEmpty(t.Comment)) sb.Append("comment=").AppendLine(t.Comment);
    if (t.Track.HasValue) sb.Append("track=").AppendLine(t.Track.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    sb.Append("genre_code=").AppendLine(t.GenreByte.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  // ── IArchiveCreatable: assemble MP3 from dropped metadata.ini / cover.* / FULL.mp3 ─────

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    byte[]? fullAudioPayload = null;
    byte[]? coverBytes = null;
    var textFrames = new Dictionary<string, string>(StringComparer.Ordinal);
    string? lyrics = null;

    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs)) {
      var lowered = name.Replace('\\', '/').ToLowerInvariant();
      var fileName = System.IO.Path.GetFileName(lowered);

      if (fileName == "full.mp3") {
        fullAudioPayload = data;
      } else if (fileName.StartsWith("cover") && IsKnownImageExtension(fileName)) {
        coverBytes = data;
      } else if (fileName == "metadata.ini") {
        // Only the root metadata.ini feeds the canonical tag; id3v1/ and id3v2/ subfolders
        // are v1/v2 archive views and are not used on re-creation (we always emit v2).
        var dir = System.IO.Path.GetDirectoryName(lowered)?.Replace('\\', '/') ?? "";
        if (dir == "" || dir == "id3v2") ParseIni(data, textFrames);
      } else if (fileName == "lyrics.txt") {
        lyrics = Encoding.UTF8.GetString(data);
      }
    }

    // Build ID3v2 tag.
    var writer = new Id3v2Writer();
    foreach (var (id, value) in textFrames.Where(kvp => kvp.Key.StartsWith('T'))) {
      writer.AddText(id, value);
    }
    foreach (var (id, value) in textFrames.Where(kvp => kvp.Key.StartsWith('W'))) {
      writer.AddUrl(id, value);
    }
    if (coverBytes != null) writer.AddPicture(coverBytes);
    if (lyrics != null) writer.AddLyrics(lyrics);
    var tag = writer.Build();

    // Concatenate: ID3v2 tag + audio frames. Strip any existing ID3v2 tag from the input
    // FULL.mp3 to avoid double-tagging.
    output.Write(tag);
    if (fullAudioPayload != null) {
      var audioStart = StripExistingId3v2(fullAudioPayload);
      output.Write(fullAudioPayload, audioStart, fullAudioPayload.Length - audioStart);
    }
  }

  private static bool IsKnownImageExtension(string fileName)
    => fileName.EndsWith(".jpg") || fileName.EndsWith(".jpeg") || fileName.EndsWith(".png") ||
       fileName.EndsWith(".gif") || fileName.EndsWith(".webp");

  private static void ParseIni(byte[] data, Dictionary<string, string> frames) {
    var text = Encoding.UTF8.GetString(data);
    foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
      var trimmed = line.Trim();
      if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#') continue;
      var eq = trimmed.IndexOf('=');
      if (eq <= 0) continue;
      var key = trimmed[..eq].Trim();
      var value = trimmed[(eq + 1)..].Trim();
      if (key.Length == 4) frames[key] = value;
    }
  }

  private static int StripExistingId3v2(byte[] mp3) {
    if (mp3.Length < 10 || mp3[0] != 'I' || mp3[1] != 'D' || mp3[2] != '3') return 0;
    var tagSize = (mp3[6] & 0x7F) << 21 | (mp3[7] & 0x7F) << 14 | (mp3[8] & 0x7F) << 7 | (mp3[9] & 0x7F);
    return 10 + tagSize;
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "MP3 archive accepts: metadata.ini, cover.jpg/png/gif/webp, lyrics.txt, FULL.mp3";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = System.IO.Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = System.IO.Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";

    if (dir is "" or "id3v1" or "id3v2") {
      if (name == "metadata.ini" || name == "lyrics.txt" || name == "full.mp3") {
        reason = null; return true;
      }
      if (name.StartsWith("cover") &&
          (name.EndsWith(".jpg") || name.EndsWith(".jpeg") || name.EndsWith(".png") ||
           name.EndsWith(".gif") || name.EndsWith(".webp"))) {
        reason = null; return true;
      }
    }
    reason = $"not an MP3-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  private static string MimeToExtension(string mime) => mime.ToLowerInvariant() switch {
    "image/jpeg" => ".jpg",
    "image/jpg" => ".jpg",
    "image/png" => ".png",
    "image/gif" => ".gif",
    "image/webp" => ".webp",
    _ => ".bin",
  };

  private static string SanitizeForPath(string s, string fallback) {
    if (string.IsNullOrEmpty(s)) return fallback;
    var sb = new StringBuilder(Math.Min(s.Length, 40));
    foreach (var c in s) {
      if (sb.Length >= 40) break;
      if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
      else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
    }
    return sb.Length > 0 ? sb.ToString().Trim('_') : fallback;
  }
}
