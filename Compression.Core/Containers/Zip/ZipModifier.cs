#pragma warning disable CS1591
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
/// at the default level.</para>
/// </remarks>
public static class ZipModifier {

  /// <summary>
  /// Adds a file to an existing ZIP archive, encoding it with Deflate. If an
  /// entry with the same name already exists the caller should
  /// <see cref="RemoveFile"/> it first; this method just appends.
  /// </summary>
  public static void AddFile(Stream zip, string name, byte[] data, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(zip);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    // Existing entries keep their directory records byte for byte; only the new one is serialised.
    var (records, cdOffset, comment) = ZipRawDirectory.Read(zip);

    // Compress the new file with Deflate (matches the writer's default).
    var crc = Crc32.Compute(data);
    var deflated = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default);
    byte[] payload;
    ZipCompressionMethod method;
    if (deflated.Length < data.Length) {
      payload = deflated;
      method = ZipCompressionMethod.Deflate;
    } else {
      payload = data;
      method = ZipCompressionMethod.Store;
    }

    // Append new local file header + payload at the old CD start position.
    zip.Position = cdOffset;
    var newLocalOffset = cdOffset;

    var newEntry = new ZipEntry {
      FileName = name,
      CompressionMethod = method,
      Crc32 = crc,
      CompressedSize = payload.Length,
      UncompressedSize = data.Length,
      LastModified = lastModified ?? new DateTime(1980, 1, 1),
      LocalHeaderOffset = newLocalOffset,
    };

    using (var lfhWriter = new BinaryWriter(zip, Encoding.Latin1, leaveOpen: true)) {
      ZipLocalFileHeader.Write(lfhWriter, newEntry);
      lfhWriter.Write(payload);
    }

    // Rewrite the central directory + EOCD at the new tail position.
    ZipRawDirectory.Write(zip, records, [newEntry], comment);
    zip.SetLength(zip.Position);
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
