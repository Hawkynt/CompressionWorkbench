#pragma warning disable CS1591
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;
using Compression.Core.Deflate;
using FileFormat.Zip;

namespace FileFormat.Aff4;

/// <summary>
/// AFF4 (Advanced Forensic Format 4) container. An AFF4 volume is a ZIP (typically
/// ZIP64) holding an RDF metadata graph in <c>information.turtle</c>, a
/// <c>version.txt</c> marker, an optional <c>container.description</c>, and image
/// data streams under <c>aff4://&lt;uuid&gt;/</c> paths split into bevy/chunk
/// segments named with zero-padded indices (e.g. <c>00000000</c>, <c>00000000.index</c>).
///
/// <para>Logical images (AFF4-L, and the AFF4 v1.1 logical images pyaff4 writes) list and
/// extract as their files and folders, with data from ZipSegments, ImageStreams or in-metadata
/// streams; other members, and every member of a physical image, list as stored. Creation writes
/// AFF4-L ZipSegment volumes using Stored or Deflate. Extraction also offers a verbatim
/// <c>FULL.aff4</c> and <c>metadata.ini</c> distilled from the Turtle graph. Detection is
/// extension-driven (<c>.aff4</c>) so it does not steal generic ZIPs; malformed input degrades
/// to FULL + partial metadata without throwing.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/aff4/Standard</c> — AFF4 Standard v1.0 (ImageStream, storage layer, ZIP64)</description></item>
///   <item><description><c>inprogress/AFF4-L-StandardSpecification-v1.0.md</c> — AFF4-L v1.0 (ZipSegment streams, metadata and hashes)</description></item>
///   <item><description><c>https://github.com/aff4/pyaff4</c> — pyaff4 — canonical reference implementation, the oracle for reading and writing</description></item>
///   <item><description>Cohen, Garfinkel &amp; Schatz, "Extending the Advanced Forensic Format to accommodate multiple data sources, logical evidence, arbitrary information and forensic workflow" (DFRWS 2009) — the defining AFF4 paper</description></item>
/// </list>
/// </summary>
public sealed class Aff4FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Aff4";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Advanced Forensic Format 4 (AFF4)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>Options supported when writing an AFF4-L ZIP volume.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Method", "ZIP segment compression", FormatOptionKind.Enum, "deflate",
      AllowedValues: ["deflate", "stored"], Description: "AFF4-L ZipSegment permits ZIP Stored or Deflate."),
    new("Level", "Deflate level", FormatOptionKind.Integer, "6",
      AllowedValues: ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"],
      Description: "0 = stored Deflate blocks, 1 = fast, 2-7 = default, 8-9 = best; ignored for Stored."),
    new("Hashes", "Stream hashes", FormatOptionKind.String, "sha256",
      Description: "Comma-separated linear hashes stored per file as aff4:hash: md5, sha1, sha256, sha512.",
      IsOptimizationAxis: false),
  ];
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".aff4";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [".aff4"];
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
    "Advanced Forensic Format 4 (AFF4): a ZIP/ZIP64 container with information.turtle RDF " +
    "metadata, version.txt and aff4:// image streams. Lists logical images as their files; " +
    "surfaces other members plus metadata distilled from the Turtle graph. Creates AFF4-L ZipSegment volumes.";

  private sealed record MemberInfo(string Name, long Size, long CompressedSize, string Method, DateTime? LastModified, string? Kind, bool IsDirectory = false);
  private sealed record WrittenFile(string Name, string Urn, long Size, DateTimeOffset Modified, IReadOnlyList<(string Datatype, string Hex)> Hashes);

  private static readonly string[] ReservedNames = ["version.txt", "container.description", "information.turtle", "information.turtle.hashes"];

  /// <summary>Creates a standards-based AFF4-L volume using ZIP-backed data streams.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite) throw new ArgumentException("Output stream must be writable.", nameof(output));
    if (options.Password is not null || options.EncryptFilenames)
      throw new NotSupportedException("AFF4 ZIP volumes do not define encryption for this writer.");

    var method = options.GetOption("Method", options.MethodName ?? "deflate").ToLowerInvariant();
    if (method is not ("deflate" or "stored"))
      throw new ArgumentException($"Unsupported AFF4 ZipSegment method '{method}'. Use deflate or stored.", nameof(options));
    if (options.HasOption("Level") && !options.TryGetInt("Level", out _))
      throw new ArgumentException("AFF4 Deflate level must be an integer from 0 through 9.", nameof(options));
    var level = options.TryGetInt("Level", out var configuredLevel) ? configuredLevel : options.Level ?? 6;
    if (level is < 0 or > 9)
      throw new ArgumentOutOfRangeException(nameof(options), "AFF4 Deflate level must be from 0 through 9.");
    var hashNames = ParseHashNames(options.GetOption("Hashes", "sha256"), nameof(options));
    var compression = GetCompressionLevel(level);

    // Validate every name before writing anything.
    var files = new List<(ArchiveInputInfo Input, string Name)>();
    var folders = new HashSet<string>(StringComparer.Ordinal);
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var input in inputs) {
      var name = NormalizeArchiveName(input.ArchiveName);
      if (input.IsDirectory) name = name.TrimEnd('/');
      else if (name.EndsWith('/')) throw new ArgumentException($"File path '{name}' ends with a directory separator.", nameof(inputs));
      if (string.IsNullOrEmpty(name)) continue;
      if (name.IndexOf('/') < 0 && ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        throw new ArgumentException($"'{name}' is reserved by the AFF4 container.", nameof(inputs));
      if (!names.Add(name)) throw new ArgumentException($"Duplicate AFF4 path '{name}'.", nameof(inputs));
      var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
      for (var i = 1; i < segments.Length; ++i)
        folders.Add(string.Join('/', segments[..i]));
      if (input.IsDirectory) folders.Add(name);
      else files.Add((input, name));
    }

    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }
    var volumeArn = $"aff4://{Guid.NewGuid():D}";
    var stamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var zip = new Aff4ZipWriter(output);
    // AFF4 Standard v1.0 section 5.4: the volume ARN goes in container.description, which must
    // be the first member, and is recommended in the ZIP comment as well.
    AddText(zip, "container.description", volumeArn, stamp);
    AddText(zip, "version.txt", "major=2\nminor=1\ntool=CompressionWorkbench\n", stamp);
    var written = new List<WrittenFile>(files.Count);
    foreach (var (input, name) in files) {
      var modified = input.InMemoryContent == null && File.Exists(input.FullPath)
        ? new DateTimeOffset(File.GetLastWriteTimeUtc(input.FullPath))
        : DateTimeOffset.UtcNow;
      var urn = $"aff4://{Guid.NewGuid():D}";
      var hashers = hashNames.Select(h => (Name: h, Hash: IncrementalHash.CreateHash(HashAlgorithmOf(h)))).ToList();
      using var source = input.InMemoryContent is { } content ? new MemoryStream(content, writable: false) : (Stream)File.OpenRead(input.FullPath);
      long size = 0;
      zip.AddEntry(urn, source, method == "deflate", compression, modified, chunk => {
        size += chunk.Length;
        foreach (var (_, hash) in hashers) hash.AppendData(chunk);
      });
      written.Add(new WrittenFile(name, urn, size, modified,
        [.. hashers.Select(h => (DatatypeOf(h.Name), Convert.ToHexString(h.Hash.GetHashAndReset()).ToLowerInvariant()))]));
      foreach (var (_, hash) in hashers) hash.Dispose();
    }

    var turtle = BuildTurtle(volumeArn, written, folders);
    var turtleHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(turtle))).ToLowerInvariant();
    AddText(zip, "information.turtle", turtle, stamp);
    // AFF4-L section 10.1's example writes the subject as ":/information.turtle", which is not
    // valid Turtle (a prefixed local name cannot start with "/"); the full IRI names the same thing.
    AddText(zip, "information.turtle.hashes",
      $"@prefix : <{volumeArn}> .\n@prefix aff4: <http://aff4.org/Schema#> .\n\n<{volumeArn}/information.turtle> aff4:hash \"{turtleHash}\"^^aff4:SHA256 .\n",
      stamp);
    zip.Finish(volumeArn);
  }

  private static DeflateCompressionLevel GetCompressionLevel(int level) => level switch {
    0 => DeflateCompressionLevel.None,
    1 => DeflateCompressionLevel.Fast,
    >= 8 => DeflateCompressionLevel.Best,
    _ => DeflateCompressionLevel.Default,
  };

  private static List<string> ParseHashNames(string value, string paramName) {
    var result = new List<string>();
    foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var name = part.ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal);
      if (name is not ("md5" or "sha1" or "sha256" or "sha512"))
        throw new ArgumentException($"Unsupported AFF4 hash '{part}'. Use md5, sha1, sha256 or sha512.", paramName);
      if (!result.Contains(name)) result.Add(name);
    }
    // AFF4-L section 6.1: producers MUST store a linear hash of every ZipSegment stream.
    if (result.Count == 0) throw new ArgumentException("AFF4-L requires at least one stream hash.", paramName);
    return result;
  }

  private static HashAlgorithmName HashAlgorithmOf(string name) => name switch {
    "md5" => HashAlgorithmName.MD5,
    "sha1" => HashAlgorithmName.SHA1,
    "sha512" => HashAlgorithmName.SHA512,
    _ => HashAlgorithmName.SHA256,
  };

  // AFF4 Standard v1.0 hash datatypes (http://aff4.org/Schema#MD5 etc.).
  private static string DatatypeOf(string name) => name switch {
    "md5" => "MD5",
    "sha1" => "SHA1",
    "sha512" => "SHA512",
    _ => "SHA256",
  };

  private static string NormalizeArchiveName(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var normalized = name.Replace('\\', '/').TrimStart('/');
    if (normalized.Split('/').Any(part => part is ".." or "."))
      throw new ArgumentException($"Archive path '{name}' contains a traversal segment.", nameof(name));
    return normalized;
  }

  private static string BuildTurtle(string volumeArn, IReadOnlyList<WrittenFile> files, IEnumerable<string> folders) {
    var sb = new StringBuilder();
    sb.Append("@prefix : <").Append(volumeArn).Append("> .\n")
      .Append("@prefix aff4: <http://aff4.org/Schema#> .\n")
      .Append("@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .\n\n")
      .Append(": a aff4:ZipVolume .\n\n");
    foreach (var file in files) {
      var leaf = file.Name[(file.Name.LastIndexOf('/') + 1)..];
      sb.Append('<').Append(file.Urn).Append("> a aff4:FileImage, aff4:Image, aff4:ZipSegment ;\n");
      AppendNames(sb, leaf, "/" + file.Name);
      sb
        .Append("  aff4:size \"").Append(file.Size.ToString(CultureInfo.InvariantCulture)).Append("\"^^xsd:long ;\n")
        .Append("  aff4:lastWritten ").Append(TurtleString(file.Modified.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))).Append("^^xsd:dateTime ;\n")
        .Append("  aff4:hash ").AppendJoin(", ", file.Hashes.Select(h => TurtleString(h.Hex) + "^^aff4:" + h.Datatype)).Append(" .\n\n");
    }
    // AFF4-L section 1.1 deprecates path-based ARNs: folders get GUID ARNs like files.
    foreach (var folder in folders.OrderBy(x => x, StringComparer.Ordinal)) {
      var leaf = folder[(folder.LastIndexOf('/') + 1)..];
      sb.Append("<aff4://").Append(Guid.NewGuid().ToString("D")).Append("> a aff4:Folder, aff4:Image ;\n");
      AppendNames(sb, leaf, "/" + folder);
      sb.Length -= 3; // turn the trailing " ;\n" into the statement end
      sb.Append(" .\n\n");
    }
    return sb.ToString();
  }

  /// <summary>
  /// AFF4-L sections 1.1 and 5: aff4:fileName and aff4:originalPathName, %XX-escaped with the raw
  /// bytes base64-encoded in the ...Raw property when the name has a control character (or is not
  /// valid UTF-16, hence not valid UTF-8). The path is repeated as aff4:originalFileName, the
  /// property the AFF4-L draft's own examples and the AFF4 v1.1 logical readers (pyaff4) use, so
  /// that legacy AFF4-L readers find the file too.
  /// </summary>
  private static void AppendNames(StringBuilder sb, string leaf, string path) {
    foreach (var (property, value) in new[] { ("fileName", leaf), ("originalPathName", path) }) {
      if (NeedsNameEscaping(value)) {
        var raw = Encoding.UTF8.GetBytes(value);
        sb.Append("  aff4:").Append(property).Append(' ').Append(TurtleString(EscapeName(raw))).Append(" ;\n");
        sb.Append("  aff4:").Append(property).Append("Raw \"").Append(Convert.ToBase64String(raw)).Append("\"^^xsd:base64Binary ;\n");
      } else
        sb.Append("  aff4:").Append(property).Append(' ').Append(TurtleString(value)).Append(" ;\n");
    }
    var legacy = NeedsNameEscaping(path) ? EscapeName(Encoding.UTF8.GetBytes(path)) : path;
    sb.Append("  aff4:originalFileName ").Append(TurtleString(legacy)).Append(" ;\n");
  }

  private static bool NeedsNameEscaping(string value) {
    for (var i = 0; i < value.Length; ++i) {
      var c = value[i];
      if (c < 0x20) return true;
      if (char.IsHighSurrogate(c) && (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))) return true;
      if (char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(value[i - 1]))) return true;
      if (char.IsHighSurrogate(c)) ++i;
    }
    return false;
  }

  // Bytes 0x00-0x1F, 0x25 ('%') and 0x80-0xFF become %XX (uppercase hex).
  private static string EscapeName(byte[] raw) {
    var sb = new StringBuilder(raw.Length);
    foreach (var b in raw)
      if (b < 0x20 || b == 0x25 || b >= 0x80) sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
      else sb.Append((char)b);
    return sb.ToString();
  }

  private static string TurtleString(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

  private static void AddText(Aff4ZipWriter zip, string name, string content, DateTimeOffset modified) {
    using var source = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
    zip.AddEntry(name, source, deflate: false, DeflateCompressionLevel.None, modified);
  }

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.aff4", fullSize, fullSize, "Stored", false, false, null, Kind: "Track"),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null, Kind: "Tag"),
    };
    var idx = 2;
    foreach (var m in EnumerateMembers(stream))
      entries.Add(new ArchiveEntryInfo(idx++, m.Name, m.Size, m.CompressedSize, m.Method, m.IsDirectory, false, m.LastModified, Kind: m.Kind));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (Wants(files, "FULL.aff4")) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.aff4");
      Directory.CreateDirectory(outputDir);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }

    string? turtle = null;
    Aff4Volume? volume = null;
    ZipReader? zip = null;
    try {
      volume = Aff4Volume.Open(stream, out zip);
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException) {
      // Malformed ZIP — fall through to partial metadata.
      zip?.Dispose();
    }
    if (volume != null) {
      // A damaged stream inside a readable volume fails the extraction rather than truncating it.
      using (zip) {
        turtle = volume.Turtle;
        foreach (var item in volume.Items) {
          if (!Wants(files, item.Path)) continue;
          var dest = SafeCombine(outputDir, item.Path);
          if (item.IsDirectory) { Directory.CreateDirectory(dest); continue; }
          var destDir = Path.GetDirectoryName(dest);
          if (destDir != null) Directory.CreateDirectory(destDir);
          using (var outFile = File.Create(dest))
            volume.CopyTo(item, outFile);
          if (item.LastWritten is { } modified)
            try { File.SetLastWriteTimeUtc(dest, modified.UtcDateTime); } catch { /* timestamp precision/platform limits */ }
        }
        foreach (var entry in volume.Entries) {
          var storedName = entry.FileName.Replace('\\', '/');
          if (volume.ConsumedMembers.Contains(entry.FileName)) continue;
          if (storedName.EndsWith('/')) {
            var directoryName = storedName.TrimEnd('/');
            if (Wants(files, directoryName) || Wants(files, storedName))
              Directory.CreateDirectory(SafeCombine(outputDir, directoryName));
            continue;
          }
          if (!Wants(files, storedName)) continue;
          var dest = SafeCombine(outputDir, storedName);
          var destDir = Path.GetDirectoryName(dest);
          if (destDir != null) Directory.CreateDirectory(destDir);
          using (var es = zip!.OpenEntryStream(entry))
          using (var outFile = File.Create(dest))
            es.CopyTo(outFile);
          try { File.SetLastWriteTime(dest, entry.LastModified); } catch { /* timestamp precision/platform limits */ }
        }
      }
    }

    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(stream, turtle)));
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static List<MemberInfo> EnumerateMembers(Stream stream) {
    var result = new List<MemberInfo>();
    try {
      var volume = Aff4Volume.Open(stream, out var zip);
      using (zip) {
        foreach (var item in volume.Items)
          result.Add(item.IsDirectory
            ? new MemberInfo(item.Path, 0, 0, "Stored", item.LastWritten?.LocalDateTime, "folder", IsDirectory: true)
            : new MemberInfo(item.Path, item.Size, item.Size, item.Storage.ToString(), item.LastWritten?.LocalDateTime, "file"));
        foreach (var entry in volume.Entries) {
          if (volume.ConsumedMembers.Contains(entry.FileName)) continue;
          var storedName = entry.FileName.Replace('\\', '/');
          if (storedName.EndsWith('/')) {
            result.Add(new MemberInfo(storedName.TrimEnd('/'), 0, 0, "Stored", entry.LastModified, "folder", IsDirectory: true));
            continue;
          }
          var method = entry.CompressedSize == entry.UncompressedSize ? "Stored" : "Deflate";
          result.Add(new MemberInfo(storedName, entry.UncompressedSize, entry.CompressedSize, method, entry.LastModified, ClassifyMember(storedName)));
        }
      }
    } catch {
      // Malformed — surface only FULL + metadata.
    }
    return result;
  }

  private static string ClassifyMember(string name) {
    var leaf = Path.GetFileName(name);
    if (string.Equals(leaf, "version.txt", StringComparison.OrdinalIgnoreCase)) return "version";
    if (string.Equals(leaf, "container.description", StringComparison.OrdinalIgnoreCase)) return "description";
    if (IsTurtle(name)) return "metadata";
    if (name.StartsWith("aff4", StringComparison.OrdinalIgnoreCase) || name.Contains("aff4%3A", StringComparison.OrdinalIgnoreCase)) return "stream";
    return "member";
  }

  private static bool IsTurtle(string name)
    => name.EndsWith("information.turtle", StringComparison.OrdinalIgnoreCase) ||
       name.EndsWith(".turtle", StringComparison.OrdinalIgnoreCase);

  private static string? ReadTurtle(Stream stream) {
    try {
      var volume = Aff4Volume.Open(stream, out var zip);
      using (zip) return volume.Turtle;
    } catch { return null; }
  }

  private static string BuildMetadataIni(Stream stream, string? turtle) {
    turtle ??= ReadTurtle(stream);
    var sb = new StringBuilder();
    sb.Append("[Aff4]\n");
    var members = EnumerateMembers(stream);
    var isZip = LooksLikeZip(stream);
    sb.Append(CultureInfo.InvariantCulture, $"valid={(isZip ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"member_count={members.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"has_version_txt={(members.Any(m => m.Kind == "version") ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"has_turtle={(turtle != null || members.Any(m => m.Kind == "metadata") ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"stream_member_count={members.Count(m => m.Kind == "stream")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"logical_file_count={members.Count(m => m.Kind == "file")}\n");

    if (turtle != null) {
      var size = FindTurtleValue(turtle, "aff4:size") ?? FindTurtleValue(turtle, "size");
      var chunk = FindTurtleValue(turtle, "aff4:chunkSize") ?? FindTurtleValue(turtle, "chunkSize");
      var comp = FindTurtleValue(turtle, "aff4:compressionMethod") ?? FindTurtleValue(turtle, "compressionMethod");
      if (size != null) sb.Append(CultureInfo.InvariantCulture, $"image_size={size}\n");
      if (chunk != null) sb.Append(CultureInfo.InvariantCulture, $"chunk_size={chunk}\n");
      if (comp != null) sb.Append(CultureInfo.InvariantCulture, $"compression={comp}\n");
    }

    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(isZip ? "ok" : "partial")}\n");
    return sb.ToString();
  }

  // Scrapes the first literal/IRI object following a given predicate token in the
  // Turtle graph. Best-effort — no full RDF parse. Returns the raw token text.
  private static string? FindTurtleValue(string turtle, string predicate) {
    var idx = turtle.IndexOf(predicate, StringComparison.OrdinalIgnoreCase);
    if (idx < 0) return null;
    var p = idx + predicate.Length;
    while (p < turtle.Length && (turtle[p] == ' ' || turtle[p] == '\t')) ++p;
    if (p >= turtle.Length) return null;
    if (turtle[p] == '"') {
      var end = turtle.IndexOf('"', p + 1);
      if (end < 0) return null;
      return turtle.Substring(p + 1, end - (p + 1));
    }
    var start = p;
    while (p < turtle.Length && turtle[p] is not (' ' or '\t' or '\r' or '\n' or ';' or ',' or '.')) ++p;
    return p > start ? turtle[start..p] : null;
  }

  private static bool LooksLikeZip(Stream stream) {
    try {
      if (!stream.CanSeek || stream.Length < 4) return false;
      stream.Position = 0;
      Span<byte> sig = stackalloc byte[4];
      var read = 0;
      while (read < 4) {
        var n = stream.Read(sig[read..]);
        if (n <= 0) break;
        read += n;
      }
      return read == 4 && sig[0] == 'P' && sig[1] == 'K' &&
             (sig[2] == 0x03 || sig[2] == 0x05 || sig[2] == 0x07);
    } catch {
      return false;
    }
  }

  private static string SafeCombine(string baseDir, string entryName) {
    var safeName = entryName.Replace('\\', '/').TrimStart('/');
    if (Path.IsPathRooted(safeName) || safeName.Split('/').Any(part => part is ".." or ".")) {
      safeName = Path.GetFileName(safeName);
      if (safeName is "." or ".." || safeName.Length == 0) safeName = "_";
    }
    // ZIP member names may contain URL-encoded colons (aff4%3A...) which are
    // illegal on some filesystems; leave them as-is — the platform handles them.
    var root = Path.GetFullPath(baseDir);
    var destination = Path.GetFullPath(Path.Combine(root, safeName));
    var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
    if (!destination.StartsWith(rootPrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
      destination = Path.Combine(root, "_");
    return destination;
  }

  private static long SafeLength(Stream s) => s.CanSeek ? s.Length : 0;
}
