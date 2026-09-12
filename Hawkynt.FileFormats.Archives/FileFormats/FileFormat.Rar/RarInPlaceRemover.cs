using System.Text;

namespace FileFormat.Rar;

/// <summary>
/// Genuine O(bytes-shifted) in-place removal for RAR5 archives. RAR5 blocks are
/// laid out sequentially, so removing a non-solid FILE block = excising its
/// <c>[header + data]</c> byte range and shifting every following block down to
/// close the gap. Only the bytes after the removed block move; the signature and
/// MAIN header are unchanged, and the base RAR5 layout carries no whole-archive
/// checksum, so nothing earlier needs back-patching. The trailing ENDARC keeps the
/// same fixed encoding, just at its new (lower) offset.
/// <para>
/// The removal bails out with <see cref="NotSupportedException"/> (so the caller
/// can fall back to the verified rebuild) when it cannot be byte-additive:
/// </para>
/// <list type="bullet">
///   <item>an ENCRYPTION block / encrypted file headers — the header chain is not
///         plain-byte editable;</item>
///   <item>a recovery-record (RR) or quick-open (QO) SERVICE block — both
///         checksum/index the whole archive, so a removal invalidates them;</item>
///   <item>no ENDARC block (truncated / streamed archive);</item>
///   <item>the target is part of a <b>solid run</b> — it is itself solid, or the
///         FILE block immediately after it is solid (and would reuse the removed
///         file's dictionary). Removing such a file breaks the solid chain, so the
///         survivors must be recompressed.</item>
///   <item>a RAR4 archive (only RAR5 is edited in place).</item>
/// </list>
/// </summary>
public static class RarInPlaceRemover {

  /// <summary>Quick-open service block name; indexes the whole archive.</summary>
  private const string QuickOpenName = "QO";

