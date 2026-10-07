#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;
using FileFormat.Zip;

namespace FileFormat.Fla;

/// <summary>
/// Adobe Flash / Animate .fla source file. Two runtime variants are supported:
/// the classic pre-CS4 OLE2 Compound File variant (CFB), and the CS5+ XFL
/// variant which is a plain ZIP container. Detection is by the first bytes.
/// Both are surfaced as archives: CFB streams become <c>streams/{name}.bin</c>,
/// while ZIP/XFL members retain their paths. Creation packs an XFL project folder
/// into a ZIP/XFL document; the legacy CFB variant remains read-only.
/// Uses compound extension <c>.fla</c> with empty magic to avoid conflicting
/// with DOC/ZIP descriptors that own the generic magics.
///
/// References:
/// <list type="bullet">
///   <item><description>Adobe "Flash Professional XFL" format documentation (CS5-era) — the ZIP-based variant</description></item>
///   <item><description><c>https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/</c> — [MS-CFB] — Compound File Binary (the pre-CS4 OLE2 variant's container)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Adobe_Animate</c> — application background</description></item>
///   <item><description><c>https://blogs.adobe.com/digitalmedia/2010/05/the_xfl_file_format_explained/</c> — Adobe — a compressed FLA is the zipped XFL folder</description></item>
///   <item><description>Compressed FLA files saved by Flash Professional CS6 (JPEXS FFDec test data, see <c>Compression.Tests/Fla/ReferenceVectors</c>) — folder entries, and the stored 25-byte <c>mimetype</c> member (<c>application/vnd.adobe.xfl</c>) written last; observed, not in an Adobe specification</description></item>
/// </list>
/// </summary>
public sealed class FlaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var sig = new byte[8];
    if (archive.Read(sig, 0, Math.Min(8, (int)Math.Min(8, archive.Length))) < 4)
      return [];
    // CFB variant
    if (sig[0] == 0xD0 && sig[1] == 0xCF && sig[2] == 0x11 && sig[3] == 0xE0)
      return FileFormat.Msi.CfbLayoutMap.Enumerate(archive);
    // ZIP/XFL variant
    if (sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04)
      return FileFormat.Zip.ZipLayoutMap.Enumerate(archive);
    return [];
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Fla";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Flash/Animate FLA";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".fla";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [".fla"];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate"), new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "Adobe Flash/Animate FLA source file. Supports OLE2 (pre-CS4) and ZIP-based " +
    "XFL (CS5+) variants; detected by first bytes.";

  private enum Variant { Unknown, Cfb, Xfl }

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    List<(string Name, byte[] Data)> entries;
    try {
      entries = BuildEntries(stream);
    } catch {
      entries = [];
    }
    if (entries.Count > 0 && Detect(entries[0].Data) == Variant.Xfl) {
      var result = new List<ArchiveEntryInfo> {
        new(0, "FULL.fla", entries[0].Data.LongLength, entries[0].Data.LongLength, "stored", false, false, null),
        new(1, "metadata.ini", entries[1].Data.LongLength, entries[1].Data.LongLength, "stored", false, false, null),
      };
      try {
        using var zipStream = new MemoryStream(entries[0].Data, writable: false);
        using var reader = new FileFormat.Zip.ZipReader(zipStream);
        result.AddRange(reader.Entries.Select((entry, i) => new ArchiveEntryInfo(
          i + 2, entry.FileName, entry.UncompressedSize, entry.CompressedSize,
          entry.CompressionMethod switch {
            FileFormat.Zip.ZipCompressionMethod.Store => "stored",
            FileFormat.Zip.ZipCompressionMethod.Deflate => "deflate",
            _ => entry.CompressionMethod.ToString(),
          }, entry.IsDirectory, entry.IsEncrypted, entry.LastModified)));
        return result;
      } catch {
        // Retain the opaque-file view for damaged or unsupported ZIP variants.
      }
    }
    return entries.Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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
    if (entries.Count > 0 && Detect(entries[0].Data) == Variant.Xfl) {
      using var source = new MemoryStream(entries[0].Data, writable: false);
      using var archive = new ZipArchive(source, ZipArchiveMode.Read);
      foreach (var entry in archive.Entries) {
        if (files != null && files.Length > 0 && !MatchesFilter(entry.FullName, files)) continue;
        var safeName = entry.FullName.Replace('\\', '/').Trim('/');
        if (safeName.Contains("..", StringComparison.Ordinal)) safeName = Path.GetFileName(safeName);
        if (safeName.Length == 0) continue;
        var path = Path.Combine(outputDir, safeName.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) {
          // Folder entries (Flash writes 'LIBRARY/' even for an empty library) become folders.
          Directory.CreateDirectory(path);
          continue;
        }
        if (File.Exists(path)) File.SetLastWriteTimeUtc(path, entry.LastWriteTime.UtcDateTime);
      }
    }
  }

  /// <summary>Creates a ZIP-based XFL FLA from its project files.</summary>
  /// <remarks>
  /// The OLE2 variant is intentionally read-only: its container can be written, but the
  /// legacy Flash document streams and their interrelationships are not reconstructible
  /// from a flat list of files. ZIP entry data and paths are preserved for XFL projects.
  /// </remarks>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite)
      throw new ArgumentException("The output stream must be writable.", nameof(output));
    if (options.Password is not null)
      throw new NotSupportedException("Encrypted FLA entries are not supported.");

    var method = options.MethodName?.Trim().ToLowerInvariant();
    var compression = method switch {
      null or "" or "deflate" => GetDeflateLevel(options.Level),
      "stored" when options.Level is null => CompressionLevel.NoCompression,
      "stored" => throw new ArgumentException("The compression level only applies to DEFLATE entries.", nameof(options)),
      _ => throw new NotSupportedException($"FLA compression method '{options.MethodName}' is not supported."),
    };

    if (!inputs.Any(i => !i.IsDirectory && string.Equals(
          i.ArchiveName.Replace('\\', '/').TrimStart('/'), "DOMDocument.xml", StringComparison.OrdinalIgnoreCase)))
      throw new ArgumentException("A compressed XFL FLA must contain a root DOMDocument.xml project document.", nameof(inputs));

    var suppliedMimetype = inputs.FirstOrDefault(i => !i.IsDirectory && IsRootMimetype(i.ArchiveName));
    if (suppliedMimetype != null
        && !suppliedMimetype.ReadContent().AsSpan().SequenceEqual(XflMimetype))
      throw new ArgumentException($"An XFL 'mimetype' entry must contain exactly '{Encoding.ASCII.GetString(XflMimetype)}'.", nameof(inputs));

    using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

    foreach (var input in inputs) {
      var name = input.ArchiveName.Replace('\\', '/').Trim('/');
      if (string.IsNullOrWhiteSpace(name) || IsRootMimetype(name))
        continue;
      if (input.IsDirectory) {
        // Flash keeps folder entries such as 'LIBRARY/' even when the folder is empty.
        archive.CreateEntry(name + "/", CompressionLevel.NoCompression).LastWriteTime = GetInputTimestamp(input);
        continue;
      }
      var entry = archive.CreateEntry(name, compression);
      entry.LastWriteTime = GetInputTimestamp(input);
      using var target = entry.Open();
      target.Write(input.ReadContent());
    }

    // Flash Professional CS6 writes the package type as an uncompressed 'mimetype' member, and
    // writes it last (see the reference vectors next to this format's tests).
    var mimetypeEntry = archive.CreateEntry(MimetypeName, CompressionLevel.NoCompression);
    mimetypeEntry.LastWriteTime = suppliedMimetype is null ? ZipEpoch : GetInputTimestamp(suppliedMimetype);
    using (var target = mimetypeEntry.Open())
      target.Write(XflMimetype);
  }

  private static readonly DateTimeOffset ZipEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private const string MimetypeName = "mimetype";

  // 25 bytes, no line terminator: the size real CS5.5+ FLA listings show for this member.
  private static ReadOnlySpan<byte> XflMimetype => "application/vnd.adobe.xfl"u8;

  private static bool IsRootMimetype(string archiveName)
    => string.Equals(archiveName.Replace('\\', '/').TrimStart('/'), MimetypeName, StringComparison.Ordinal);

  private static DateTimeOffset GetInputTimestamp(ArchiveInputInfo input) {
    if (input.InMemoryContent is null && (File.Exists(input.FullPath) || Directory.Exists(input.FullPath))) {
      var timestamp = new DateTimeOffset(File.GetLastWriteTime(input.FullPath));
      if (timestamp < ZipEpoch) return ZipEpoch;
      if (timestamp > new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero))
        return new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);
      return timestamp;
    }
    return ZipEpoch;
  }

  // System.IO.Compression exposes effort tiers rather than every numeric deflate level.
  private static CompressionLevel GetDeflateLevel(int? level) => level switch {
    null or >= 4 and <= 6 => CompressionLevel.Optimal,
    >= 0 and <= 3 => CompressionLevel.Fastest,
    >= 7 and <= 9 => CompressionLevel.SmallestSize,
    _ => throw new ArgumentOutOfRangeException(nameof(level), level, "FLA compression level must be between 0 and 9."),
  };

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
          using var archive = new ZipReader(zipMs, leaveOpen: false);
          foreach (var entry in archive.Entries) {
            // Skip directory entries (name ends in a slash).
            if (entry.IsDirectory)
              continue;
            entries.Add((entry.FileName, archive.ExtractEntry(entry)));
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
