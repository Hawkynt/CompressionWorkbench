#pragma warning disable CS1591
using System.Buffers;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace FileFormat.Zip;

/// <summary>
/// Random-access in-place modifier for ZIP archives. Reads and writes only
/// the central directory, the EOCD record, and (for new files) the appended
/// local file header + compressed data — never the entire archive payload.
/// Lets callers operate on multi-GB ZIP files without rebuild cost.
/// </summary>
/// <remarks>
/// <para><b>Add layout</b>: appends new local file headers + data at the
/// position of the old central directory, then rewrites the CD with old
/// entries (their LFH offsets unchanged) followed by the new entries.</para>
/// <para><b>Remove layout</b>: rewrites the CD without the named entries;
/// orphan LFH+data bytes remain in place. When <c>wipeData</c> is true the
/// orphan bytes are zeroed for forensic cleanliness.</para>
/// <para>Limitations: does not support encrypted entries, ZIP64 archives
/// with multiple disks, or multi-volume archives. Added entries use Deflate
/// at the default level, or Store when Deflate does not shrink them.</para>
/// </remarks>
public static class ZipModifier {

  /// <summary>
  /// Adds a file to an existing ZIP archive, encoding it with Deflate. If an
  /// entry with the same name already exists the caller should
  /// <see cref="RemoveFile"/> it first; this method just appends.
  /// </summary>
  public static void AddFile(Stream zip, string name, byte[] data, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(data);
    using var source = new MemoryStream(data, writable: false);
    AddFile(zip, name, source, lastModified);
  }

  /// <summary>
  /// Adds a file to an existing ZIP archive, streaming <paramref name="data"/> from its
  /// current position to its end through Deflate straight into the archive. Memory use
  /// is bounded by the copy buffer, not by the file size, so files larger than 2 GiB
  /// are fine. If an entry with the same name already exists the caller should
  /// <see cref="RemoveFile"/> it first; this method just appends.
  /// </summary>
  /// <remarks>
  /// <para>When Deflate does not shrink the data and <paramref name="data"/> is seekable
  /// the entry is rewritten with the Store method instead; a non-seekable source keeps
  /// the (slightly larger) Deflate encoding because it cannot be read twice.</para>
  /// <para>A seekable source is read for exactly the length it reported up front. When
  /// that length is unknown (non-seekable) or at least 0xFFFFFFFF the local header
  /// reserves the ZIP64 extended-information field (APPNOTE.TXT 4.3.9, 4.5.3) so the
  /// final sizes can be patched in without moving the data. The central directory and
  /// end record switch to their ZIP64 forms whenever a size, the entry's header offset
  /// or the directory offset needs it.</para>
  /// </remarks>
  public static void AddFile(Stream zip, string name, Stream data, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(zip);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!data.CanRead)
      throw new ArgumentException("The source stream must be readable.", nameof(data));

    // Existing entries keep their directory records byte for byte; only the new one is serialised.
    var (records, cdOffset, comment) = ZipRawDirectory.Read(zip);

