using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml.Linq;

namespace FileFormat.Xar;

/// <summary>
/// Reads XAR (eXtensible ARchive) archives.
/// XAR is Apple's archive format used for .pkg installers.
/// Header: "xar!" magic, header size, version, TOC compressed/uncompressed sizes, checksum algo.
/// TOC is zlib-compressed XML listing all entries.
/// Data is stored in a heap after the TOC.
/// </summary>
public sealed class XarReader {
  private readonly Stream _stream;
  private readonly List<XarEntry> _entries = [];
  private long _heapStart;

  /// <summary>All entries in the archive.</summary>
  public IReadOnlyList<XarEntry> Entries => _entries;

  /// <summary>Creates a new XAR reader for the given stream.</summary>
  public XarReader(Stream stream) {
    _stream = stream;
    ReadHeader();
  }

  private void ReadHeader() {
    Span<byte> hdr = stackalloc byte[28];
    ReadExact(_stream, hdr);

    // Magic: "xar!" (0x78617221)
    if (hdr[0] != 0x78 || hdr[1] != 0x61 || hdr[2] != 0x72 || hdr[3] != 0x21)
      throw new InvalidDataException("Not a valid XAR archive (missing 'xar!' magic).");

    var headerSize = BinaryPrimitives.ReadUInt16BigEndian(hdr[4..]);
    var version = BinaryPrimitives.ReadUInt16BigEndian(hdr[6..]);
    var tocCompressedSize = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr[8..]);
    var tocUncompressedSize = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr[16..]);
    var checksumAlgo = BinaryPrimitives.ReadUInt32BigEndian(hdr[24..]);

    // Seek past header to TOC
    _stream.Position = headerSize;

    // Read compressed TOC
    var compressedToc = new byte[tocCompressedSize];
    ReadExact(_stream, compressedToc);

    _heapStart = headerSize + tocCompressedSize;

    // Decompress TOC (always zlib)
    var tocBytes = DecompressZlib(compressedToc, (int)tocUncompressedSize);
    var tocXml = System.Text.Encoding.UTF8.GetString(tocBytes);

    ParseToc(tocXml);
  }

  private void ParseToc(string xml) {
    var doc = XDocument.Parse(xml);
    var toc = doc.Root?.Element("toc");
    if (toc == null) return;

    ParseFiles(toc, "");
  }

  private void ParseFiles(XElement parent, string pathPrefix) {
    foreach (var fileEl in parent.Elements("file")) {
      var name = fileEl.Element("name")?.Value ?? "unknown";
      var type = fileEl.Element("type")?.Value ?? "file";
      var fullPath = string.IsNullOrEmpty(pathPrefix) ? name : pathPrefix + "/" + name;

      if (type == "directory") {
        _entries.Add(new XarEntry {
          FileName = fullPath,
          IsDirectory = true,
          Method = "none",
        });
        // Recurse into directory
        ParseFiles(fileEl, fullPath);
      }
      else {
        var dataEl = fileEl.Element("data");
        long origSize = 0, compSize = 0, heapOffset = 0;
        var method = "none";
        string? archivedChecksum = null, extractedChecksum = null;
        DateTime? lastModified = null;

        if (dataEl != null) {
          origSize = ParseLong(dataEl.Element("size")?.Value);
          compSize = ParseLong(dataEl.Element("length")?.Value);
          heapOffset = ParseLong(dataEl.Element("offset")?.Value);
          method = dataEl.Element("encoding")?.Attribute("style")?.Value ?? "none";
          // Normalize method names
          if (method.Contains("zlib") || method.Contains("x-gzip"))
            method = "zlib";
          else if (method.Contains("bzip2"))
            method = "bzip2";
          else if (method.Contains("lzma") || method.Contains("xz"))
            method = "lzma";
          else if (method == "application/octet-stream" || method.Contains("none"))
            method = "none";

          archivedChecksum = dataEl.Element("archived-checksum")?.Value;
          extractedChecksum = dataEl.Element("extracted-checksum")?.Value;
        }

        var mtimeStr = fileEl.Element("mtime")?.Value;
        if (mtimeStr != null && DateTime.TryParse(mtimeStr, out var dt))
          lastModified = dt;

        _entries.Add(new XarEntry {
          FileName = fullPath,
          OriginalSize = origSize,
          CompressedSize = compSize,
          IsDirectory = false,
          Method = method,
          LastModified = lastModified,
          HeapOffset = heapOffset,
          ArchivedChecksum = archivedChecksum,
          ExtractedChecksum = extractedChecksum,
        });
      }
    }
  }

  /// <summary>Extracts the data for a given entry.</summary>
  public byte[] Extract(XarEntry entry) {
    if (entry.IsDirectory) return [];

    _stream.Position = _heapStart + entry.HeapOffset;
    var compressedData = new byte[entry.CompressedSize];
    ReadExact(_stream, compressedData);

    return entry.Method switch {
      "zlib" => DecompressZlib(compressedData, (int)entry.OriginalSize),
      "bzip2" => DecompressBzip2(compressedData),
      "none" or "" => compressedData,
      _ => compressedData, // unknown method, return raw
    };
  }

  private static byte[] DecompressZlib(byte[] data, int expectedSize) {
    // Zlib data: 2-byte header + deflate stream + 4-byte Adler32
    // .NET's DeflateStream can handle raw deflate if we skip the zlib header
    if (data.Length < 2)
      return data;

    // Skip zlib header (2 bytes) and omit trailing Adler32 (4 bytes)
    using var ms = new MemoryStream(data, 2, data.Length - 2);
    using var ds = new DeflateStream(ms, CompressionMode.Decompress);
    var result = new byte[expectedSize];
    var totalRead = 0;
    while (totalRead < expectedSize) {
      var read = ds.Read(result, totalRead, expectedSize - totalRead);
      if (read == 0) break;
      totalRead += read;
    }
    return result[..totalRead];
  }

  private static byte[] DecompressBzip2(byte[] data) {
    // Bzip2 decompression requires FileFormat.Bzip2 which we don't reference.
    // For XAR archives with bzip2 entries, return the raw compressed data.
    // The higher-level Compression.Lib can re-decompress if needed.
    throw new NotSupportedException("XAR entries compressed with bzip2 are not supported in standalone mode. Use Compression.Lib for full format support.");
  }

  private static long ParseLong(string? s) =>
    long.TryParse(s, out var v) ? v : 0;

  private static void ReadExact(Stream s, Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = s.Read(buffer[total..]);
      if (read == 0) throw new EndOfStreamException("Unexpected end of XAR stream.");
      total += read;
    }
  }
}
