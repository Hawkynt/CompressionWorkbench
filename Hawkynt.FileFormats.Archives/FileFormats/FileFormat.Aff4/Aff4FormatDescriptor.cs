#pragma warning disable CS1591
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Aff4;

/// <summary>
/// AFF4 (Advanced Forensic Format 4) container. An AFF4 volume is a ZIP (typically
/// ZIP64) holding an RDF metadata graph in <c>information.turtle</c>, a
/// <c>version.txt</c> marker, an optional <c>container.description</c>, and image
/// data streams under <c>aff4://&lt;uuid&gt;/</c> paths split into bevy/chunk
/// segments named with zero-padded indices (e.g. <c>00000000</c>, <c>00000000.index</c>).
///
/// <para>This descriptor reads ZIP members and AFF4-L logical file names, and creates
/// ZIP-backed AFF4-L ZipSegment streams using Stored or Deflate. Extraction also
/// offers a verbatim <c>FULL.aff4</c> and <c>metadata.ini</c> distilled from the Turtle
/// graph. Detection is extension-driven (<c>.aff4</c>) so it does not steal generic ZIPs;
/// malformed input degrades to FULL + partial metadata without throwing.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/aff4/Standard</c> — AFF4 standard specification documents</description></item>
///   <item><description><c>inprogress/AFF4-L-StandardSpecification-v1.0.md</c> — AFF4-L v1.0 (ZipSegment streams, metadata and hashes)</description></item>
///   <item><description><c>https://github.com/aff4/pyaff4</c> — pyaff4 — canonical reference implementation</description></item>
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
      Description: "Maps to the platform ZIP Deflate effort tiers; ignored for Stored."),
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
    "metadata, version.txt and aff4:// image streams. Delegates to the ZIP reader; surfaces " +
    "each member plus metadata distilled from the Turtle graph. Creates AFF4-L ZipSegment volumes.";

  private sealed record MemberInfo(string Name, long Size, long CompressedSize, string Method, DateTime? LastModified, string? Kind, bool IsDirectory = false);
  private sealed record InputMember(string Name, string Urn, byte[] Data, DateTimeOffset Modified, string Sha256);

  /// <summary>Creates a standards-based AFF4-L volume using ZIP-backed data streams.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite) throw new ArgumentException("Output stream must be writable.", nameof(output));
    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }
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
    var compression = method == "stored" ? CompressionLevel.NoCompression : GetCompressionLevel(level);
    var volume = Guid.NewGuid();
    var members = new List<InputMember>();
    var folders = new HashSet<string>(StringComparer.Ordinal);
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var input in inputs) {
      var name = NormalizeArchiveName(input.ArchiveName);
      if (input.IsDirectory) name = name.TrimEnd('/');
      else if (name.EndsWith('/')) throw new ArgumentException($"File path '{name}' ends with a directory separator.", nameof(inputs));
      if (string.IsNullOrEmpty(name)) continue;
      if (name.IndexOf('/') < 0 && new[] { "version.txt", "container.description", "information.turtle", "information.turtle.hashes" }
            .Contains(name, StringComparer.OrdinalIgnoreCase))
        throw new ArgumentException($"'{name}' is reserved by the AFF4 container.", nameof(inputs));
      if (!names.Add(name)) throw new ArgumentException($"Duplicate AFF4 path '{name}'.", nameof(inputs));
      var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
      for (var i = 1; i < segments.Length; ++i)
        folders.Add(string.Join('/', segments[..i]));
      if (input.IsDirectory) { folders.Add(name.TrimEnd('/')); continue; }

      var data = input.ReadContent();
      var modified = File.Exists(input.FullPath)
        ? new DateTimeOffset(File.GetLastWriteTimeUtc(input.FullPath))
        : DateTimeOffset.UtcNow;
      if (modified.Year < 1980) modified = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
      if (modified.Year > 2107) modified = new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);
      members.Add(new InputMember(name, $"aff4://{Guid.NewGuid():D}", data, modified, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()));
    }

    var turtle = BuildTurtle(volume, members, folders);
    var turtleHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(turtle))).ToLowerInvariant();
    var volumeArn = $"aff4://{volume:D}";
    using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
    // AFF4 Standard v1.0 section 5.4: the volume ARN goes in container.description, which must
    // be the first member, and is recommended in the ZIP comment as well.
    zip.Comment = volumeArn;
    AddZipText(zip, "container.description", volumeArn, CompressionLevel.NoCompression);
    AddZipText(zip, "version.txt", "major=2\nminor=1\ntool=CompressionWorkbench\n", CompressionLevel.NoCompression);
    foreach (var member in members) {
      var entry = zip.CreateEntry(member.Urn, compression);
      entry.LastWriteTime = member.Modified;
      using var target = entry.Open();
      target.Write(member.Data);
    }
    AddZipText(zip, "information.turtle", turtle, CompressionLevel.NoCompression);
    // AFF4-L section 10.1's example writes the subject as ":/information.turtle", which is not
    // valid Turtle (a prefixed local name cannot start with "/"); the full IRI names the same thing.
    AddZipText(zip, "information.turtle.hashes",
      $"@prefix : <{volumeArn}> .\n@prefix aff4: <http://aff4.org/Schema#> .\n\n<{volumeArn}/information.turtle> aff4:hash \"{turtleHash}\"^^aff4:SHA256 .\n",
      CompressionLevel.NoCompression);
  }

  private static CompressionLevel GetCompressionLevel(int level) => level switch {
    <= 1 => CompressionLevel.Fastest,
    >= 8 => CompressionLevel.SmallestSize,
    _ => CompressionLevel.Optimal,
  };

  private static string NormalizeArchiveName(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var normalized = name.Replace('\\', '/').TrimStart('/');
    if (normalized.Split('/').Any(part => part is ".." or "."))
      throw new ArgumentException($"Archive path '{name}' contains a traversal segment.", nameof(name));
    return normalized;
  }

  private static string BuildTurtle(Guid volume, IReadOnlyList<InputMember> members, IEnumerable<string> folders) {
    var sb = new StringBuilder();
    sb.Append("@prefix : <aff4://").Append(volume.ToString("D")).Append("> .\n")
      .Append("@prefix aff4: <http://aff4.org/Schema#> .\n")
      .Append("@prefix aff4l: <http://aff4.org/Schema/2022/#> .\n")
      .Append("@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .\n\n")
      .Append(": a aff4:ZipVolume .\n\n");
    foreach (var member in members) {
      var leaf = member.Name[(member.Name.LastIndexOf('/') + 1)..];
      sb.Append('<').Append(member.Urn).Append("> a aff4:FileImage, aff4:Image, aff4:ZipSegment ;\n");
      AppendNames(sb, leaf, "/" + member.Name);
      sb
        .Append("  aff4:size \"").Append(member.Data.LongLength.ToString(CultureInfo.InvariantCulture)).Append("\"^^xsd:long ;\n")
        .Append("  aff4:lastWritten ").Append(TurtleString(member.Modified.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))).Append("^^xsd:dateTime ;\n")
        .Append("  aff4:hash ").Append(TurtleString(member.Sha256)).Append("^^aff4:SHA256 .\n\n");
    }
    // AFF4-L section 1.1 deprecates path-based ARNs: folders get GUID ARNs like files.
    foreach (var folder in folders.OrderBy(x => x, StringComparer.Ordinal)) {
      var leaf = folder[(folder.LastIndexOf('/') + 1)..];
      sb.Append("<aff4://").Append(Guid.NewGuid().ToString("D")).Append("> a aff4:Folder ;\n");
      AppendNames(sb, leaf, "/" + folder);
      sb.Length -= 3; // turn the trailing " ;\n" into the statement end
      sb.Append(" .\n\n");
    }
    return sb.ToString();
  }

  /// <summary>
  /// AFF4-L section 5: a name with a control character (or one that is not valid UTF-16, hence not
  /// valid UTF-8) is stored %XX-escaped in aff4:fileName / aff4:originalPathName, with the raw
  /// bytes base64-encoded in the ...Raw property.
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

  private static void AddZipText(ZipArchive zip, string name, string content, CompressionLevel level) {
    var entry = zip.CreateEntry(name, level);
    entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    using var target = entry.Open();
    target.Write(Encoding.UTF8.GetBytes(content));
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

    var turtle = ReadTurtle(stream);
    var logicalNames = ParseLogicalNames(turtle);
    var logicalModified = ParseLogicalModified(turtle);
    foreach (var directory in ParseLogicalFolders(turtle))
      if (Wants(files, directory)) Directory.CreateDirectory(SafeCombine(outputDir, directory));
    try {
      stream.Seek(0, SeekOrigin.Begin);
      using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
      foreach (var entry in zip.Entries) {
        var storedName = entry.FullName.Replace('\\', '/');
        if (storedName.EndsWith('/')) {
          var directoryName = storedName.TrimEnd('/');
          if (Wants(files, directoryName) || Wants(files, storedName))
            Directory.CreateDirectory(SafeCombine(outputDir, directoryName));
          continue;
        }
        var name = logicalNames.GetValueOrDefault(storedName, storedName);
        if (Wants(files, name) || Wants(files, storedName)) {
          var dest = SafeCombine(outputDir, name);
          var destDir = Path.GetDirectoryName(dest);
          if (destDir != null) Directory.CreateDirectory(destDir);
          using var es = entry.Open();
          using var outFile = File.Create(dest);
          es.CopyTo(outFile);
          try { File.SetLastWriteTime(dest, logicalModified.GetValueOrDefault(storedName, entry.LastWriteTime).LocalDateTime); } catch { /* timestamp precision/platform limits */ }
        }
      }
    } catch {
      // Malformed ZIP — fall through to partial metadata.
    }

    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(stream, turtle)));
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static IEnumerable<MemberInfo> EnumerateMembers(Stream stream) {
    var result = new List<MemberInfo>();
    try {
      var turtle = ReadTurtle(stream);
      var logicalNames = ParseLogicalNames(turtle);
      var logicalFolders = ParseLogicalFolders(turtle);
      var logicalModified = ParseLogicalModified(turtle);
      stream.Seek(0, SeekOrigin.Begin);
      using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
      foreach (var entry in zip.Entries) {
        var storedName = entry.FullName.Replace('\\', '/');
        if (storedName.EndsWith('/')) {
          result.Add(new MemberInfo(storedName.TrimEnd('/'), 0, 0, "Stored", entry.LastWriteTime.DateTime, "folder", IsDirectory: true));
          continue;
        }
        var name = logicalNames.GetValueOrDefault(storedName, storedName);
        var method = entry.CompressedLength == entry.Length ? "Stored" : "Deflate";
        var modified = logicalModified.TryGetValue(storedName, out var exactModified)
          ? exactModified.LocalDateTime
          : entry.LastWriteTime.DateTime;
        result.Add(new MemberInfo(name, entry.Length, entry.CompressedLength, method, modified, ClassifyMember(name)));
      }
      foreach (var folder in logicalFolders)
        result.Add(new MemberInfo(folder, 0, 0, "Stored", null, "folder", IsDirectory: true));
    } catch {
      // Malformed — surface only FULL + metadata.
    }
    return result;
  }

  private static string? ReadTurtle(Stream stream) {
    try {
      stream.Seek(0, SeekOrigin.Begin);
      using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
      var entry = zip.Entries.FirstOrDefault(e => IsTurtle(e.FullName));
      if (entry == null) return null;
      using var source = entry.Open();
      using var buffer = new MemoryStream();
      source.CopyTo(buffer);
      return SafeUtf8(buffer.ToArray());
    } catch { return null; }
  }

  private static Dictionary<string, string> ParseLogicalNames(string? turtle) {
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    if (turtle == null) return result;
    foreach (Match match in Regex.Matches(turtle,
      "<(?<urn>aff4://[^>]+)>\\s+a\\s+aff4:FileImage\\b(?<body>.*?)(?:\\.\\s*(?:\\r?\\n|$))",
      RegexOptions.Singleline | RegexOptions.CultureInvariant)) {
      var logical = LogicalPath(match.Groups["body"].Value);
      if (logical is { Length: > 0 }) result[match.Groups["urn"].Value] = logical;
    }
    return result;
  }

  private static List<string> ParseLogicalFolders(string? turtle) {
    var result = new List<string>();
    if (turtle == null) return result;
    foreach (Match match in Regex.Matches(turtle,
      "<(?<urn>aff4://[^>]+)>\\s+a\\s+aff4:Folder\\b(?<body>.*?)(?:\\.\\s*(?:\\r?\\n|$))",
      RegexOptions.Singleline | RegexOptions.CultureInvariant)) {
      var logical = LogicalPath(match.Groups["body"].Value);
      if (logical is { Length: > 0 }) result.Add(logical);
    }
    return result;
  }

  /// <summary>
  /// The logical path of a FileImage or Folder: aff4:originalPathNameRaw (base64) when present,
  /// else aff4:originalPathName, else the AFF4 v1.1 / pyaff4 aff4:originalFileName. Windows
  /// separators become '/'.
  /// </summary>
  private static string? LogicalPath(string body) {
    var raw = Regex.Match(body, "aff4:originalPathNameRaw\\s+\"(?<b64>[A-Za-z0-9+/=\\s]*)\"", RegexOptions.CultureInvariant);
    string? path = null;
    if (raw.Success) {
      try { path = Encoding.UTF8.GetString(Convert.FromBase64String(raw.Groups["b64"].Value)); } catch (FormatException) { path = null; }
    }
    if (path == null) {
      var named = Regex.Match(body, "aff4:(?:originalPathName|originalFileName)\\s+\"(?<path>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.CultureInvariant);
      if (!named.Success) return null;
      path = TurtleUnescape(named.Groups["path"].Value);
    }
    path = path.Replace('\\', '/').TrimStart('/');
    return path.Split('/').Any(part => part is ".." or ".") ? Path.GetFileName(path) : path;
  }

  // Turtle string escapes; any other backslash is kept, since producers write Windows paths such
  // as "\GovDocs\000\000785.html" unescaped.
  private static string TurtleUnescape(string value) {
    var sb = new StringBuilder(value.Length);
    for (var i = 0; i < value.Length; ++i) {
      if (value[i] != '\\' || i + 1 >= value.Length) { sb.Append(value[i]); continue; }
      var next = value[i + 1];
      var mapped = next switch { '"' => '"', '\\' => '\\', 'n' => '\n', 'r' => '\r', 't' => '\t', '\'' => '\'', _ => '\0' };
      if (mapped == '\0') { sb.Append('\\'); continue; }
      sb.Append(mapped);
      ++i;
    }
    return sb.ToString();
  }

  private static Dictionary<string, DateTimeOffset> ParseLogicalModified(string? turtle) {
    var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    if (turtle == null) return result;
    foreach (Match match in Regex.Matches(turtle,
      "<(?<urn>aff4://[^>]+)>\\s+a\\s+aff4:FileImage\\b(?<body>.*?)(?:\\.\\s*(?:\\r?\\n|$))",
      RegexOptions.Singleline | RegexOptions.CultureInvariant)) {
      var timestamp = Regex.Match(match.Groups["body"].Value,
        "aff4:lastWritten\\s+\"(?<value>(?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.CultureInvariant);
      if (timestamp.Success && DateTimeOffset.TryParse(timestamp.Groups["value"].Value,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified))
        result[match.Groups["urn"].Value] = modified;
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

  private static string BuildMetadataIni(Stream stream, string? turtle) {
    var sb = new StringBuilder();
    sb.Append("[Aff4]\n");
    var members = EnumerateMembers(stream).ToList();
    var isZip = LooksLikeZip(stream);
    sb.Append(CultureInfo.InvariantCulture, $"valid={(isZip ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"member_count={members.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"has_version_txt={(members.Any(m => m.Kind == "version") ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"has_turtle={(turtle != null || members.Any(m => m.Kind == "metadata") ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"stream_member_count={members.Count(m => m.Kind == "stream")}\n");

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

  private static string SafeUtf8(byte[] data) {
    try { return Encoding.UTF8.GetString(data); }
    catch { return string.Empty; }
  }

  private static long SafeLength(Stream s) => s.CanSeek ? s.Length : 0;
}
