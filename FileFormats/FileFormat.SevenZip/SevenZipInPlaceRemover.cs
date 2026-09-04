using Compression.Core.Checksums;

namespace FileFormat.SevenZip;

/// <summary>
/// Genuine O(bytes-shifted) in-place removal for 7z archives. A 7z file lives
/// inside a folder (solid block); removing one is byte-additive only when it
/// removes an <b>entire</b> folder's worth of files. Such a removal drops that
/// folder's packed stream and compacts the packed region by the gap — physically
/// shifting only the packed bytes that follow the removed stream — then rewrites
/// the small trailing descriptive header (PackInfo / UnpackInfo / SubStreamsInfo /
/// FilesInfo minus the removed entries) and the 32-byte signature header. Every
/// surviving folder's packed stream stays byte-identical; the ones before the
/// removed stream keep their exact offsets, the ones after move down by the gap
/// without being re-read or re-packed.
/// <para>
/// Removal of empty-stream entries (directories / empty files) carries no packed
/// data and is always served in place.
/// </para>
/// <para>
/// The removal bails out with <see cref="NotSupportedException"/> (so the caller
/// can fall back to the verified rebuild) when it cannot be byte-additive:
/// </para>
/// <list type="bullet">
///   <item>an EncodedHeader (compressed/encrypted metadata) — the trailing header
///         is not a plain <c>kHeader</c> structure;</item>
///   <item><c>PackPos != 0</c> or a gap between the packed data and the header;</item>
///   <item>any folder whose packed stream cannot be re-emitted verbatim from
///         PackInfo alone — multi-pack-stream chains (BCJ2) or AES folders;</item>
///   <item>a removal that targets a <b>proper subset</b> of a multi-file solid
///         block — the surviving members would have to be recompressed.</item>
/// </list>
/// </summary>
public static class SevenZipInPlaceRemover {

