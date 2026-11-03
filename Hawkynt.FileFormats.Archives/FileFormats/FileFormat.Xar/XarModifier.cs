#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Deflate;
using Compression.Core.Streams;
using System.Text;
using System.Xml.Linq;
using Compression.Core.Checksums;

namespace FileFormat.Xar;

/// <summary>
/// Random-access in-place modifier for XAR archives. Reads the existing
/// header + compressed XML TOC, mutates the parsed XML, recompresses, and
/// rewrites only the header + new TOC at the start of the file. The heap
/// (raw entry data) is shifted just enough to absorb the change in TOC
/// size — never re-encoded or re-hashed.
/// </summary>
/// <remarks>
/// <para><b>Layout</b>: <c>[28-byte header][zlib-compressed TOC][heap]</c>
/// where each <c>&lt;file&gt;&lt;data&gt;&lt;offset&gt;</c> in the TOC
/// is an offset relative to the heap start (i.e. relative to the byte
/// just after the compressed TOC).</para>
/// <para><b>Add layout</b>: appends new compressed entry data after the
/// last heap byte, builds a new <c>&lt;file&gt;</c> XML element pointing at
/// that offset, recompresses the TOC and rewrites <c>header + TOC</c>. If
/// the new compressed TOC differs in size from the old, the heap is shifted
/// (forward or backward) by exactly that delta. Since heap offsets are
/// heap-start-relative, no offset values inside the TOC need patching.</para>
/// <para><b>Remove layout</b>: drops the matching <c>&lt;file&gt;</c> XML
/// element, optionally zeros the orphan heap bytes, recompresses the TOC,
/// rewrites <c>header + TOC</c>. Heap is not compacted (gaps are legal in
/// XAR — readers index by explicit offset).</para>
/// <para><b>Folders</b>: a path with '/' names <c>&lt;file&gt;</c> elements of type
/// <c>directory</c> nested in each other, as xar writes them; missing folders are created,
/// and removing a folder removes everything in it.</para>
/// <para><b>TOC checksum</b>: when the TOC names one (<c>&lt;checksum style="sha1"&gt;</c>
/// with its heap offset and size, as xar and libarchive write it), it is recomputed over the
/// new compressed TOC; xar and libarchive reject an archive whose checksum is stale.</para>
/// <para>Added entries are stored with zlib compression at
/// <see cref="DeflateCompressionLevel.Default"/>; everything else in the TOC — owners,
/// extended attributes, other entries' encodings — is left as it was.</para>
/// </remarks>
public static class XarModifier {

  private const int HeaderSize = 28;

  /// <summary>
  /// Adds a regular file entry to an existing XAR archive. The new entry's
  /// data is zlib-compressed and appended to the end of the heap; the TOC
  /// is then recompressed and rewritten at the start of the file with the
  /// heap shifted by the TOC-size delta.
  /// </summary>
  public static void AddFile(Stream xar, string name, byte[] data, DateTime? lastModified = null, int? mode = null) {
    ArgumentNullException.ThrowIfNull(xar);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var segments = Segments(name);
    if (segments.Length == 0) throw new ArgumentException("XAR: an entry needs a name.", nameof(name));

    var (oldTocCompSize, doc) = ReadHeaderAndToc(xar);
    var toc = RequireToc(doc);

    // Compute the current end-of-heap (max offset+length across all data elements).
    var heapEnd = ComputeHeapEnd(toc);

    // Compress the new entry and append it at heap-end.
    var compressed = CompressZlib(data);
    var origHash = ComputeSha1Hex(data);
    var compHash = ComputeSha1Hex(compressed);
    var newOffset = heapEnd;
    var modified = lastModified ?? DateTime.UtcNow;

    var heapStart = (long)HeaderSize + oldTocCompSize;
    var absoluteWriteOffset = heapStart + newOffset;

    // The folders it goes into are resolved (and refused) before a byte is written.
    var nextId = NextFreeId(toc);
    var parent = EnsureDirectory(toc, segments[..^1], modified, ref nextId);

    // Write the new compressed payload at the absolute heap-end position
    // BEFORE we touch the TOC, so the heap grows in-place. The subsequent
    // TOC rewrite may shift this whole tail, but the bytes are correct.
    if (compressed.Length > 0) {
      xar.Position = absoluteWriteOffset;
      xar.Write(compressed);
      if (xar.Position > xar.Length) xar.SetLength(xar.Position);
    }

    // Build and append the new <file> element, inside its folders.
    var fileEl = BuildFileElement(nextId, segments[^1], modified, mode ?? DefaultFileMode, data.Length, compressed.Length, newOffset, origHash, compHash);
    parent.Add(fileEl);

    RewriteHeaderAndTocWithHeapShift(xar, doc, oldTocCompSize, heapEnd + compressed.Length);
  }

