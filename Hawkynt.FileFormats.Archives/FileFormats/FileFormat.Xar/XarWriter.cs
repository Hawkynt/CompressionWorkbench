using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Compression.Core.Checksums;

namespace FileFormat.Xar;

/// <summary>
/// Creates XAR (eXtensible ARchive) archives.
/// Entries are zlib-compressed by default.
/// </summary>
public sealed class XarWriter : IDisposable {
  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly List<(string name, byte[] data, DateTime modified)> _files = [];

  /// <summary>Creates a new XAR writer.</summary>
  public XarWriter(Stream output, bool leaveOpen = false) {
    _output = output;
    _leaveOpen = leaveOpen;
  }

  /// <summary>Adds a file to the archive.</summary>
  public void AddFile(string name, byte[] data, DateTime? modified = null) {
    _files.Add((name, data, modified ?? DateTime.UtcNow));
  }

  /// <summary>Writes the archive and flushes.</summary>
  public void Dispose() {
    WriteArchive();
    if (!_leaveOpen) _output.Dispose();
  }

  private void WriteArchive() {
    // Build heap: compress each file and collect metadata
    var heapEntries = new List<(string name, byte[] compressed, long offset, long origSize, long compSize, string origHash, string compHash, DateTime modified)>();
    long heapOffset = 0;

    foreach (var (name, data, modified) in _files) {
      var compressed = CompressZlib(data);
      var origHash = ComputeSha1Hex(data);
      var compHash = ComputeSha1Hex(compressed);
      heapEntries.Add((name, compressed, heapOffset, data.Length, compressed.Length, origHash, compHash, modified));
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

    // Write compressed TOC
    _output.Write(tocCompressed);

    // Write heap (compressed file data)
    foreach (var entry in heapEntries)
      _output.Write(entry.compressed);

    _output.Flush();
  }

  private static string BuildToc(List<(string name, byte[] compressed, long offset, long origSize, long compSize, string origHash, string compHash, DateTime modified)> entries) {
    var xar = new XElement("xar",
      new XElement("toc",
        new XElement("creation-time", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")),
        entries.Select((e, i) =>
          new XElement("file",
            new XAttribute("id", i.ToString()),
            new XElement("name", e.name),
            new XElement("type", "file"),
            new XElement("mtime", e.modified.ToString("yyyy-MM-ddTHH:mm:ssZ")),
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
          )
        )
      )
    );
    return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + xar.ToString();
  }

  private static byte[] CompressZlib(byte[] data) {
    using var ms = new MemoryStream();
    // Zlib header: CMF=0x78 (deflate, window 32KB), FLG=0x9C (default compression, check bits)
    ms.WriteByte(0x78);
    ms.WriteByte(0x9C);
    using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
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