  /// <summary>
  /// Attempts a genuine in-place removal of <paramref name="entryNames"/> from the
  /// RAR5 archive in <paramref name="archive"/>. On success the stream contains the
  /// compacted archive and its length is trimmed to the new end. Throws
  /// <see cref="NotSupportedException"/> for any case that cannot be served as a
  /// pure byte-shifting removal (the caller should then rebuild).
  /// </summary>
  /// <param name="archive">A seekable, writable stream holding the RAR5 archive.</param>
  /// <param name="entryNames">The names of the entries to remove (case-insensitive).</param>
  public static void Remove(Stream archive, IReadOnlyCollection<string> entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (!archive.CanSeek || !archive.CanWrite)
      throw new NotSupportedException("In-place RAR5 remove requires a seekable, writable stream.");

    var remove = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
    if (remove.Count == 0)
      return;

    // ── Validate signature ──
    archive.Position = 0;
    var sig = new byte[8];
    ReadExact(archive, sig, 8);
    if (!sig.AsSpan().SequenceEqual(RarConstants.Rar5Signature))
      throw new NotSupportedException("In-place remove is only supported for RAR5 archives.");

    // ── Walk every block, recording extents and (for FILE blocks) name + solid bit ──
    var blocks = new List<BlockSpan>();
    long endArcStart = -1;
    long endArcEnd = -1;

    while (archive.Position < archive.Length) {
      var blockStart = archive.Position;
      RarHeader header;
      try {
        header = RarHeader.Read(archive);
      } catch (EndOfStreamException) {
        break;
      }

      // RarHeader.Read leaves the stream just after the header body; the data
      // area (if any) follows. The block spans [blockStart, dataEnd).
      var dataStart = archive.Position;
      var dataSize = header.HasDataArea ? header.DataSize : 0;
      var blockEnd = dataStart + dataSize;

      switch (header.HeaderType) {
        case RarConstants.HeaderTypeEncryption:
          throw new NotSupportedException(
            "In-place remove not supported: archive uses an encryption header.");

        case RarConstants.HeaderTypeEndArchive:
          endArcStart = blockStart;
          endArcEnd = blockEnd;
          break;

        case RarConstants.HeaderTypeFile: {
          var fileHeader = RarFileHeader.Read(header.RawHeaderData!, header);
          if (fileHeader.IsEncrypted)
            throw new NotSupportedException(
              "In-place remove not supported: archive contains encrypted file headers.");
          blocks.Add(new BlockSpan(blockStart, blockEnd, RarConstants.HeaderTypeFile,
            fileHeader.FileName, fileHeader.IsSolid));
          break;
        }

        case RarConstants.HeaderTypeService: {
          var name = ReadServiceName(header);
          if (name == RarConstants.RecoveryRecordName)
            throw new NotSupportedException(
              "In-place remove not supported: archive has a recovery record (RR) covering the whole archive.");
          if (name == QuickOpenName)
            throw new NotSupportedException(
              "In-place remove not supported: archive has a quick-open (QO) index covering the whole archive.");
          blocks.Add(new BlockSpan(blockStart, blockEnd, RarConstants.HeaderTypeService, null, false));
          break;
        }

        default:
          blocks.Add(new BlockSpan(blockStart, blockEnd, header.HeaderType, null, false));
          break;
      }

      if (endArcStart >= 0)
        break;

      archive.Position = blockEnd;
    }

    if (endArcStart < 0)
      throw new NotSupportedException(
        "In-place remove not supported: archive has no end-of-archive block.");

    // ── Decide which FILE blocks are removable byte-additively ──
    // A removed FILE block must itself be non-solid AND must not be immediately
    // followed by a solid FILE block (whose dictionary chains off it). Otherwise
    // removing it would break a solid run, so the whole op falls back to rebuild.
    var toRemove = new bool[blocks.Count];
    var matchedAny = false;
    for (var i = 0; i < blocks.Count; ++i) {
      var b = blocks[i];
      if (b.Type != RarConstants.HeaderTypeFile || b.Name == null || !remove.Contains(b.Name))
        continue;

      if (b.IsSolid)
        throw new NotSupportedException(
          $"In-place remove not supported: '{b.Name}' is a solid block (part of a solid run).");

      var next = NextFileBlock(blocks, i);
      if (next >= 0 && blocks[next].IsSolid)
        throw new NotSupportedException(
          $"In-place remove not supported: the file after '{b.Name}' is solid and reuses its dictionary.");

      toRemove[i] = true;
      matchedAny = true;
    }

    if (!matchedAny)
      return; // nothing matched — caller's stream is already correct.

    // ── Compact: keep surviving block ranges, then ENDARC, shifting the tail down ──
    // The signature + MAIN header region up to blocks[0].Start never moves; we start
    // compacting from there. Bytes before the first removed block keep their exact
    // offset; the copy moves only the bytes physically after a removed block.
    var dst = blocks[0].Start;
    var buffer = new byte[1 << 16];

    for (var i = 0; i < blocks.Count; ++i) {
      if (toRemove[i])
        continue; // skip — leaves a hole the following keepers close
      var b = blocks[i];
      var len = b.End - b.Start;
      if (b.Start != dst)
        MoveBytes(archive, b.Start, dst, len, buffer);
      dst += len;
    }

    // Move the ENDARC block down to the compacted tail.
    var endArcLen = endArcEnd - endArcStart;
    if (endArcStart != dst)
      MoveBytes(archive, endArcStart, dst, endArcLen, buffer);
    dst += endArcLen;

    archive.SetLength(dst);
    archive.Flush();
  }

  /// <summary>Index of the next FILE block after <paramref name="i"/>, or -1.</summary>
  private static int NextFileBlock(List<BlockSpan> blocks, int i) {
    for (var j = i + 1; j < blocks.Count; ++j) {
      if (blocks[j].Type == RarConstants.HeaderTypeFile)
        return j;
    }
    return -1;
  }

  /// <summary>
  /// Copies <paramref name="count"/> bytes within the same stream from
  /// <paramref name="src"/> to <paramref name="dst"/> (forward; non-overlapping in
  /// the shift direction since dst &lt; src), chunked through a scratch buffer.
  /// </summary>
  private static void MoveBytes(Stream stream, long src, long dst, long count, byte[] buffer) {
    var remaining = count;
    while (remaining > 0) {
      var chunk = (int)Math.Min(buffer.Length, remaining);
      stream.Position = src;
      ReadExact(stream, buffer, chunk);
      stream.Position = dst;
      stream.Write(buffer, 0, chunk);
      src += chunk;
      dst += chunk;
      remaining -= chunk;
    }
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
    if (offset >= data.Length)
      return "";
    var rest = Encoding.UTF8.GetString(data[offset..]);
    if (rest.Contains(RarConstants.RecoveryRecordName, StringComparison.Ordinal))
      return RarConstants.RecoveryRecordName;
    if (rest.Contains(QuickOpenName, StringComparison.Ordinal))
      return QuickOpenName;
    return rest;
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

  /// <summary>A parsed top-level RAR5 block's byte extent and identifying fields.</summary>
  private readonly record struct BlockSpan(long Start, long End, int Type, string? Name, bool IsSolid);
}
