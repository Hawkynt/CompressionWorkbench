using System.Buffers.Binary;
using Compression.Core.Deflate;
using Compression.Core.Streams;
using System.Text;
using System.Xml.Linq;
using Compression.Core.Checksums;

namespace FileFormat.Xar;

/// <summary>
/// Creates XAR (eXtensible ARchive) archives.
/// Entries are zlib-compressed by default.
/// </summary>
/// <remarks>
/// The heap opens with the SHA-1 of the compressed TOC, which the header announces
/// (checksum algorithm 1) and the TOC locates (<c>&lt;checksum style="sha1"&gt;</c> at heap
/// offset 0, size 20): xar and libarchive verify it and refuse an archive without it. Folders
/// are <c>&lt;file&gt;</c> elements of type <c>directory</c> holding their children.
/// </remarks>
public sealed class XarWriter : IDisposable {
  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly List<(string name, byte[] data, DateTime modified, int? mode)> _files = [];
  private readonly List<(string name, DateTime modified, int? mode)> _dirs = [];
  private const int TocChecksumSize = 20;
  private const int DefaultFileMode = 0x1A4;      // 0644
  private const int DefaultDirectoryMode = 0x1ED; // 0755

  /// <summary>Creates a new XAR writer.</summary>
  public XarWriter(Stream output, bool leaveOpen = false) {
    _output = output;
    _leaveOpen = leaveOpen;
  }

  /// <summary>Adds a file to the archive; a path with '/' puts it into folders.</summary>
  public void AddFile(string name, byte[] data, DateTime? modified = null, int? mode = null) {
    _files.Add((name, data, modified ?? DateTime.UtcNow, mode));
  }

  /// <summary>Adds a folder (and its parents); needed only for empty ones.</summary>
  public void AddDirectory(string name, DateTime? modified = null, int? mode = null) {
    var path = string.Join('/', name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries));
    if (path.Length > 0) _dirs.Add((path, modified ?? DateTime.UtcNow, mode));
  }

  /// <summary>Writes the archive and flushes.</summary>
  public void Dispose() {
    WriteArchive();
    if (!_leaveOpen) _output.Dispose();
  }

  private void WriteArchive() {
    // Build heap: compress each file and collect metadata
    var heapEntries = new List<(string name, byte[] compressed, long offset, long origSize, long compSize, string origHash, string compHash, DateTime modified, int? mode)>();
    long heapOffset = TocChecksumSize;

    foreach (var (name, data, modified, mode) in _files) {
      var compressed = CompressZlib(data);
      var origHash = ComputeSha1Hex(data);
      var compHash = ComputeSha1Hex(compressed);
      heapEntries.Add((name, compressed, heapOffset, data.Length, compressed.Length, origHash, compHash, modified, mode));
      heapOffset += compressed.Length;
    }

    // Build TOC XML
    var toc = BuildToc(heapEntries);
    var tocBytes = Encoding.UTF8.GetBytes(toc);
    var tocCompressed = CompressZlib(tocBytes);

    // XAR header: 28 bytes
    const ushort headerSize = 28;
    const ushort version = 1;
    const uint checksumAlgo = 1; // SHA-1

    Span<byte> header = stackalloc byte[28];
    header[0] = 0x78; header[1] = 0x61; header[2] = 0x72; header[3] = 0x21; // "xar!"
    BinaryPrimitives.WriteUInt16BigEndian(header[4..], headerSize);
    BinaryPrimitives.WriteUInt16BigEndian(header[6..], version);
    BinaryPrimitives.WriteUInt64BigEndian(header[8..], (ulong)tocCompressed.Length);
    BinaryPrimitives.WriteUInt64BigEndian(header[16..], (ulong)tocBytes.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header[24..], checksumAlgo);
    _output.Write(header);

    // Write the compressed TOC, then its checksum at the start of the heap
    _output.Write(tocCompressed);
    _output.Write(Sha1.Compute(tocCompressed));

    // Write heap (compressed file data)
    foreach (var entry in heapEntries)
      _output.Write(entry.compressed);

    _output.Flush();
  }

  private string BuildToc(List<(string name, byte[] compressed, long offset, long origSize, long compSize, string origHash, string compHash, DateTime modified, int? mode)> entries) {
    var toc = new XElement("toc",
      new XElement("checksum", new XAttribute("style", "sha1"),
        new XElement("offset", "0"),
        new XElement("size", TocChecksumSize.ToString())),
      new XElement("creation-time", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")));
    var nextId = 1;
    var folders = new Dictionary<string, XElement>(StringComparer.Ordinal) { [""] = toc };
    XElement Folder(string path, DateTime modified, int? mode) {
      if (folders.TryGetValue(path, out var el)) return el;
      var slash = path.LastIndexOf('/');
      var parent = Folder(slash < 0 ? "" : path[..slash], modified, null);
      el = new XElement("file",
        new XAttribute("id", (nextId++).ToString()),
        new XElement("name", path[(slash + 1)..]),
        new XElement("type", "directory"),
        new XElement("mode", FormatMode(mode ?? DefaultDirectoryMode)),
        new XElement("mtime", Utc(modified)));
      parent.Add(el);
      folders[path] = el;
      return el;
    }
    foreach (var (name, modified, mode) in _dirs) Folder(name, modified, mode);
    foreach (var e in entries) {
      var path = string.Join('/', e.name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries));
      var slash = path.LastIndexOf('/');
      var parent = Folder(slash < 0 ? "" : path[..slash], e.modified, null);
      parent.Add(
          new XElement("file",
            new XAttribute("id", (nextId++).ToString()),
            new XElement("name", path[(slash + 1)..]),
            new XElement("type", "file"),
            new XElement("mode", FormatMode(e.mode ?? DefaultFileMode)),
            new XElement("mtime", Utc(e.modified)),
            new XElement("data",
              new XElement("size", e.origSize.ToString()),
              new XElement("length", e.compSize.ToString()),
              new XElement("offset", e.offset.ToString()),
              new XElement("encoding",
                new XAttribute("style", "application/x-gzip")),
              new XElement("extracted-checksum",
                new XAttribute("style", "sha1"),
                e.origHash),
              new XElement("archived-checksum",
                new XAttribute("style", "sha1"),
                e.compHash)
            )
          ));
    }
    return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + new XElement("xar", toc).ToString();
  }

  private static string FormatMode(int mode) => "0" + Convert.ToString(mode & 0xFFF, 8);

  private static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

  private static byte[] CompressZlib(byte[] data) {
    using var ms = new MemoryStream();
    // Zlib header: CMF=0x78 (deflate, window 32KB), FLG=0x9C (default compression, check bits)
    ms.WriteByte(0x78);
    ms.WriteByte(0x9C);
    using (var ds = new RawDeflateStream(ms, CompressionStreamMode.Compress, DeflateCompressionLevel.Default, leaveOpen: true))
      ds.Write(data);
    // Adler-32 checksum (big-endian)
    var adler = ComputeAdler32(data);
    Span<byte> adlerBytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(adlerBytes, adler);
    ms.Write(adlerBytes);
    return ms.ToArray();
  }

  private static uint ComputeAdler32(ReadOnlySpan<byte> data) {
    uint a = 1, b = 0;
    foreach (var d in data) {
      a = (a + d) % 65521;
      b = (b + a) % 65521;
    }
    return (b << 16) | a;
  }

  private static string ComputeSha1Hex(byte[] data) {
    var hash = Sha1.Compute(data);
    var sb = new StringBuilder(hash.Length * 2);
    foreach (var b in hash)
      sb.Append(b.ToString("x2"));
    return sb.ToString();
  }
}
