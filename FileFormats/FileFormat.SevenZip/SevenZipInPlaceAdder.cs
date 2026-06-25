using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.SevenZip;

/// <summary>
/// Genuine O(bytes-added) in-place append for 7z archives. New files are
/// compressed into one fresh solid block whose packed stream is written at the
/// byte position the old descriptive header occupied; the existing packed
/// region is never re-read or re-packed, so every previously compressed solid
/// block stays byte-identical at its original file offset. Only the small
/// trailing descriptive header (PackInfo / UnpackInfo / SubStreamsInfo /
/// FilesInfo) and the 32-byte signature header are rewritten.
/// <para>
/// The append is attempted only for archives this writer could itself have
/// produced and for pure additions of new names. The following bail out with
/// <see cref="NotSupportedException"/> so the caller can fall back to the
/// verified rebuild:
/// </para>
/// <list type="bullet">
///   <item>an EncodedHeader (compressed/encrypted metadata) — the trailing
///         header is not a plain <c>kHeader</c> structure;</item>
///   <item><c>PackPos != 0</c> or a gap between the end of the packed data and
///         the descriptive header (the new block must start exactly where the
///         old header began);</item>
///   <item>any existing folder whose packed streams cannot be re-emitted
///         verbatim from PackInfo alone — multi-pack-stream chains such as BCJ2,
///         or AES-encrypted folders;</item>
///   <item>a new file whose name collides with an existing entry (a replace
///         would have to touch an existing solid block).</item>
/// </list>
/// </summary>
public static class SevenZipInPlaceAdder {
  /// <summary>
  /// Attempts a genuine in-place append of <paramref name="newFiles"/> to the 7z
  /// archive in <paramref name="archive"/>. On success the stream contains the
  /// merged archive and its length is trimmed to the new end. Throws
  /// <see cref="NotSupportedException"/> for any case that cannot be served as a
  /// pure byte-additive append (the caller should then rebuild).
  /// </summary>
  /// <param name="archive">A seekable, writable stream holding the 7z archive.</param>
  /// <param name="newFiles">The new files to append (name, bytes, is-directory). Directories
  /// and zero-length entries are appended as empty-stream metadata only.</param>
  /// <param name="codec">The codec to compress the new solid block with.</param>
  /// <param name="dictionarySize">LZMA/LZMA2 dictionary size for the new block.</param>
  public static void Add(Stream archive, IReadOnlyList<(string Name, byte[] Data, bool IsDirectory)> newFiles,
      SevenZipCodec codec = SevenZipCodec.Lzma2, int dictionarySize = 1 << 23) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(newFiles);
    if (!archive.CanSeek || !archive.CanWrite)
      throw new NotSupportedException("In-place 7z add requires a seekable, writable stream.");

    // ── Parse the existing signature + descriptive header ──
    archive.Position = 0;
    var sig = SevenZipHeader.Read(archive);

    // EncodedHeader (compressed/encrypted metadata) cannot be re-emitted as a
    // plain header from the parsed structures without losing fidelity → rebuild.
    var headerStart = SevenZipConstants.SignatureHeaderSize + sig.NextHeaderOffset;
    archive.Position = headerStart;
    var firstId = archive.ReadByte();
    if (firstId != SevenZipConstants.IdHeader)
      throw new NotSupportedException("7z in-place add only supports plain (unencoded) headers.");

    archive.Position = headerStart;
    var headerData = new byte[sig.NextHeaderSize];
    ReadExact(archive, headerData, (int)sig.NextHeaderSize);
    using var headerStream = new MemoryStream(headerData);
    var (packInfo, folders, subStreams, fileInfos) = SevenZipHeaderCodec.ReadHeader(headerStream);

    // ── Verify the archive matches the byte-additive append contract ──
    if (packInfo.PackPos != 0)
      throw new NotSupportedException("7z in-place add requires PackPos == 0.");

    long existingPackedSize = 0;
    foreach (var s in packInfo.PackSizes)
      existingPackedSize += s;
    var existingPackedEnd = SevenZipConstants.SignatureHeaderSize + existingPackedSize;

