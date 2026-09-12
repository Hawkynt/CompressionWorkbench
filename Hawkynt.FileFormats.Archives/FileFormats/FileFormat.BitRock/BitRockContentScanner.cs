using System.IO.Compression;
using System.Text;
using FileFormat.Tar;

namespace FileFormat.BitRock;

/// <summary>A gzip-wrapped tar component found in the reconstructed cookfs content.</summary>
/// <param name="Name">The tar's original name from the gzip FNAME field
/// (e.g. "InCAMPro.2.0SP1.246831.Win64.tar").</param>
/// <param name="ContentOffset">Offset of the gzip member within the reconstructed content.</param>
/// <param name="Length">Byte length of this member within the reconstructed content
/// (up to the next member or the content end).</param>
public sealed record BitRockPayloadComponent(string Name, long ContentOffset, long Length);

/// <summary>
/// Locates and recovers the application payload of a BitRock / InstallBuilder installer.
///
/// <para>Layout: <c>[PE image] [cookfs content region] [Metakit VFS] [16-byte trailer] [id] [end magic]</c>.
/// The content region is a <b>cookfs</b> (<c>CFS0002</c>) page archive (see <see cref="CookfsArchive"/>);
/// reconstructing it yields the real deliverable as one or more gzip-wrapped tars laid end to end. Each
/// component's FNAME field holds the tar's original name, e.g. <c>incamd.2.0.245314.Win64.tar</c>,
/// <c>InCAMPro.2.0SP1.246831.Win64.tar</c>, <c>InLink.1.5.235442.Win64.tar</c>.</para>
///
/// <para>Because the cookfs page store is stripped back to a plain gzip stream, decoding uses the stock
/// <see cref="GZipStream"/> — no private framing — so every component decodes end-to-end and every
/// extracted file is byte-exact. Reconstruction is streamed to a temporary file (bounded memory, never
/// the whole multi-hundred-megabyte payload in RAM), and extraction streams each entry to disk.</para>
/// </summary>
public static class BitRockContentScanner {

  /// <summary>
  /// Returns the offset at which the PE overlay (appended installer data) begins, i.e. the end of
  /// the last section's raw data. Returns 0 when the PE headers cannot be parsed.
  /// </summary>
  public static long GetOverlayStart(Stream stream) {
    try {
      Span<byte> dos = stackalloc byte[64];
      stream.Position = 0;
      if (stream.Read(dos) < 64 || dos[0] != (byte)'M' || dos[1] != (byte)'Z')
        return 0;
      var peOff = (long)(dos[60] | (dos[61] << 8) | (dos[62] << 16) | (dos[63] << 24));
      if (peOff <= 0 || peOff + 24 > stream.Length)
        return 0;

      Span<byte> coff = stackalloc byte[24];
      stream.Position = peOff;
      if (stream.Read(coff) < 24 || coff[0] != (byte)'P' || coff[1] != (byte)'E')
        return 0;
      var sections = coff[6] | (coff[7] << 8);
      var optSize = coff[20] | (coff[21] << 8);
      var tableOff = peOff + 24 + optSize;

      var overlay = 0L;
      Span<byte> sh = stackalloc byte[40];
      for (var i = 0; i < sections; i++) {
        stream.Position = tableOff + (long)i * 40;
        if (stream.Read(sh) < 40)
          break;
        var rawSize = (uint)(sh[16] | (sh[17] << 8) | (sh[18] << 16) | (sh[19] << 24));
        var rawPtr = (uint)(sh[20] | (sh[21] << 8) | (sh[22] << 16) | (sh[23] << 24));
        overlay = Math.Max(overlay, (long)rawPtr + rawSize);
      }
      return overlay < stream.Length ? overlay : 0;
    } catch {
      return 0;
    }
  }

