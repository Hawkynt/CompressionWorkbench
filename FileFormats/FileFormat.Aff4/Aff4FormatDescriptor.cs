#pragma warning disable CS1591
using System.Globalization;
using System.IO.Compression;
using System.Text;
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
/// <para>This descriptor delegates ZIP enumeration / extraction to the platform ZIP
/// reader and surfaces every ZIP member as a first-class entry, alongside a verbatim
/// <c>FULL.aff4</c> and a <c>metadata.ini</c> that distills the Turtle graph
/// (stored image size, chunk size, compression method) when present. Read-only; the
/// Turtle RDF is exposed raw — no full graph reasoning is performed. Detection is
/// extension-driven (<c>.aff4</c>) so it does not steal generic ZIPs; malformed
/// input degrades to FULL + partial metadata without throwing.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/aff4/Standard</c> — AFF4 standard specification documents</description></item>
///   <item><description><c>https://github.com/aff4/pyaff4</c> — pyaff4 — canonical reference implementation</description></item>
///   <item><description>Cohen, Garfinkel &amp; Schatz, "Extending the Advanced Forensic Format to accommodate multiple data sources, logical evidence, arbitrary information and forensic workflow" (DFRWS 2009) — the defining AFF4 paper</description></item>
/// </list>
/// </summary>
public sealed class Aff4FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
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
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
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
    "each member plus metadata distilled from the Turtle graph. Read-only.";

  private sealed record MemberInfo(string Name, long Size, string Method, DateTime? LastModified, string? Kind);

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
      entries.Add(new ArchiveEntryInfo(idx++, m.Name, m.Size, m.Size, m.Method, false, false, m.LastModified, Kind: m.Kind));
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
    try {
      stream.Seek(0, SeekOrigin.Begin);
      using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
      foreach (var entry in zip.Entries) {
        if (entry.FullName.EndsWith('/')) continue;
        var name = entry.FullName.Replace('\\', '/');
        if (Wants(files, name)) {
          var dest = SafeCombine(outputDir, name);
          var destDir = Path.GetDirectoryName(dest);
          if (destDir != null) Directory.CreateDirectory(destDir);
          using var es = entry.Open();
          using var outFile = File.Create(dest);
          es.CopyTo(outFile);
        }
        if (turtle == null && IsTurtle(name)) {
          using var es = entry.Open();
          using var ms = new MemoryStream();
          es.CopyTo(ms);
          turtle = SafeUtf8(ms.ToArray());
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
      stream.Seek(0, SeekOrigin.Begin);
      using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
      foreach (var entry in zip.Entries) {
        if (entry.FullName.EndsWith('/')) continue;
        var name = entry.FullName.Replace('\\', '/');
        var method = entry.CompressedLength == entry.Length ? "Stored" : "Deflate";
        result.Add(new MemberInfo(name, entry.Length, method, entry.LastWriteTime.DateTime, ClassifyMember(name)));
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
    if (safeName.Contains("..")) safeName = Path.GetFileName(safeName);
    // ZIP member names may contain URL-encoded colons (aff4%3A...) which are
    // illegal on some filesystems; leave them as-is — the platform handles them.
    return Path.Combine(baseDir, safeName);
  }

  private static string SafeUtf8(byte[] data) {
    try { return Encoding.UTF8.GetString(data); }
    catch { return string.Empty; }
  }

  private static long SafeLength(Stream s) => s.CanSeek ? s.Length : 0;
}
