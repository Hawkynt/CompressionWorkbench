using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.Streams;
using FileFormat.Bzip2;
using FileFormat.Lzma;
using FileFormat.Xz;
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

  /// <summary>Byte offset where the data heap begins (after header + TOC).</summary>
  public long HeapStart => _heapStart;

  /// <summary>Creates a new XAR reader for the given stream.</summary>
  public XarReader(Stream stream) {
    _stream = stream;
    ReadHeader();
  }

  private void ReadHeader() {
    Span<byte> hdr = stackalloc byte[28];
    _stream.ReadExactly(hdr);

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
    _stream.ReadExactly(compressedToc);

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
        string? archivedChecksum = null, extractedChecksum = null, extractedChecksumStyle = null;
        DateTime? lastModified = null;

        if (dataEl != null) {
          origSize = ParseLong(dataEl.Element("size")?.Value);
          compSize = ParseLong(dataEl.Element("length")?.Value);
          heapOffset = ParseLong(dataEl.Element("offset")?.Value);
          method = NormalizeMethod(dataEl.Element("encoding")?.Attribute("style")?.Value);

          archivedChecksum = dataEl.Element("archived-checksum")?.Value;
          extractedChecksum = dataEl.Element("extracted-checksum")?.Value;
          extractedChecksumStyle = dataEl.Element("extracted-checksum")?.Attribute("style")?.Value;
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
          ExtractedChecksumStyle = extractedChecksumStyle,
        });
      }
    }
  }

  /// <summary>Maps an encoding style (a MIME type) to the method name <see cref="XarEntry.Method"/> reports.</summary>
  /// <remarks>
  /// xar names its encodings by MIME type: <c>application/octet-stream</c> (stored),
  /// <c>application/x-gzip</c> (a zlib stream, despite the name), <c>application/x-bzip2</c>,
  /// <c>application/x-lzma</c> and <c>application/x-xz</c>. An unrecognised style is kept verbatim so
  /// extraction can refuse it by name rather than hand back encoded bytes.
  /// </remarks>
  private static string NormalizeMethod(string? style) => style switch {
    null or "" or "application/octet-stream" => "none",
    "application/x-gzip" or "application/zlib" or "application/x-zlib" => "zlib",
    "application/x-bzip2" => "bzip2",
    "application/x-lzma" => "lzma",
    "application/x-xz" => "xz",
    _ => style,
  };

  /// <summary>Extracts the data for a given entry and checks it against the TOC's extracted checksum.</summary>
  /// <exception cref="NotSupportedException">The entry's encoding is not one xar defines.</exception>
  /// <exception cref="InvalidDataException">The decoded data is the wrong size or fails its checksum.</exception>
  public byte[] Extract(XarEntry entry) {
    if (entry.IsDirectory) return [];

    _stream.Position = _heapStart + entry.HeapOffset;
    var compressedData = new byte[entry.CompressedSize];
    _stream.ReadExactly(compressedData);

    var data = entry.Method switch {
      "none" => compressedData,
      "zlib" => DecompressZlib(compressedData, (int)entry.OriginalSize),
      "bzip2" => DecodeStream(compressedData, s => new Bzip2Stream(s, CompressionStreamMode.Decompress, leaveOpen: true)),
      // xar's own lzma encoder (lzma_easy_encoder) writes .xz streams under "application/x-lzma";
      // libarchive writes the legacy .lzma ("alone") format there. Both readers sniff, so do we.
      "lzma" or "xz" when IsXz(compressedData) =>
        DecodeStream(compressedData, s => new XzStream(s, CompressionStreamMode.Decompress, leaveOpen: true)),
      "lzma" => DecodeLzmaAlone(compressedData),
      "xz" => throw new InvalidDataException($"XAR entry '{entry.FileName}' is marked xz but lacks the xz stream magic."),
      _ => throw new NotSupportedException($"XAR entry '{entry.FileName}' uses unsupported encoding '{entry.Method}'."),
    };

    if (data.LongLength != entry.OriginalSize)
      throw new InvalidDataException(
        $"XAR entry '{entry.FileName}' decoded to {data.LongLength} bytes; the TOC says {entry.OriginalSize}.");
    VerifyChecksum(entry, data);
    return data;
  }

  private static bool IsXz(ReadOnlySpan<byte> data) =>
    data.Length >= 6 && data[..6].SequenceEqual((ReadOnlySpan<byte>)[0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]);

  private static byte[] DecodeStream(byte[] data, Func<Stream, Stream> open) {
    using var input = new MemoryStream(data, writable: false);
    using var decoder = open(input);
    using var output = new MemoryStream();
    decoder.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] DecodeLzmaAlone(byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    LzmaStream.Decompress(input, output);
    return output.ToArray();
  }

  /// <summary>Checks <paramref name="data"/> against the TOC's <c>extracted-checksum</c> when it names a known hash.</summary>
  private static void VerifyChecksum(XarEntry entry, byte[] data) {
    if (string.IsNullOrWhiteSpace(entry.ExtractedChecksum) || entry.ExtractedChecksumStyle is null)
      return;
    byte[]? actual = entry.ExtractedChecksumStyle.ToLowerInvariant() switch {
      "sha1" => Sha1.Compute(data),
      "md5" => Md5.Compute(data),
      "sha256" => Sha256.Compute(data),
      _ => null,
    };
    if (actual is null)
      return;
    if (!string.Equals(Convert.ToHexString(actual), entry.ExtractedChecksum.Trim(), StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException(
        $"XAR entry '{entry.FileName}' fails its {entry.ExtractedChecksumStyle} extracted-checksum.");
  }

  private static byte[] DecompressZlib(byte[] data, int expectedSize) {
    // Zlib data: 2-byte header + deflate stream + 4-byte Adler32; the raw Deflate decoder takes it
    // once the header is skipped, and stops by itself before the trailer.
    if (data.Length < 2)
      throw new InvalidDataException("XAR zlib stream is shorter than its two-byte header.");

    using var ms = new MemoryStream(data, 2, data.Length - 2);
    using var ds = new RawDeflateStream(ms, CompressionStreamMode.Decompress);
    var result = new byte[expectedSize];
    var totalRead = 0;
    while (totalRead < expectedSize) {
      var read = ds.Read(result, totalRead, expectedSize - totalRead);
      if (read == 0) break;
      totalRead += read;
    }
    return result[..totalRead];
  }

  private static long ParseLong(string? s) =>
    long.TryParse(s, out var v) ? v : 0;
}
