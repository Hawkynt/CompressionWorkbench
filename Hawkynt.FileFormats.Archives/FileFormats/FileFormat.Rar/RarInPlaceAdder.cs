using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Rar;

namespace FileFormat.Rar;

/// <summary>
/// Genuine O(bytes-added) in-place append for RAR5 archives. New files are
/// compressed into fresh, <b>non-solid</b> RAR5 FILE blocks written at the byte
/// offset the old end-of-archive block occupied; a new ENDARC block is written
/// after them. The signature and every pre-existing block
/// (offset 8 .. old-ENDARC-offset) are never re-read or re-packed, so the
/// existing compressed file data stays byte-identical at its original offsets.
/// RAR5 readers scan blocks sequentially and the MAIN header stores no file
/// count or offset table, so an append needs no back-patching of earlier headers.
/// <para>
/// A newly appended file is written non-solid (solid bit clear), which resets
/// the dictionary and lets it decode independently — so it can be appended even
/// after a solid run without touching the existing blocks.
/// </para>
/// <para>
/// The append bails out with <see cref="NotSupportedException"/> (so the caller
/// can fall back to the verified rebuild) when the archive is not byte-additive:
/// </para>
/// <list type="bullet">
///   <item>an ENCRYPTION block / encrypted headers — append cannot extend an
///         encrypted header chain byte-additively;</item>
///   <item>a recovery-record SERVICE block ("RR") or a quick-open SERVICE block
///         ("QO") — both checksum/index the whole archive, so appending invalidates them;</item>
///   <item>no ENDARC block (truncated / streamed archive) — there is no defined
///         insertion point;</item>
///   <item>a new file name that collides with an existing entry (a replace would
///         rewrite an existing block).</item>
/// </list>
/// </summary>
public static class RarInPlaceAdder {

  /// <summary>Quick-open service block name; indexes the whole archive.</summary>
  private const string QuickOpenName = "QO";

  /// <summary>
  /// Attempts a genuine in-place append of <paramref name="newFiles"/> to the
  /// RAR5 archive in <paramref name="archive"/>. On success the stream contains
  /// the merged archive and its length is trimmed to the new end. Throws
  /// <see cref="NotSupportedException"/> for any case that cannot be served as a
  /// pure byte-additive append (the caller should then rebuild).
  /// </summary>
  /// <param name="archive">A seekable, writable stream holding the RAR5 archive.</param>
  /// <param name="newFiles">The new files to append (name, bytes, modification time).
  /// Directory entries are not supported in place and force a rebuild fallback.</param>
  /// <param name="method">Compression method for the new blocks: 0=Store (default),
  /// 1-5=compressed via <see cref="Rar5Encoder"/>.</param>
  /// <param name="dictionarySizeLog">Dictionary size (log2, 17-28) for compressed blocks.</param>
  public static void Add(Stream archive,
      IReadOnlyList<(string Name, byte[] Data, DateTimeOffset? ModifiedTime)> newFiles,
      int method = RarConstants.MethodStore, int dictionarySizeLog = 17) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(newFiles);
    if (!archive.CanSeek || !archive.CanWrite)
      throw new NotSupportedException("In-place RAR5 add requires a seekable, writable stream.");

    // ── Validate signature ──
    archive.Position = 0;
    var sig = new byte[8];
    ReadExact(archive, sig, 8);
    if (!sig.AsSpan().SequenceEqual(RarConstants.Rar5Signature))
      throw new NotSupportedException("In-place add is only supported for RAR5 archives.");

    // ── Walk blocks until ENDARC, recording its offset and existing names ──
    var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    long endArcOffset = -1;