  /// <summary>
  /// Reconstructs the cookfs content region (ending at <paramref name="cookfsEnd"/> = the Metakit VFS
  /// start) to a temporary file and returns its path, or null when there is no cookfs archive. The
  /// caller owns the file and must delete it. Memory stays bounded (pages are streamed).
  /// </summary>
  public static string? ReconstructContent(Stream stream, long cookfsEnd) {
    if (!CookfsArchive.TryOpen(stream, cookfsEnd, out var archive) || archive == null)
      return null;
    var tmp = Path.Combine(Path.GetTempPath(), "cwb_bitrock_content_" + Guid.NewGuid().ToString("N")[..12] + ".bin");
    try {
      using var dest = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
      archive.ReconstructTo(dest);
    } catch {
      try { File.Delete(tmp); } catch { /* best-effort */ }
      throw;
    }
    return tmp;
  }

  /// <summary>
  /// Scans reconstructed <paramref name="content"/> (seekable) for gzip members that carry a tar name
  /// in their FNAME field and returns them in order.
  /// </summary>
  public static List<BitRockPayloadComponent> ScanMembers(Stream content) {
    var found = new List<(long Offset, string Name)>();
    const int Chunk = 1 << 20;
    var buf = new byte[Chunk + 3];
    var carry = 0;
    var pos = 0L;
    var length = content.Length;
    Span<byte> name = stackalloc byte[128];

    while (pos < length) {
      var want = (int)Math.Min(Chunk, length - pos);
      content.Position = pos;                         // ReadFName seeks, so re-anchor the sequential scan
      var read = ReadUpTo(content, buf, carry, want);
      if (read <= 0)
        break;
      var total = carry + read;
      for (var i = 0; i + 4 <= total; i++) {
        if (buf[i] != 0x1f || buf[i + 1] != 0x8b || buf[i + 2] != 0x08 || (buf[i + 3] & 0x08) == 0)
          continue;
        var absOffset = pos - carry + i;
        var nm = ReadFName(content, absOffset, name);
        if (nm != null)
          found.Add((absOffset, nm));
      }
      carry = Math.Min(3, total);
      for (var k = 0; k < carry; k++)
        buf[k] = buf[total - carry + k];
      pos += read;
    }

    var result = new List<BitRockPayloadComponent>(found.Count);
    for (var i = 0; i < found.Count; i++) {
      var end = i + 1 < found.Count ? found[i + 1].Offset : length;
      result.Add(new BitRockPayloadComponent(found[i].Name, found[i].Offset, end - found[i].Offset));
    }
    return result;
  }

  /// <summary>Result of a full extraction of one payload component.</summary>
  /// <param name="FileCount">Regular files written to disk (each byte-exact).</param>
  /// <param name="DirCount">Directory entries seen.</param>
  /// <param name="TotalBytes">Sum of extracted file sizes.</param>
  /// <param name="CleanEnd">True when the gzip member decoded to its end without error.</param>
  public sealed record BitRockComponentExtract(long FileCount, long DirCount, long TotalBytes, bool CleanEnd);

  /// <summary>
  /// Enumerates a component's tar entries by streaming its gzip member — never materialising the
  /// (multi-hundred-MiB) component. Entry data is skipped, not held.
  /// </summary>
  public static IEnumerable<(string Path, long Size, bool IsDir)> EnumerateComponent(
      Stream content, BitRockPayloadComponent component) {
    content.Position = component.ContentOffset;
    using var member = new SubReadStream(content, component.Length);
    using var gz = new GZipStream(member, CompressionMode.Decompress, leaveOpen: true);
    using var tar = new TarReader(gz, leaveOpen: true);
    while (true) {
      TarEntry? entry;
      try {
        entry = tar.GetNextEntry();     // header checksum is validated here
      } catch {
        yield break;
      }
      if (entry == null)
        yield break;
      if (entry.IsDirectory)
        yield return (entry.Name, 0, true);
      else if (entry.IsFile)
        yield return (entry.Name, entry.Size, false);
    }
  }