    // The new entry overwrites the old central directory while it streams in. Keep that
    // tail (directory + end record, never file data) so a source that fails half-way
    // leaves the archive exactly as it was instead of without a directory.
    var oldTail = ReadTail(zip, cdOffset);
    try {
      zip.Position = cdOffset;
      var added = AppendEntry(zip, name, data, lastModified);

      // Rewrite the central directory + EOCD at the new tail position.
      ZipRawDirectory.Write(zip, records, [added], comment);
      zip.SetLength(zip.Position);
    } catch {
      zip.Position = cdOffset;
      zip.Write(oldTail);
      zip.SetLength(cdOffset + oldTail.Length);
      throw;
    }
  }

  /// <summary>
  /// Writes the local header and payload of a new entry at the current position, leaving
  /// the position just past the payload, and returns the finished entry.
  /// </summary>
  private static ZipEntry AppendEntry(Stream zip, string name, Stream data, DateTime? lastModified) {
    long? knownLength = data.CanSeek ? Math.Max(0, data.Length - data.Position) : null;
    var sourceStart = data.CanSeek ? data.Position : 0;
    var reserveZip64 = knownLength is not { } length || length >= uint.MaxValue;

    // The header goes out with placeholder CRC/sizes first and is patched once they are known.
    var localHeaderOffset = zip.Position;
    var entry = new ZipEntry {
      FileName = name,
      CompressionMethod = ZipCompressionMethod.Deflate,
      LastModified = lastModified ?? new DateTime(1980, 1, 1),
      LocalHeaderOffset = localHeaderOffset,
    };
    WriteLocalHeader(zip, entry, reserveZip64);
    var dataStart = zip.Position;

    var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
    try {
      var crc = new Crc32();
      long uncompressed = 0;
      int read;
      using (var output = new BufferedStream(new NonClosingStream(zip), CopyBufferSize)) {
        var deflater = new DeflateCompressor(output, DeflateCompressionLevel.Default);
        while ((read = ReadChunk(data, buffer, knownLength, uncompressed)) > 0) {
          var chunk = buffer.AsSpan(0, read);
          crc.Update(chunk);
          deflater.Write(chunk);
          uncompressed += read;
        }
        deflater.Finish();
      }

      entry.Crc32 = crc.Value;
      entry.UncompressedSize = uncompressed;
      entry.CompressedSize = zip.Position - dataStart;

      if (entry.CompressedSize >= uncompressed && data.CanSeek) {
        // Deflate did not pay off: store the bytes verbatim, as the one-shot path always did.
        data.Position = sourceStart;
        zip.Position = dataStart;
        long stored = 0;
        while ((read = ReadChunk(data, buffer, uncompressed, stored)) > 0) {
          zip.Write(buffer, 0, read);
          stored += read;
        }
        if (stored != uncompressed)
          throw new IOException("The source stream changed while it was being added.");
        entry.CompressionMethod = ZipCompressionMethod.Store;
        entry.CompressedSize = stored;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }

    // Cannot happen for a seekable source read to its announced length: data that does not
    // fit in 32 bits was either reserved for or fell back to Store at that length.
    if (!reserveZip64 && entry.NeedsZip64Sizes)
      throw new InvalidOperationException("Entry sizes outgrew the local header reserved for them.");

    var dataEnd = zip.Position;
    zip.Position = localHeaderOffset;
    WriteLocalHeader(zip, entry, reserveZip64);
    if (zip.Position != dataStart)
      throw new InvalidOperationException("Patched local header changed length.");
    zip.Position = dataEnd;
    return entry;
  }

  private static byte[] ReadTail(Stream zip, long offset) {
    var tail = new byte[checked((int)(zip.Length - offset))];
    zip.Position = offset;
    zip.ReadExactly(tail);
    return tail;
  }

  private const int CopyBufferSize = 1 << 20;

  private static void WriteLocalHeader(Stream zip, ZipEntry entry, bool reserveZip64) {
    using var writer = new BinaryWriter(zip, Encoding.Latin1, leaveOpen: true);
    ZipLocalFileHeader.Write(writer, entry, forceZip64: reserveZip64);
  }

  /// <summary>
  /// Reads the next chunk, never past <paramref name="limit"/> bytes in total when a
  /// limit is given, so a growing source cannot overrun the length it announced.
  /// </summary>
  private static int ReadChunk(Stream source, byte[] buffer, long? limit, long consumed) {
    var want = limit is { } max ? (int)Math.Min(buffer.Length, max - consumed) : buffer.Length;
    if (want <= 0)
      return 0;

    var total = 0;
    while (total < want) {
      var n = source.Read(buffer, total, want - total);
      if (n <= 0)
        break;
      total += n;
    }
    return total;
  }

  /// <summary>Lets a <see cref="BufferedStream"/> be disposed without closing the archive.</summary>
  private sealed class NonClosingStream(Stream inner) : Stream {
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
  }

  /// <summary>
  /// Removes a named entry from a ZIP archive. Returns true if found and
  /// removed. When <paramref name="wipeData"/> is true (default) the orphan
  /// LFH+data bytes are zeroed; otherwise they remain readable in-place but
  /// are no longer referenced by any CD entry.
  /// </summary>
  public static bool RemoveFile(Stream zip, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(zip);
    ArgumentNullException.ThrowIfNull(name);

    // Names match exactly. ZIP names are case-sensitive, and an archive may hold "A.txt" and
    // "a.txt" side by side; matching without regard to case removed both.
    var (records, cdOffset, comment) = ZipRawDirectory.Read(zip);
    var dropped = records.Where(r => r.Name == name).ToList();
    if (dropped.Count == 0) return false;

    // Wipe orphan LFH + payload + data descriptor bytes in place. The LFH has variable length
    // (filename + extra field), so it is re-read to know its full size.
    if (wipeData) {
      foreach (var d in dropped) {
        var lfhLen = ReadLfhLength(zip, d.LocalHeaderOffset);
        if (lfhLen <= 0) continue;
        var descriptor = (d.Flags & ZipConstants.FlagDataDescriptor) != 0 ? (d.HasZip64Sizes ? 24 : 16) : 0;
        var totalToWipe = Math.Min(lfhLen + d.CompressedSize + descriptor, cdOffset - d.LocalHeaderOffset);
        zip.Position = d.LocalHeaderOffset;
        WriteZeros(zip, totalToWipe);
      }
    }

    // Rewrite CD + EOCD at the original CD offset; LFHs of kept entries stay exactly where they
    // were, and their directory records are written back byte for byte.
    zip.Position = cdOffset;
    ZipRawDirectory.Write(zip, [.. records.Except(dropped)], [], comment);
    zip.SetLength(zip.Position);
    return true;
  }

  private static List<ZipEntry> ReadCdEntries(Stream zip, long cdOffset, int count) {
    zip.Position = cdOffset;
    using var reader = new BinaryReader(zip, Encoding.Latin1, leaveOpen: true);
    var entries = new List<ZipEntry>(count);
    for (var i = 0; i < count; i++)
      entries.Add(ZipCentralDirectoryEntry.Read(reader));
    return entries;
  }

  /// <summary>
  /// Reads just enough of a local file header at <paramref name="offset"/> to
  /// know its total length (sig+fixed+filename+extra). Returns -1 if the
  /// header is malformed.
  /// </summary>
  private static int ReadLfhLength(Stream zip, long offset) {
    if (offset < 0 || offset + 30 > zip.Length) return -1;
    zip.Position = offset;
    Span<byte> hdr = stackalloc byte[30];
    var read = 0;
    while (read < hdr.Length) {
      var n = zip.Read(hdr[read..]);
      if (n <= 0) return -1;
      read += n;
    }
    var sig = (uint)(hdr[0] | hdr[1] << 8 | hdr[2] << 16 | hdr[3] << 24);
    if (sig != ZipConstants.LocalFileHeaderSignature) return -1;
    var fileNameLen = (ushort)(hdr[26] | hdr[27] << 8);
    var extraLen = (ushort)(hdr[28] | hdr[29] << 8);
    return 30 + fileNameLen + extraLen;
  }

  private static void WriteZeros(Stream s, long count) {
    var buf = new byte[(int)Math.Min(count, 8192)];
    var remaining = count;
    while (remaining > 0) {
      var chunk = (int)Math.Min(buf.Length, remaining);
      s.Write(buf, 0, chunk);
      remaining -= chunk;
    }
  }
}
