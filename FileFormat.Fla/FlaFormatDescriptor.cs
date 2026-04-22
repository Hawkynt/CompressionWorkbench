#pragma warning disable CS1591
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Fla;

/// <summary>
/// Adobe Flash / Animate .fla source file. Two runtime variants are supported:
/// the classic pre-CS4 OLE2 Compound File variant (CFB), and the CS5+ XFL
/// variant which is a plain ZIP container. Detection is by the first bytes.
/// Both are surfaced read-only: CFB streams become <c>streams/{name}.bin</c>,
/// ZIP entries are listed flatly by their path inside the archive.
/// Uses compound extension <c>.fla</c> with empty magic to avoid conflicting
/// with DOC/ZIP descriptors that own the generic magics.
/// </summary>
public sealed class FlaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Fla";
  public string DisplayName => "Flash/Animate FLA";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".fla";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [".fla"];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Adobe Flash/Animate FLA source file. Supports OLE2 (pre-CS4) and ZIP-based " +
    "XFL (CS5+) variants; detected by first bytes.";

  private enum Variant { Unknown, Cfb, Xfl }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    List<(string Name, byte[] Data)> entries;
    try {
      entries = BuildEntries(stream);
    } catch {
      entries = [];
    }
    return entries.Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    List<(string Name, byte[] Data)> entries;
    try {
      entries = BuildEntries(stream);
    } catch {
      entries = [];
    }
    foreach (var e in entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, byte[] Data)> {
      ("FULL.fla", blob),
    };

    var variant = Detect(blob);
    var meta = new StringBuilder();
    meta.AppendLine("; Flash FLA metadata");
    meta.Append("format=").AppendLine(variant switch {
      Variant.Cfb => "cfb",
      Variant.Xfl => "xfl",
      _ => "unknown",
    });

    switch (variant) {
      case Variant.Cfb: {
        meta.AppendLine("variant_detection=ole2_magic_D0CF11E0A1B11AE1");
        // Use MsiReader which wraps the CFB walker and exposes streams/storages.
        using var cfbMs = new MemoryStream(blob, writable: false);
        int count = 0;
        try {
          using var r = new FileFormat.Msi.MsiReader(cfbMs);
          foreach (var e in r.Entries) {
            if (e.IsDirectory) continue;
            var data = r.Extract(e);
            var safe = SafeStreamName(e.FullPath);
            entries.Add(($"streams/{safe}.bin", data));
            count++;
          }
        } catch {
          meta.AppendLine("cfb_parse_error=true");
        }
        meta.Append("cfb_stream_count=").AppendLine(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        break;
      }
      case Variant.Xfl: {
        meta.AppendLine("variant_detection=zip_magic_PK0304");
        using var zipMs = new MemoryStream(blob, writable: false);
        int count = 0;
        try {
          using var archive = new ZipArchive(zipMs, ZipArchiveMode.Read, leaveOpen: false);
          foreach (var entry in archive.Entries) {
            // Skip directory entries (zero-length name-ends-in-slash).
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
              continue;
            using var es = entry.Open();
            using var buf = new MemoryStream();
            es.CopyTo(buf);
            entries.Add((entry.FullName, buf.ToArray()));
            count++;
          }
        } catch {
          meta.AppendLine("zip_parse_error=true");
        }
        meta.Append("xfl_entry_count=").AppendLine(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        break;
      }
      default:
        meta.AppendLine("variant_detection=none");
        break;
    }

    entries.Insert(1, ("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString())));
    return entries;
  }

  private static Variant Detect(byte[] blob) {
    if (blob.Length >= 8
        && blob[0] == 0xD0 && blob[1] == 0xCF && blob[2] == 0x11 && blob[3] == 0xE0
        && blob[4] == 0xA1 && blob[5] == 0xB1 && blob[6] == 0x1A && blob[7] == 0xE1)
      return Variant.Cfb;
    if (blob.Length >= 4
        && blob[0] == 0x50 && blob[1] == 0x4B && blob[2] == 0x03 && blob[3] == 0x04)
      return Variant.Xfl;
    return Variant.Unknown;
  }

  private static string SafeStreamName(string path) {
    var cleaned = path.Replace('\\', '/').TrimStart('/');
    // CFB stream names often begin with control chars (e.g. 0x01, 0x05). Filter
    // to a printable subset for filesystem-safe on-disk entries.
    var sb = new StringBuilder(cleaned.Length);
    foreach (var c in cleaned) {
      if (c < 0x20 || c == '?' || c == '*' || c == ':' || c == '<' || c == '>' || c == '|' || c == '"')
        sb.Append('_');
      else
        sb.Append(c);
    }
    var result = sb.ToString();
    return string.IsNullOrEmpty(result) ? "unnamed" : result;
  }
}