  /// <summary>
  /// Fully extracts a payload component to <paramref name="rootDir"/>, streaming each file straight to
  /// disk. The gzip member is decoded by the stock <see cref="GZipStream"/> and every tar header is
  /// checksum-validated, so every written file is byte-exact.
  /// </summary>
  public static BitRockComponentExtract ExtractComponentToDisk(
      Stream content, BitRockPayloadComponent component, string rootDir, Func<string, bool>? accept = null) {
    content.Position = component.ContentOffset;
    using var member = new SubReadStream(content, component.Length);
    using var gz = new GZipStream(member, CompressionMode.Decompress, leaveOpen: true);
    using var tar = new TarReader(gz, leaveOpen: true);
    long files = 0, dirs = 0, bytes = 0;
    var clean = true;
    try {
      while (true) {
        var entry = tar.GetNextEntry();       // header checksum validated here
        if (entry == null)
          break;
        if (entry.IsDirectory) { ++dirs; continue; }
        if (!entry.IsFile)
          continue;
        if (accept != null && !accept(entry.Name))
          continue;

        var full = SafeJoin(rootDir, entry.Name);
        var dir = Path.GetDirectoryName(full);
        if (dir != null)
          Directory.CreateDirectory(dir);
        using (var es = tar.GetEntryStream())
        using (var outFs = File.Create(full))
          es.CopyTo(outFs, 1 << 20);
        ++files;
        bytes += entry.Size;
      }
    } catch {
      clean = false;      // a truncated/garbled member — keep the byte-exact prefix already written
    }
    return new BitRockComponentExtract(files, dirs, bytes, clean);
  }

  // ── helpers ─────────────────────────────────────────────────────────────────────

  private static int ReadUpTo(Stream stream, byte[] buf, int destOffset, int count) {
    var total = 0;
    while (total < count) {
      var r = stream.Read(buf, destOffset + total, count - total);
      if (r == 0)
        break;
      total += r;
    }
    return total;
  }

  private static string? ReadFName(Stream stream, long offset, Span<byte> scratch) {
    stream.Position = offset;
    Span<byte> hdr = stackalloc byte[10];
    if (stream.Read(hdr) < 10 || (hdr[3] & 0x08) == 0)
      return null;

    stream.Position = offset + 10;
    if (!ReadUntilNul(stream, scratch, out var len) || len is 0 or > 120)
      return null;
    for (var i = 0; i < len; i++)
      if (scratch[i] < 0x20 || scratch[i] >= 0x7f)
        return null;

    var name = Encoding.Latin1.GetString(scratch[..len]);
    var lower = name.ToLowerInvariant();
    if (!(lower.EndsWith(".tar") || lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz")
          || lower.EndsWith(".zip") || lower.EndsWith(".cpio")))
      return null;
    return name;

    static bool ReadUntilNul(Stream s, Span<byte> dst, out int written) {
      written = 0;
      Span<byte> one = stackalloc byte[1];
      while (written < dst.Length) {
        if (s.Read(one) < 1)
          return false;
        if (one[0] == 0)
          return true;
        dst[written++] = one[0];
      }
      return false;
    }
  }

  private static string SafeJoin(string root, string name) {
    var safe = name.Replace('\\', '/').TrimStart('/');
    if (safe.Contains(".."))
      safe = string.Join('/', safe.Split('/').Where(s => s != ".."));
    return Path.Combine(root, safe);
  }
}

/// <summary>Read-only view over the next <c>length</c> bytes of a seekable base stream.</summary>
internal sealed class SubReadStream(Stream baseStream, long length) : Stream {
  private readonly long _length = length;
  private long _remaining = length;

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => this._length;
  public override long Position { get => this._length - this._remaining; set => throw new NotSupportedException(); }
  public override void Flush() { }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  public override int Read(byte[] buffer, int offset, int count) {
    if (this._remaining <= 0)
      return 0;
    var want = (int)Math.Min(count, this._remaining);
    var got = baseStream.Read(buffer, offset, want);
    this._remaining -= got;
    return got;
  }
}