  /// <summary>
  /// Adds a folder (with any missing parents) to an existing XAR archive; a folder that is
  /// already there is left as it is.
  /// </summary>
  public static void AddDirectory(Stream xar, string name, DateTime? lastModified = null, int? mode = null) {
    ArgumentNullException.ThrowIfNull(xar);
    ArgumentNullException.ThrowIfNull(name);
    var segments = Segments(name);
    if (segments.Length == 0) return;

    var (oldTocCompSize, doc) = ReadHeaderAndToc(xar);
    var toc = RequireToc(doc);
    if (Resolve(toc, segments) != null) return;
    var heapEnd = ComputeHeapEnd(toc);
    var nextId = NextFreeId(toc);
    var folder = EnsureDirectory(toc, segments, lastModified ?? DateTime.UtcNow, ref nextId);
    if (mode is { } m) folder.SetElementValue("mode", FormatMode(m));
    RewriteHeaderAndTocWithHeapShift(xar, doc, oldTocCompSize, heapEnd);
  }

  /// <summary>
  /// Removes the named entry from a XAR archive. Returns true if the entry
  /// was found and dropped from the TOC. When <paramref name="wipeData"/>
  /// is true (default) the orphan heap bytes are zeroed in place. The heap
  /// itself is not compacted: XAR readers locate entries by explicit
  /// <c>&lt;offset&gt;</c>, so leaving gaps is legal.
  /// </summary>
  public static bool RemoveFile(Stream xar, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(xar);
    ArgumentNullException.ThrowIfNull(name);

    var (oldTocCompSize, doc) = ReadHeaderAndToc(xar);
    var toc = RequireToc(doc);
    var heapStart = (long)HeaderSize + oldTocCompSize;

    // The entry at exactly that path; a folder goes with everything in it.
    var victim = Resolve(toc, Segments(name));
    if (victim == null) return false;

    var heapEnd = ComputeHeapEnd(toc);
    var orphans = victim.DescendantsAndSelf("file")
      .Select(f => f.Element("data"))
      .OfType<XElement>()
      .Select(d => (Offset: ParseLong(d.Element("offset")?.Value), Length: ParseLong(d.Element("length")?.Value)))
      .ToList();
    victim.Remove();

    // Wipe orphan heap bytes BEFORE the TOC rewrite shifts them around.
    if (wipeData)
      foreach (var (offset, length) in orphans) {
        if (length <= 0) continue;
        var absStart = heapStart + offset;
        if (absStart >= heapStart && absStart + length <= xar.Length)
          ZeroRange(xar, absStart, length);
      }

    RewriteHeaderAndTocWithHeapShift(xar, doc, oldTocCompSize, heapEnd);
    return true;
  }

  // ── Header + TOC I/O ─────────────────────────────────────────────────