  /// <summary>
  /// Attempts a genuine in-place removal of <paramref name="entryNames"/> from the
  /// 7z archive in <paramref name="archive"/>. On success the stream contains the
  /// compacted archive and its length is trimmed to the new end. Throws
  /// <see cref="NotSupportedException"/> for any case that cannot be served as a
  /// pure byte-shifting removal (the caller should then rebuild).
  /// </summary>
  /// <param name="archive">A seekable, writable stream holding the 7z archive.</param>
  /// <param name="entryNames">The names of the entries to remove (case-insensitive).</param>
  public static void Remove(Stream archive, IReadOnlyCollection<string> entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (!archive.CanSeek || !archive.CanWrite)
      throw new NotSupportedException("In-place 7z remove requires a seekable, writable stream.");

    var remove = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
    if (remove.Count == 0)
      return;

    archive.Position = 0;
    var sig = SevenZipHeader.Read(archive);

    var headerStart = SevenZipConstants.SignatureHeaderSize + sig.NextHeaderOffset;
    archive.Position = headerStart;
    var firstId = archive.ReadByte();
    if (firstId != SevenZipConstants.IdHeader)
      throw new NotSupportedException("7z in-place remove only supports plain (unencoded) headers.");

    archive.Position = headerStart;
    var headerData = new byte[sig.NextHeaderSize];
    ReadExact(archive, headerData, (int)sig.NextHeaderSize);
    using var headerStream = new MemoryStream(headerData);
    var (packInfo, folders, subStreams, fileInfos) = SevenZipHeaderCodec.ReadHeader(headerStream);

    if (packInfo.PackPos != 0)
      throw new NotSupportedException("7z in-place remove requires PackPos == 0.");

    long existingPackedSize = 0;
    foreach (var s in packInfo.PackSizes)
      existingPackedSize += s;
    var existingPackedEnd = SevenZipConstants.SignatureHeaderSize + existingPackedSize;
    if (existingPackedEnd != headerStart)
      throw new NotSupportedException("7z in-place remove requires the header to follow the packed data with no gap.");

    foreach (var folder in folders) {
      if (!CanReEmitVerbatim(folder))
        throw new NotSupportedException("7z in-place remove cannot re-emit a multi-pack-stream or encrypted folder.");
    }

    if (packInfo.PackSizes.Length != folders.Count)
      throw new NotSupportedException("7z in-place remove requires one pack stream per folder.");

    var withStream = new List<SevenZipFileInfo>();
    var emptyEntries = new List<SevenZipFileInfo>();
    foreach (var f in fileInfos) {
      if (f.IsEmptyStream)
        emptyEntries.Add(f);
      else
        withStream.Add(f);
    }

    var folderOfFile = new int[withStream.Count];
    {
      var fileIdx = 0;
      for (var folderIdx = 0; folderIdx < folders.Count; ++folderIdx) {
        var count = folderIdx < subStreams.NumUnpackStreams.Length
          ? subStreams.NumUnpackStreams[folderIdx]
          : 1;
        for (var j = 0; j < count; ++j) {
          if (fileIdx >= withStream.Count)
            throw new NotSupportedException("7z in-place remove: substream count exceeds file count.");
          folderOfFile[fileIdx++] = folderIdx;
        }
      }
      if (fileIdx != withStream.Count)
        throw new NotSupportedException("7z in-place remove: substream/file count mismatch.");
    }

    var removedPerFolder = new int[folders.Count];
    var filesPerFolder = new int[folders.Count];
    var removedAnyWithStream = false;
    for (var i = 0; i < withStream.Count; ++i) {
      ++filesPerFolder[folderOfFile[i]];
      if (remove.Contains(withStream[i].Name)) {
        ++removedPerFolder[folderOfFile[i]];
        removedAnyWithStream = true;
      }
    }

    var removedAnyEmpty = emptyEntries.Any(f => remove.Contains(f.Name));
    if (!removedAnyWithStream && !removedAnyEmpty)
      return;

    var folderRemoved = new bool[folders.Count];
    for (var fi = 0; fi < folders.Count; ++fi) {
      if (removedPerFolder[fi] == 0)
        continue;
      if (removedPerFolder[fi] != filesPerFolder[fi])
        throw new NotSupportedException(
          "7z in-place remove targets a proper subset of a solid block (would recompress survivors).");
      folderRemoved[fi] = true;
    }

    var keptFolders = new List<SevenZipFolder>();
    var keptPackSizes = new List<long>();
    var keptNumUnpack = new List<int>();
    var keptSubSizes = new List<long>();
    var keptDigests = new List<uint>();

    var subIdx = 0;
    for (var fi = 0; fi < folders.Count; ++fi) {
      var n = fi < subStreams.NumUnpackStreams.Length ? subStreams.NumUnpackStreams[fi] : 1;
      if (!folderRemoved[fi]) {
        keptFolders.Add(folders[fi]);
        keptPackSizes.Add(packInfo.PackSizes[fi]);
        keptNumUnpack.Add(n);
        for (var j = 0; j < n; ++j) {
          if (subIdx + j < subStreams.UnpackSizes.Length)
            keptSubSizes.Add(subStreams.UnpackSizes[subIdx + j]);
          if (subIdx + j < subStreams.Digests.Length)
            keptDigests.Add(subStreams.Digests[subIdx + j]);
        }
      }
      subIdx += n;
    }

    var keptWithStream = new List<SevenZipFileInfo>();
    for (var i = 0; i < withStream.Count; ++i) {
      if (!folderRemoved[folderOfFile[i]])
        keptWithStream.Add(withStream[i]);
    }

    var keptEmpty = new List<SevenZipFileInfo>();
    foreach (var f in emptyEntries) {
      if (!remove.Contains(f.Name))
        keptEmpty.Add(f);
    }

    var keptPackInfo = new SevenZipPackInfo {
      PackPos = 0,
      PackSizes = [.. keptPackSizes],
      PackCrcs = new uint?[keptPackSizes.Count],
    };

    var keptSubStreams = new SevenZipSubStreamsInfo {
      NumUnpackStreams = [.. keptNumUnpack],
      UnpackSizes = [.. keptSubSizes],
      Digests = [.. keptDigests],
    };

    var keptFileInfos = new List<SevenZipFileInfo>();
    keptFileInfos.AddRange(keptWithStream);
    keptFileInfos.AddRange(keptEmpty);

    // Complete all metadata serialization before touching packed bytes. After
    // this point the only possible failures are I/O failures, not a format/profile
    // rejection that a caller could safely recover from by rebuilding.
    var keptPackedSize = keptPackSizes.Sum();
    var newPackedEnd = checked(SevenZipConstants.SignatureHeaderSize + keptPackedSize);
    using var newHeaderStream = new MemoryStream();
    SevenZipHeaderCodec.WriteHeader(newHeaderStream, keptPackInfo, keptFolders,
      keptSubStreams, keptFileInfos);
    var newHeader = newHeaderStream.ToArray();

    byte[] signatureHeader;
    using (var signatureStream = new MemoryStream(SevenZipConstants.SignatureHeaderSize)) {
      new SevenZipHeader {
        MajorVersion = sig.MajorVersion,
        MinorVersion = sig.MinorVersion,
        NextHeaderOffset = newPackedEnd - SevenZipConstants.SignatureHeaderSize,
        NextHeaderSize = newHeader.Length,
        NextHeaderCrc = Crc32.Compute(newHeader),
      }.Write(signatureStream);
      signatureHeader = signatureStream.ToArray();
    }

    // ── Compact the packed region in place ──
    var srcOffset = (long)SevenZipConstants.SignatureHeaderSize;
    var dstOffset = (long)SevenZipConstants.SignatureHeaderSize;
    var buffer = new byte[1 << 16];
    for (var fi = 0; fi < folders.Count; ++fi) {
      var size = packInfo.PackSizes[fi];
      if (folderRemoved[fi]) {
        srcOffset += size;
        continue;
      }
      if (srcOffset != dstOffset)
        MoveBytes(archive, srcOffset, dstOffset, size, buffer);
      srcOffset += size;
      dstOffset += size;
    }

    if (dstOffset != newPackedEnd)
      throw new InvalidDataException("7z packed-stream compaction length does not match planned metadata.");

    archive.Position = newPackedEnd;
    archive.Write(newHeader, 0, newHeader.Length);
    archive.Position = 0;
    archive.Write(signatureHeader, 0, signatureHeader.Length);
    archive.SetLength(newPackedEnd + newHeader.Length);
    archive.Flush();
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

  private static bool CanReEmitVerbatim(SevenZipFolder folder) {
    if (folder.Coders.Count == 0)
      return false;

    var totalIn = 0;
    foreach (var c in folder.Coders) {
      totalIn += c.NumInStreams;
      if (c.CodecId.AsSpan().SequenceEqual(SevenZipConstants.CodecAes))
        return false;
      if (c.CodecId.AsSpan().SequenceEqual(SevenZipConstants.CodecBcj2))
        return false;
    }

    var numPackStreams = totalIn - folder.BindPairs.Count;
    return numPackStreams == 1;
  }

  private static void ReadExact(Stream stream, byte[] buffer, int count) {
    var total = 0;
    while (total < count) {
      var read = stream.Read(buffer, total, count - total);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of 7z stream.");
      total += read;
    }
  }
}
