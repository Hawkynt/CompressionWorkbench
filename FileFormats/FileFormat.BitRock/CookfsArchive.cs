using System.IO.Compression;

namespace FileFormat.BitRock;

/// <summary>
/// Reader for the <b>cookfs</b> (<c>CFS0002</c>) archive that holds a BitRock / InstallBuilder
/// installer's application payload. The content region between the PE image and the tclkit runtime's
/// Metakit VFS is a cookfs page store; reconstructing it yields the real deliverable (one or more
/// gzip-wrapped tars laid end to end).
///
/// <para><b>Layout (reverse-engineered from the runtime's own cookfs reader).</b> The archive ends,
/// just before the Metakit VFS, with a 16-byte footer whose bytes 9..15 are the ASCII signature
/// <c>CFS0002</c> and whose first 8 bytes are two big-endian int32s <c>idxsize</c> and <c>numpages</c>.
/// Immediately before the footer sit, in order: a per-page 16-byte MD5/CRC table (<c>numpages*16</c>),
/// a per-page big-endian int32 <b>page-size</b> table (<c>numpages*4</c>), and the compressed
/// <c>fsindex</c> (<c>idxsize</c> bytes). The pages themselves are stored consecutively ending at the
/// start of that MD5 table, so the first page begins at
/// <c>indexoffset - Σ pageSizes</c> (= the content-region start).</para>
///
/// <para><b>Page format.</b> Each page is one compression-id byte (<c>cid</c>) followed by its data:
/// <c>cid 0</c> = stored (data verbatim), <c>cid 1</c> = raw DEFLATE (no zlib wrapper), <c>cid 2</c> =
/// bzip2. Decompressing every page in order and concatenating the results reconstructs the content
/// byte-exactly — a plain gzip stream with no private framing, which any decoder reads.</para>
/// </summary>
internal sealed class CookfsArchive {

  /// <summary>The 7-byte cookfs archive signature (footer bytes 9..15).</summary>
  public static ReadOnlySpan<byte> Signature => "CFS0002"u8;

  private readonly Stream _s;
  private readonly long[] _pageOffset;   // absolute file offset of each page
  private readonly int[] _pageSize;      // stored length of each page (cid byte + data)

  private CookfsArchive(Stream s, long[] pageOffset, int[] pageSize) {
    this._s = s;
    this._pageOffset = pageOffset;
    this._pageSize = pageSize;
  }

  /// <summary>Number of pages in the archive.</summary>
  public int PageCount => this._pageSize.Length;

  /// <summary>
  /// Parses the cookfs footer/page-table ending at <paramref name="endOffset"/> (the offset at which
  /// the following Metakit VFS begins). Cheap — reads only the tables, not the page data.
  /// </summary>
  public static bool TryOpen(Stream s, long endOffset, out CookfsArchive? archive) {
    archive = null;
    if (endOffset < 16 || endOffset > s.Length)
      return false;

    Span<byte> fc = stackalloc byte[16];
    s.Position = endOffset - 16;
    if (s.Read(fc) < 16 || !fc.Slice(9, 7).SequenceEqual(Signature))
      return false;

    var idxsize = ReadBe32(fc, 0);
    var numpages = ReadBe32(fc, 4);
    if (idxsize < 0 || numpages < 0 || numpages > 100_000_000)
      return false;

    var indexOffset = endOffset - (16L + idxsize + (long)numpages * 20);
    if (indexOffset < 0)
      return false;

    var sizeData = new byte[(long)numpages * 4 <= int.MaxValue ? numpages * 4 : 0];
    if (sizeData.Length != numpages * 4)
      return false;
    s.Position = indexOffset + (long)numpages * 16;    // skip the MD5/CRC table
    s.ReadExactly(sizeData);

    var sizes = new int[numpages];
    long total = 0;
    for (var i = 0; i < numpages; i++) {
      var sz = ReadBe32(sizeData, i * 4);
      if (sz < 1) return false;
      sizes[i] = sz;
      total += sz;
    }
    var startOffset = indexOffset - total;
    if (startOffset < 0)
      return false;

    var offsets = new long[numpages];
    var o = startOffset;
    for (var i = 0; i < numpages; i++) {
      offsets[i] = o;
      o += sizes[i];
    }
    archive = new CookfsArchive(s, offsets, sizes);
    return true;
  }

  /// <summary>
  /// Reconstructs the whole content region to <paramref name="dest"/> by decompressing every page in
  /// order and concatenating — memory-bounded (stored pages stream through; compressed pages are small).
  /// Returns the number of content bytes written.
  /// </summary>
  public long ReconstructTo(Stream dest) {
    long written = 0;
    var buf = new byte[1 << 20];
    for (var i = 0; i < this._pageSize.Length; i++) {
      var size = this._pageSize[i];
      this._s.Position = this._pageOffset[i];
      var cid = this._s.ReadByte();
      if (cid < 0)
        break;
      var dataLen = size - 1;
      switch (cid) {
        case 0:                                        // stored
          written += CopyExact(this._s, dest, dataLen, buf);
          break;
        case 1: {                                      // raw DEFLATE (no zlib wrapper)
          var comp = new byte[dataLen];
          this._s.ReadExactly(comp);
          using var src = new MemoryStream(comp, writable: false);
          using var inf = new DeflateStream(src, CompressionMode.Decompress);
          written += inf.CopyTo2(dest, buf);
          break;
        }
        default:
          throw new InvalidDataException($"Unsupported cookfs page compression id {cid} (page {i}).");
      }
    }
    return written;
  }

  private static long CopyExact(Stream src, Stream dest, long count, byte[] buf) {
    var left = count;
    while (left > 0) {
      var want = (int)Math.Min(buf.Length, left);
      var got = src.Read(buf, 0, want);
      if (got == 0)
        break;
      dest.Write(buf, 0, got);
      left -= got;
    }
    return count - left;
  }

  private static int ReadBe32(ReadOnlySpan<byte> s, int off)
    => (s[off] << 24) | (s[off + 1] << 16) | (s[off + 2] << 8) | s[off + 3];
}

file static class StreamCopyExtensions {
  public static long CopyTo2(this Stream src, Stream dest, byte[] buf) {
    long total = 0;
    int got;
    while ((got = src.Read(buf, 0, buf.Length)) > 0) {
      dest.Write(buf, 0, got);
      total += got;
    }
    return total;
  }
}