  private static (long TocCompSize, XDocument Doc) ReadHeaderAndToc(Stream xar) {
    if (xar.Length < HeaderSize)
      throw new InvalidDataException("XAR stream too small to contain a header.");

    xar.Position = 0;
    Span<byte> hdr = stackalloc byte[HeaderSize];
    ReadExact(xar, hdr);
    if (hdr[0] != 0x78 || hdr[1] != 0x61 || hdr[2] != 0x72 || hdr[3] != 0x21)
      throw new InvalidDataException("Not a valid XAR archive (missing 'xar!' magic).");

    var headerSize = BinaryPrimitives.ReadUInt16BigEndian(hdr[4..]);
    var tocCompressedSize = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr[8..]);
    var tocUncompressedSize = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr[16..]);

    if (headerSize != HeaderSize)
      throw new InvalidDataException($"XAR header size {headerSize} not supported (expected {HeaderSize}).");

    xar.Position = headerSize;
    var compressedToc = new byte[tocCompressedSize];
    ReadExact(xar, compressedToc);

    var tocBytes = DecompressZlib(compressedToc, (int)tocUncompressedSize);
    var doc = XDocument.Parse(Encoding.UTF8.GetString(tocBytes));
    return (tocCompressedSize, doc);
  }

  private static XElement RequireToc(XDocument doc) {
    var toc = doc.Root?.Element("toc")
      ?? throw new InvalidDataException("XAR TOC missing <toc> element.");
    return toc;
  }

  /// <summary>
  /// Recompresses the TOC, computes the size delta vs the old compressed
  /// TOC, shifts the heap (forward or backward) by exactly that delta, then
  /// rewrites the 28-byte header + new compressed TOC at offset 0. Heap
  /// offsets inside the TOC are heap-start-relative and need no patching.
  /// </summary>
  private static void RewriteHeaderAndTocWithHeapShift(Stream xar, XDocument doc, long oldTocCompSize, long heapEnd) {
    var newTocBytes = Encoding.UTF8.GetBytes(SerializeToc(doc));
    var newTocCompressed = CompressZlib(newTocBytes);
    var delta = (long)newTocCompressed.Length - oldTocCompSize;

    var oldHeapStart = (long)HeaderSize + oldTocCompSize;
    var newHeapStart = (long)HeaderSize + newTocCompressed.Length;

    // Shift the heap (heapEnd - oldHeapStart bytes starting at oldHeapStart)
    // by 'delta' bytes (positive = forward / right, negative = backward / left).
    var heapBytes = heapEnd; // heap is heapEnd bytes long (heapEnd is heap-relative end)
    if (delta > 0)
      ShiftRight(xar, oldHeapStart, heapBytes, delta);
    else if (delta < 0)
      ShiftLeft(xar, oldHeapStart, heapBytes, -delta);

    // Write the new compressed TOC at offset HeaderSize.
    xar.Position = HeaderSize;
    xar.Write(newTocCompressed);

    // Patch the 28-byte header in place (only TOC sizes change; checksum
    // algo, magic, version and header_size stay the same).
    xar.Position = 8;
    Span<byte> sizes = stackalloc byte[16];
    BinaryPrimitives.WriteUInt64BigEndian(sizes[..8], (ulong)newTocCompressed.Length);
    BinaryPrimitives.WriteUInt64BigEndian(sizes[8..], (ulong)newTocBytes.Length);
    xar.Write(sizes);

    // The TOC checksum in the heap covers the compressed TOC just written.
    if (TocChecksum(RequireToc(doc), newTocCompressed) is { } checksum) {
      xar.Position = newHeapStart + checksum.Offset;
      xar.Write(checksum.Hash);
    }

    // Truncate to the new total length (header + new TOC + heap).
    xar.SetLength(newHeapStart + heapBytes);
  }

  private static void ShiftRight(Stream s, long srcOffset, long length, long delta) {
    if (length <= 0 || delta == 0) return;
    // Grow file first so we can write past the old end.
    var required = srcOffset + length + delta;
    if (s.Length < required) s.SetLength(required);
    var buf = new byte[(int)Math.Min(length, 64 * 1024)];
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(buf.Length, remaining);
      var readPos = srcOffset + remaining - chunk;
      s.Position = readPos;
      ReadFully(s, buf.AsSpan(0, chunk));
      s.Position = readPos + delta;
      s.Write(buf, 0, chunk);
      remaining -= chunk;
    }
  }

  private static void ShiftLeft(Stream s, long srcOffset, long length, long delta) {
    if (length <= 0 || delta == 0) return;
    var buf = new byte[(int)Math.Min(length, 64 * 1024)];
    long copied = 0;
    while (copied < length) {
      var chunk = (int)Math.Min(buf.Length, length - copied);
      s.Position = srcOffset + copied;
      ReadFully(s, buf.AsSpan(0, chunk));
      s.Position = srcOffset + copied - delta;
      s.Write(buf, 0, chunk);
      copied += chunk;
    }
  }

  // ── TOC XML helpers ──────────────────────────────────────────────────

  /// <summary>The TOC checksum's heap offset and the hash of <paramref name="compressedToc"/> in its style.</summary>
  private static (long Offset, byte[] Hash)? TocChecksum(XElement toc, byte[] compressedToc) {
    var checksum = toc.Element("checksum");
    if (checksum == null) return null;
    byte[]? hash = checksum.Attribute("style")?.Value.ToLowerInvariant() switch {
      "sha1" => Sha1.Compute(compressedToc),
      "md5" => Md5.Compute(compressedToc),
      "sha256" => Sha256.Compute(compressedToc),
      _ => null,
    };
    if (hash == null || ParseLong(checksum.Element("size")?.Value) != hash.Length) return null;
    return (ParseLong(checksum.Element("offset")?.Value), hash);
  }

  private static string[] Segments(string name) => name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

  private static XElement? Resolve(XElement toc, string[] segments) {
    XElement? current = null;
    var parent = toc;
    foreach (var segment in segments) {
      current = parent.Elements("file").FirstOrDefault(f => string.Equals(f.Element("name")?.Value, segment, StringComparison.Ordinal));
      if (current == null) return null;
      parent = current;
    }
    return current;
  }

  /// <summary>The folder element at <paramref name="segments"/>, created with its parents when missing.</summary>
  private static XElement EnsureDirectory(XElement toc, string[] segments, DateTime modified, ref int nextId) {
    var parent = toc;
    foreach (var segment in segments) {
      var child = parent.Elements("file").FirstOrDefault(f => string.Equals(f.Element("name")?.Value, segment, StringComparison.Ordinal));
      if (child == null) {
        child = new XElement("file",
          new XAttribute("id", (nextId++).ToString()),
          new XElement("name", segment),
          new XElement("type", "directory"),
          new XElement("mode", FormatMode(DefaultDirectoryMode)),
          new XElement("mtime", modified.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")));
        parent.Add(child);
      } else if (child.Element("type")?.Value is { } type && type != "directory") {
        throw new IOException($"XAR: '{segment}' is a {type}, not a folder.");
      }
      parent = child;
    }
    return parent;
  }

  private const int DefaultFileMode = 0x1A4;      // 0644
  private const int DefaultDirectoryMode = 0x1ED; // 0755

  private static string FormatMode(int mode) => "0" + Convert.ToString(mode & 0xFFF, 8);

  private static long ComputeHeapEnd(XElement toc) {
    long max = 0;
    // The TOC checksum lives in the heap too (usually at offset 0).
    if (toc.Element("checksum") is { } checksum)
      max = ParseLong(checksum.Element("offset")?.Value) + ParseLong(checksum.Element("size")?.Value);
    foreach (var fileEl in toc.Descendants("file")) {
      var dataEl = fileEl.Element("data");
      if (dataEl == null) continue;
      var off = ParseLong(dataEl.Element("offset")?.Value);
      var len = ParseLong(dataEl.Element("length")?.Value);
      if (off + len > max) max = off + len;
    }
    return max;
  }

  private static int NextFreeId(XElement toc) {
    var max = -1;
    foreach (var fileEl in toc.Descendants("file")) {
      var idAttr = fileEl.Attribute("id");
      if (idAttr == null) continue;
      if (int.TryParse(idAttr.Value, out var id) && id > max) max = id;
    }
    return max + 1;
  }

  private static XElement BuildFileElement(int id, string name, DateTime modified, int mode,
      long origSize, long compSize, long offset, string origHash, string compHash) =>
    new("file",
      new XAttribute("id", id.ToString()),
      new XElement("name", name),
      new XElement("type", "file"),
      new XElement("mode", FormatMode(mode)),
      new XElement("mtime", modified.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")),
      new XElement("data",
        new XElement("size", origSize.ToString()),
        new XElement("length", compSize.ToString()),
        new XElement("offset", offset.ToString()),
        new XElement("encoding", new XAttribute("style", "application/x-gzip")),
        new XElement("extracted-checksum", new XAttribute("style", "sha1"), origHash),
        new XElement("archived-checksum", new XAttribute("style", "sha1"), compHash)
      ));

  private static string SerializeToc(XDocument doc) =>
    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + (doc.Root?.ToString() ?? string.Empty);

  // ── Compression / hashing primitives (mirrored from XarWriter) ───────

  private static byte[] CompressZlib(byte[] data) {
    using var ms = new MemoryStream();
    ms.WriteByte(0x78);
    ms.WriteByte(0x9C);
    using (var ds = new RawDeflateStream(ms, CompressionStreamMode.Compress, DeflateCompressionLevel.Default, leaveOpen: true))
      ds.Write(data);
    var adler = ComputeAdler32(data);
    Span<byte> adlerBytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(adlerBytes, adler);
    ms.Write(adlerBytes);
    return ms.ToArray();
  }

  private static byte[] DecompressZlib(byte[] data, int expectedSize) {
    if (data.Length < 2) return data;
    using var ms = new MemoryStream(data, 2, data.Length - 2);
    using var ds = new RawDeflateStream(ms, CompressionStreamMode.Decompress);
    var capacity = expectedSize > 0 ? expectedSize : 4096;
    using var outMs = new MemoryStream(capacity);
    ds.CopyTo(outMs);
    return outMs.ToArray();
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
    foreach (var b in hash) sb.Append(b.ToString("x2"));
    return sb.ToString();
  }

  // ── Stream / parsing utilities ───────────────────────────────────────

  private static long ParseLong(string? s) => long.TryParse(s, out var v) ? v : 0;

  private static void ReadExact(Stream s, Span<byte> buf) {
    var total = 0;
    while (total < buf.Length) {
      var n = s.Read(buf[total..]);
      if (n <= 0) throw new EndOfStreamException("Unexpected end of XAR stream.");
      total += n;
    }
  }

  private static void ReadFully(Stream s, Span<byte> buf) {
    var total = 0;
    while (total < buf.Length) {
      var n = s.Read(buf[total..]);
      if (n <= 0) throw new EndOfStreamException("Unexpected end of stream during heap shift.");
      total += n;
    }
  }

  private static void ZeroRange(Stream s, long offset, long length) {
    var buf = new byte[(int)Math.Min(length, 8192)];
    s.Position = offset;
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(buf.Length, remaining);
      s.Write(buf, 0, chunk);
      remaining -= chunk;
    }
  }
}