    while (archive.Position < archive.Length) {
      var blockStart = archive.Position;
      RarHeader header;
      try {
        header = RarHeader.Read(archive);
      } catch (EndOfStreamException) {
        break;
      }

      switch (header.HeaderType) {
        case RarConstants.HeaderTypeEncryption:
          throw new NotSupportedException(
            "In-place add not supported: archive uses an encryption header.");

        case RarConstants.HeaderTypeEndArchive:
          endArcOffset = blockStart;
          break;

        case RarConstants.HeaderTypeFile: {
          var fileHeader = RarFileHeader.Read(header.RawHeaderData!, header);
          if (fileHeader.IsEncrypted)
            throw new NotSupportedException(
              "In-place add not supported: archive contains encrypted file headers.");
          existingNames.Add(fileHeader.FileName);
          break;
        }

        case RarConstants.HeaderTypeService: {
          var name = ReadServiceName(header);
          if (name == RarConstants.RecoveryRecordName)
            throw new NotSupportedException(
              "In-place add not supported: archive has a recovery record (RR) covering the whole archive.");
          if (name == QuickOpenName)
            throw new NotSupportedException(
              "In-place add not supported: archive has a quick-open (QO) index covering the whole archive.");
          break;
        }
      }

      if (endArcOffset >= 0)
        break;

      // RarHeader.Read leaves the stream positioned just after the header body;
      // skip past this block's data area to reach the next block.
      if (header.HasDataArea && header.DataSize > 0)
        archive.Position += header.DataSize;
    }

    if (endArcOffset < 0)
      throw new NotSupportedException(
        "In-place add not supported: archive has no end-of-archive block.");

    // ── Name-collision check: only pure additions qualify in place ──
    foreach (var (name, _, _) in newFiles) {
      if (existingNames.Contains(name))
        throw new NotSupportedException(
          $"In-place add: '{name}' already exists (replace requires rebuild).");
    }

    // ── Encode the new FILE blocks ──
    using var blocksMs = new MemoryStream();
    var dictionarySize = 1 << Math.Clamp(dictionarySizeLog, 17, 28);
    foreach (var (name, data, mtime) in newFiles) {
      var block = BuildFileBlock(name, data, mtime, method, dictionarySize, dictionarySizeLog);
      blocksMs.Write(block, 0, block.Length);
    }
    var newBlocks = blocksMs.ToArray();
    var endArc = BuildEndOfArchiveBlock();