    // The descriptive header must immediately follow the packed data — otherwise
    // there is unmanaged data we would clobber by writing the new block there.
    if (existingPackedEnd != headerStart)
      throw new NotSupportedException("7z in-place add requires the header to follow the packed data with no gap.");

    foreach (var folder in folders) {
      if (!CanReEmitVerbatim(folder))
        throw new NotSupportedException("7z in-place add cannot re-emit a multi-pack-stream or encrypted folder.");
    }

    // Name-collision check: only pure additions qualify in place.
    var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var f in fileInfos)
      existingNames.Add(f.Name);
    foreach (var (name, _, _) in newFiles) {
      if (existingNames.Contains(name))
        throw new NotSupportedException($"7z in-place add: '{name}' already exists (replace requires rebuild).");
    }

    // ── Split new inputs into a single solid block + empty entries ──
    var newFileEntries = new List<(string Name, byte[] Data)>();
    var newEmptyEntries = new List<(string Name, bool IsDirectory)>();
    foreach (var (name, data, isDir) in newFiles) {
      if (isDir || data.Length == 0)
        newEmptyEntries.Add((name, isDir));
      else
        newFileEntries.Add((name, data));
    }

    var newFileInfos = new List<SevenZipFileInfo>();
    SevenZipFolder? newFolder = null;
    var newPackBytes = System.Array.Empty<byte>();
    long newBlockUnpackTotal = 0;
    var newSubSizes = new List<long>();
    var newSubDigests = new List<uint>();

    if (newFileEntries.Count > 0) {
      // Concatenate the new files into one solid block, mirroring the writer.
      foreach (var (_, data) in newFileEntries)
        newBlockUnpackTotal += data.Length;
      var solid = new byte[newBlockUnpackTotal];
      var off = 0;
      foreach (var (_, data) in newFileEntries) {
        data.AsSpan().CopyTo(solid.AsSpan(off));
        off += data.Length;
      }

      newPackBytes = CompressData(solid, codec, dictionarySize, out var coder);
      newFolder = new SevenZipFolder();
      newFolder.Coders.Add(coder);
      newFolder.UnpackSizes = [newBlockUnpackTotal];
      newFolder.UnpackCrc = Crc32.Compute(solid);

      foreach (var (name, data) in newFileEntries) {
        newFileInfos.Add(new SevenZipFileInfo {
          Name = name, IsDirectory = false, IsEmptyStream = false, IsEmptyFile = false,
        });
        newSubSizes.Add(data.Length);
        newSubDigests.Add(Crc32.Compute(data));
      }
    }

    foreach (var (name, isDir) in newEmptyEntries) {
      newFileInfos.Add(new SevenZipFileInfo {
        Name = name, IsDirectory = isDir, IsEmptyStream = true, IsEmptyFile = !isDir,
      });
    }

    // ── Merge structures: existing first, new block last ──
    var mergedFolders = new List<SevenZipFolder>(folders);
    if (newFolder != null)
      mergedFolders.Add(newFolder);

    var mergedPackSizes = new List<long>(packInfo.PackSizes.Select(s => s));
    if (newFolder != null)
      mergedPackSizes.Add(newPackBytes.Length);

    var mergedPackInfo = new SevenZipPackInfo {
      PackPos = 0,
      PackSizes = [.. mergedPackSizes],
      PackCrcs = new uint?[mergedPackSizes.Count],
    };

    var mergedNumUnpack = new List<int>(subStreams.NumUnpackStreams);
    var mergedSizes = new List<long>(subStreams.UnpackSizes);
    var mergedDigests = new List<uint>(subStreams.Digests);
    if (newFolder != null) {
      mergedNumUnpack.Add(newFileEntries.Count);
      mergedSizes.AddRange(newSubSizes);
      mergedDigests.AddRange(newSubDigests);
    }

    var mergedSubStreams = new SevenZipSubStreamsInfo {
      NumUnpackStreams = [.. mergedNumUnpack],
      UnpackSizes = [.. mergedSizes],
      Digests = [.. mergedDigests],
    };

    // FilesInfo ordering: 7z lists entries with streams first (in folder order),
    // then empty-stream entries. The writer emits its non-empty file infos before
    // the empty ones, so the existing list is [existing-with-stream..., existing-empty...].
    // We splice the new non-empty infos in just after the existing with-stream
    // ones so they map to the freshly-appended last folder, then append all empty
    // entries (existing empties kept in their original relative order, new ones after).
    var existingWithStream = new List<SevenZipFileInfo>();
    var existingEmpty = new List<SevenZipFileInfo>();
    foreach (var f in fileInfos) {
      if (f.IsEmptyStream)
        existingEmpty.Add(f);
      else
        existingWithStream.Add(f);
    }

    var newWithStream = newFileInfos.Where(f => !f.IsEmptyStream).ToList();
    var newEmpty = newFileInfos.Where(f => f.IsEmptyStream).ToList();

    var mergedFileInfos = new List<SevenZipFileInfo>();
    mergedFileInfos.AddRange(existingWithStream);
    mergedFileInfos.AddRange(newWithStream);
    mergedFileInfos.AddRange(existingEmpty);
    mergedFileInfos.AddRange(newEmpty);

    // ── Write the new packed block where the old header started ──
    archive.Position = existingPackedEnd;
    if (newFolder != null)
      archive.Write(newPackBytes, 0, newPackBytes.Length);
    var newHeaderStart = archive.Position;

    // ── Serialize and write the merged descriptive header ──
    using var newHeaderStream = new MemoryStream();
    SevenZipHeaderCodec.WriteHeader(newHeaderStream, mergedPackInfo, mergedFolders,
      mergedSubStreams, mergedFileInfos);
    var newHeader = newHeaderStream.ToArray();
    archive.Write(newHeader, 0, newHeader.Length);

    // ── Rewrite the signature header ──
    archive.Position = 0;
    new SevenZipHeader {
      NextHeaderOffset = newHeaderStart - SevenZipConstants.SignatureHeaderSize,
      NextHeaderSize = newHeader.Length,
      NextHeaderCrc = Crc32.Compute(newHeader),
    }.Write(archive);

    archive.SetLength(newHeaderStart + newHeader.Length);
    archive.Flush();
  }

  /// <summary>
  /// True when a folder's packed streams can be reproduced verbatim from PackInfo
  /// alone — i.e. it is a simple linear coder chain fed by exactly one pack stream
  /// and contains no AES (encryption) coder.
  /// </summary>
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

    // A single unbound input stream means a single pack stream we copy verbatim.
    var numPackStreams = totalIn - folder.BindPairs.Count;
    return numPackStreams == 1;
  }

  private static byte[] CompressData(byte[] data, SevenZipCodec codec, int dictionarySize, out SevenZipCoder coder) {
    switch (codec) {
      case SevenZipCodec.Lzma2: {
        var encoder = new Lzma2Encoder(dictionarySize);
        using var ms = new MemoryStream();
        encoder.Encode(ms, data);
        coder = new SevenZipCoder {
          CodecId = SevenZipConstants.CodecLzma2.ToArray(),
          NumInStreams = 1, NumOutStreams = 1,
          Properties = [encoder.DictionarySizeByte],
        };
        return ms.ToArray();
      }
      case SevenZipCodec.Lzma: {
        var encoder = new LzmaEncoder(dictionarySize);
        using var ms = new MemoryStream();
        encoder.Encode(ms, data, writeEndMarker: true);
        coder = new SevenZipCoder {
          CodecId = SevenZipConstants.CodecLzma.ToArray(),
          NumInStreams = 1, NumOutStreams = 1,
          Properties = encoder.Properties,
        };
        return ms.ToArray();
      }
      default:
        // Copy (store) — no compression.
        coder = new SevenZipCoder {
          CodecId = SevenZipConstants.CodecCopy.ToArray(),
          NumInStreams = 1, NumOutStreams = 1,
        };
        return data;
    }
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
