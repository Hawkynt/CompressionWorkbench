#pragma warning disable CS1591
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Afio;

/// <summary>
/// afio archive — a derivative of the portable-ASCII (<c>odc</c>) cpio format.
/// Each member begins with a 76-byte ASCII header: 6-char magic <c>"070707"</c>,
/// then octal fields dev(6), ino(6), mode(6), uid(6), gid(6), nlink(6), rdev(6),
/// mtime(11), namesize(6) and filesize(11). The NUL-terminated name follows, then
/// the file data. The archive ends with a member named <c>TRAILER!!!</c>.
///
/// <para>afio extends cpio with optional <b>per-file compression</b>: a member's
/// stored payload may be a gzip stream (RFC 1952), in which case the original
/// size is recorded after the name. This reader detects a gzip member by its
/// <c>1F 8B</c> signature and transparently inflates it on extraction, surfacing
/// each member as an entry. Read-only (List / Extract); malformed input never
/// throws — it stops at the last parseable member.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/kholtman/afio</c> — canonical afio source (Koen Holtman); afio(1) documents the archive format</description></item>
///   <item><description><c>https://pubs.opengroup.org/onlinepubs/9699919799/utilities/pax.html</c> — POSIX pax — defines the portable-ASCII (odc, "070707") cpio header afio derives from</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Cpio</c> — background on the cpio family</description></item>
/// </list>
/// </summary>
public sealed class AfioFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Afio";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "afio";
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
public string DefaultExtension => ".afio";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".afio"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("070707"u8.ToArray(), Confidence: 0.55), // shared with portable-ASCII cpio; extension disambiguates
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("gzip", "Gzip (per-file)"),
  ];
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
    "afio cpio-derivative: portable-ASCII (070707) headers with optional per-file gzip compression.";

  private const int HeaderSize = 76;
  private const string Magic = "070707";
  private const string Trailer = "TRAILER!!!";

  private sealed record AfioMember(string Name, uint Mode, byte[] StoredData, bool IsDirectory) {
    public bool IsGzip => this.StoredData.Length >= 2 && this.StoredData[0] == 0x1F && this.StoredData[1] == 0x8B;
  }

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var members = TryReadMembers(stream, out _);
    var entries = new List<ArchiveEntryInfo>(members.Count);
    var idx = 0;
    foreach (var m in members)
      entries.Add(new ArchiveEntryInfo(
        idx++, m.Name, MaterializeSize(m), m.StoredData.Length,
        m.IsGzip ? "Gzip" : "Stored", m.IsDirectory, false, null));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var members = TryReadMembers(stream, out _);
    foreach (var m in members) {
      if (files != null && files.Length > 0 && !MatchesFilter(m.Name, files)) continue;
      if (m.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, m.Name.Replace('\\', '/').TrimStart('/')));
        continue;
      }
      WriteFile(outputDir, m.Name, Materialize(m));
    }
  }

  private static byte[] Materialize(AfioMember m) {
    if (!m.IsGzip) return m.StoredData;
    try {
      using var src = new MemoryStream(m.StoredData);
      using var gz = new GZipStream(src, CompressionMode.Decompress);
      using var ms = new MemoryStream();
      gz.CopyTo(ms);
      return ms.ToArray();
    } catch {
      return m.StoredData; // fall back to raw bytes on corrupt gzip
    }
  }

  private static long MaterializeSize(AfioMember m) {
    if (!m.IsGzip) return m.StoredData.Length;
    try {
      return Materialize(m).Length;
    } catch {
      return m.StoredData.Length;
    }
  }

  private static List<AfioMember> TryReadMembers(Stream stream, out bool partial) {
    partial = false;
    var members = new List<AfioMember>();
    try {
      if (stream.CanSeek) stream.Position = 0;
      while (true) {
        var header = new byte[HeaderSize];
        if (!TryReadExact(stream, header)) break;
        var text = Encoding.ASCII.GetString(header);
        if (text[..6] != Magic) { partial = true; break; }

        var mode = ParseOctal(text, 18, 6);
        var nameSize = (int)ParseOctal(text, 59, 6);
        var fileSize = ParseOctal(text, 65, 11);
        if (nameSize is <= 0 or > 4096) { partial = true; break; }

        var nameBuf = new byte[nameSize];
        if (!TryReadExact(stream, nameBuf)) { partial = true; break; }
        // Name includes the trailing NUL.
        var name = Encoding.ASCII.GetString(nameBuf, 0, nameSize > 0 ? nameSize - 1 : 0);

        if (name == Trailer) break;

        var data = new byte[fileSize];
        if (fileSize > 0 && !TryReadExact(stream, data)) { partial = true; break; }

        // mode & S_IFDIR (0040000 octal) marks a directory.
        var isDir = (mode & 0xF000) == 0x4000;
        members.Add(new AfioMember(name, (uint)mode, data, isDir));
      }
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or FormatException) {
      partial = true;
    }
    return members;
  }

  private static long ParseOctal(string text, int offset, int length) {
    var slice = text.Substring(offset, length).Trim();
    if (slice.Length == 0) return 0;
    return Convert.ToInt64(slice, 8);
  }

  private static bool TryReadExact(Stream stream, Span<byte> buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var n = stream.Read(buffer[read..]);
      if (n <= 0) return read == buffer.Length;
      read += n;
    }
    return true;
  }
}