    // ── Write the new blocks over the old ENDARC, then a fresh ENDARC ──
    archive.Position = endArcOffset;
    archive.Write(newBlocks, 0, newBlocks.Length);
    archive.Write(endArc, 0, endArc.Length);
    archive.SetLength(endArcOffset + newBlocks.Length + endArc.Length);
    archive.Flush();
  }

  /// <summary>Reads the service-block name out of a parsed SERVICE header body.</summary>
  private static string ReadServiceName(RarHeader header) {
    if (header.RawHeaderData == null)
      return "";
    ReadOnlySpan<byte> data = header.RawHeaderData;
    var offset = 0;
    _ = RarVint.Read(data[offset..], out var consumed); offset += consumed; // type
    _ = RarVint.Read(data[offset..], out consumed); offset += consumed;      // flags
    if (header.HasExtraArea) { _ = RarVint.Read(data[offset..], out consumed); offset += consumed; }
    if (header.HasDataArea) { _ = RarVint.Read(data[offset..], out consumed); offset += consumed; }
    // The service name follows the common fields. SERVICE bodies carry file-style
    // fields (flags/size/...) before the name; the RR/QO names are short ASCII and
    // appear verbatim within the remaining body, so a contains-check is reliable.
    if (offset >= data.Length)
      return "";
    var rest = Encoding.UTF8.GetString(data[offset..]);
    if (rest.Contains(RarConstants.RecoveryRecordName, StringComparison.Ordinal))
      return RarConstants.RecoveryRecordName;
    if (rest.Contains(QuickOpenName, StringComparison.Ordinal))
      return QuickOpenName;
    return rest;
  }

  /// <summary>
  /// Builds a complete non-solid RAR5 FILE block (CRC + size vint + body + data
  /// area), mirroring <c>RarWriter.AddFile</c>'s encoding exactly minus the
  /// encryption / solid paths.
  /// </summary>
  private static byte[] BuildFileBlock(string fileName, byte[] data, DateTimeOffset? modifiedTime,
      int method, int dictionarySize, int dictionarySizeLog) {
    var dataCrc = Crc32.Compute(data);

    byte[] compressed;
    int actualMethod;
    if (method == RarConstants.MethodStore || data.Length == 0) {
      compressed = data;
      actualMethod = RarConstants.MethodStore;
    } else {
      var encoder = new Rar5Encoder(dictionarySize);
      compressed = encoder.Compress(data);
      if (compressed.Length >= data.Length) {
        compressed = data;
        actualMethod = RarConstants.MethodStore;
      } else {
        actualMethod = method;
      }
    }

    // Compression info: solid bit always clear so the block decodes independently.
    var dictLog = Math.Clamp(dictionarySizeLog, 17, 28);
    var compressionInfo = (actualMethod << 7) | ((dictLog - 17) << 10);

    var fileFlags = RarConstants.FileFlagCrc32;
    uint mtime = 0;
    if (modifiedTime != null) {
      fileFlags |= RarConstants.FileFlagTimeMtime;
      mtime = (uint)modifiedTime.Value.ToUnixTimeSeconds();
    }

    var bodyMs = new MemoryStream();
    RarVint.Write(bodyMs, RarConstants.HeaderTypeFile);
    RarVint.Write(bodyMs, RarConstants.HeaderFlagDataArea);
    RarVint.Write(bodyMs, (ulong)compressed.Length); // data area size
    RarVint.Write(bodyMs, (ulong)fileFlags);
    RarVint.Write(bodyMs, (ulong)data.Length);        // unpacked size
    RarVint.Write(bodyMs, 0);                          // attributes

    if ((fileFlags & RarConstants.FileFlagTimeMtime) != 0)
      bodyMs.Write(BitConverter.GetBytes(mtime));
    if ((fileFlags & RarConstants.FileFlagCrc32) != 0)
      bodyMs.Write(BitConverter.GetBytes(dataCrc));

    RarVint.Write(bodyMs, (ulong)compressionInfo);
    RarVint.Write(bodyMs, RarConstants.OsWindows);

    var nameBytes = Encoding.UTF8.GetBytes(fileName);
    RarVint.Write(bodyMs, (ulong)nameBytes.Length);
    bodyMs.Write(nameBytes);

    return AssembleBlock(bodyMs.ToArray(), compressed);
  }

  /// <summary>Builds the trailing ENDARC block (type 5, no next volume).</summary>
  private static byte[] BuildEndOfArchiveBlock() {
    var bodyMs = new MemoryStream();
    RarVint.Write(bodyMs, RarConstants.HeaderTypeEndArchive);
    RarVint.Write(bodyMs, 0UL); // header flags
    RarVint.Write(bodyMs, 0UL); // end-of-archive flags: no next volume
    return AssembleBlock(bodyMs.ToArray(), dataArea: null);
  }

  /// <summary>
  /// Frames a block body as CRC(4 LE) + size(vint) + body, then appends an
  /// optional data area. The CRC-32 covers the size-vint bytes and the body.
  /// </summary>
  private static byte[] AssembleBlock(byte[] body, byte[]? dataArea) {
    var sizeMs = new MemoryStream();
    RarVint.Write(sizeMs, (ulong)body.Length);
    var sizeBytes = sizeMs.ToArray();

    var crcData = new byte[sizeBytes.Length + body.Length];
    sizeBytes.AsSpan().CopyTo(crcData);
    body.AsSpan().CopyTo(crcData.AsSpan(sizeBytes.Length));
    var crc = Crc32.Compute(crcData);

    var dataLen = dataArea?.Length ?? 0;
    var result = new byte[4 + crcData.Length + dataLen];
    BitConverter.GetBytes(crc).CopyTo(result.AsSpan(0));
    crcData.AsSpan().CopyTo(result.AsSpan(4));
    if (dataArea != null)
      dataArea.AsSpan().CopyTo(result.AsSpan(4 + crcData.Length));
    return result;
  }

  private static void ReadExact(Stream stream, byte[] buffer, int count) {
    var total = 0;
    while (total < count) {
      var read = stream.Read(buffer, total, count - total);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of RAR stream.");
      total += read;
    }
  }
}
