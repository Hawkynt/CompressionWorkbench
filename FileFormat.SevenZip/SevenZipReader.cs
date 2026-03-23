using Compression.Core.Checksums;
using Compression.Core.Crypto;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.Entropy.Ppmd;
using Compression.Core.Streams;
using Compression.Core.Transforms;

namespace FileFormat.SevenZip;

/// <summary>
/// Reads entries from a 7z archive.
/// </summary>
/// <remarks>
/// 7z archives use solid compression: multiple files are packed into a single
/// compressed block (folder). Extracting a single file may require decompressing
/// the entire folder.
/// </remarks>
public sealed class SevenZipReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly string? _password;
  private readonly List<SevenZipEntry> _entries;
  private readonly SevenZipPackInfo _packInfo;
  private readonly List<SevenZipFolder> _folders;
  private readonly SevenZipSubStreamsInfo _subStreams;
  private readonly List<SevenZipFileInfo> _fileInfos;
  private bool _disposed;

  // Map from entry index to (folderIndex, fileIndexWithinFolder)
  private readonly (int FolderIndex, int FileIndex)[] _entryToFolder;

  /// <summary>
  /// Gets the entries in the 7z archive.
  /// </summary>
  public IReadOnlyList<SevenZipEntry> Entries => this._entries;

  /// <summary>
  /// Initializes a new <see cref="SevenZipReader"/> from a seekable stream.
  /// </summary>
  /// <param name="stream">A seekable stream containing the 7z archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="password">Optional password for encrypted archives.</param>
  public SevenZipReader(Stream stream, bool leaveOpen = false, string? password = null) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;
    this._password = password;
    this._entries = [];

    // Read signature header
    var sigHeader = SevenZipHeader.Read(this._stream);

    // Read next header
    var nextHeaderPos = SevenZipConstants.SignatureHeaderSize + sigHeader.NextHeaderOffset;
    this._stream.Position = nextHeaderPos;

    var nextHeaderData = new byte[sigHeader.NextHeaderSize];
    ReadExact(nextHeaderData, 0, (int)sigHeader.NextHeaderSize);

    // Verify next header CRC
    var nextHeaderCrc = Crc32.Compute(nextHeaderData);
    if (nextHeaderCrc != sigHeader.NextHeaderCrc)
      throw new InvalidDataException("7z next header CRC mismatch.");

    // Parse header
    using var headerStream = new MemoryStream(nextHeaderData);
    (this._packInfo, this._folders, this._subStreams, this._fileInfos) =
      SevenZipHeaderCodec.ReadHeader(headerStream, password, archiveStream: this._stream);

    // Build entries from file infos and sub-stream sizes
    _entryToFolder = BuildEntries();
  }

  /// <summary>
  /// Extracts the decompressed data for an entry by index.
  /// </summary>
  /// <param name="entryIndex">The zero-based index of the entry to extract.</param>
  /// <returns>The decompressed file data.</returns>
  public byte[] Extract(int entryIndex) {
    if (entryIndex < 0 || entryIndex >= this._entries.Count)
      throw new ArgumentOutOfRangeException(nameof(entryIndex));

    var entry = this._entries[entryIndex];
    if (entry.IsDirectory || entry.Size == 0)
      return [];

    var (folderIndex, fileIndex) = _entryToFolder[entryIndex];
    if (folderIndex < 0)
      return [];

    // Decompress the entire folder
    var folderData = DecompressFolder(folderIndex);

    // Extract the file's portion
    var offset = 0L;
    var sizeIndexBase = 0;
    for (var f = 0; f < folderIndex; ++f)
      sizeIndexBase += this._subStreams.NumUnpackStreams[f];

    for (var i = 0; i < fileIndex; ++i)
      offset += this._subStreams.UnpackSizes[sizeIndexBase + i];

    var size = this._subStreams.UnpackSizes[sizeIndexBase + fileIndex];
    var result = new byte[size];
    folderData.AsSpan((int)offset, (int)size).CopyTo(result);

    // Verify CRC if available
    if (entry.Crc.HasValue) {
      var computedCrc = Crc32.Compute(result);
      if (computedCrc != entry.Crc.Value)
        throw new InvalidDataException(
          $"CRC-32 mismatch for '{entry.Name}': expected 0x{entry.Crc.Value:X8}, computed 0x{computedCrc:X8}.");
    }

    return result;
  }

  /// <summary>
  /// Extracts the decompressed data for an entry into a stream.
  /// </summary>
  /// <param name="entryIndex">The zero-based index of the entry to extract.</param>
  /// <param name="output">The stream to write the decompressed data to.</param>
  public void Extract(int entryIndex, Stream output) {
    var data = Extract(entryIndex);
    output.Write(data, 0, data.Length);
  }

  private (int FolderIndex, int FileIndex)[] BuildEntries() {
    var mapping = new (int FolderIndex, int FileIndex)[this._fileInfos.Count];

    // Precompute per-folder: total packed size, total unpacked size, method name
    var folderPackedSize = new long[this._folders.Count];
    var folderUnpackedTotal = new long[this._folders.Count];
    var folderMethod = new string[this._folders.Count];

    var packIdx = 0;
    for (var fi = 0; fi < this._folders.Count; fi++) {
      var folder = this._folders[fi];
      var totalIn = folder.Coders.Sum(c => c.NumInStreams);
      var numPack = totalIn - folder.BindPairs.Count;

      long packed = 0;
      for (var p = 0; p < numPack && packIdx < this._packInfo.PackSizes.Length; p++)
        packed += this._packInfo.PackSizes[packIdx++];
      folderPackedSize[fi] = packed;

      // Total unpacked size for this folder
      var numStreams = fi < this._subStreams.NumUnpackStreams.Length
        ? this._subStreams.NumUnpackStreams[fi] : 1;
      // We'll accumulate while assigning entries below
      folderUnpackedTotal[fi] = 0;

      // Method name from primary coder (last in chain, or first non-filter)
      folderMethod[fi] = GetMethodName(folder);
    }

    // First pass: accumulate unpacked totals per folder
    {
      var curFolder = 0;
      var curFile = 0;
      var ssIdx = 0;
      for (var i = 0; i < this._fileInfos.Count; ++i) {
        if (this._fileInfos[i].IsEmptyStream) continue;
        if (curFolder < this._folders.Count) {
          if (ssIdx < this._subStreams.UnpackSizes.Length)
            folderUnpackedTotal[curFolder] += this._subStreams.UnpackSizes[ssIdx];
          ++ssIdx;
          ++curFile;
          if (curFolder < this._subStreams.NumUnpackStreams.Length &&
              curFile >= this._subStreams.NumUnpackStreams[curFolder]) {
            ++curFolder;
            curFile = 0;
          }
        }
      }
    }

    // Second pass: build entries with proportional compressed sizes
    var currentFolder = 0;
    var currentFileInFolder = 0;
    var subStreamSizeIndex = 0;

    for (var i = 0; i < this._fileInfos.Count; ++i) {
      var fileInfo = this._fileInfos[i];
      var entry = new SevenZipEntry {
        Name = fileInfo.Name,
        IsDirectory = fileInfo.IsDirectory,
        LastWriteTime = fileInfo.LastWriteTime,
        CreationTime = fileInfo.CreationTime,
        Attributes = fileInfo.Attributes,
      };

      if (fileInfo.IsEmptyStream) {
        entry.Size = 0;
        entry.CompressedSize = 0;
        mapping[i] = (-1, -1);
      }
      else {
        if (currentFolder < this._folders.Count) {
          mapping[i] = (currentFolder, currentFileInFolder);

          if (subStreamSizeIndex < this._subStreams.UnpackSizes.Length)
            entry.Size = this._subStreams.UnpackSizes[subStreamSizeIndex];

          if (subStreamSizeIndex < this._subStreams.Digests.Length)
            entry.Crc = this._subStreams.Digests[subStreamSizeIndex];

          // Proportional compressed size
          var totalUnpacked = folderUnpackedTotal[currentFolder];
          entry.CompressedSize = totalUnpacked > 0
            ? (long)(folderPackedSize[currentFolder] * ((double)entry.Size / totalUnpacked))
            : 0;

          entry.Method = folderMethod[currentFolder];

          ++subStreamSizeIndex;
          ++currentFileInFolder;

          if (currentFolder < this._subStreams.NumUnpackStreams.Length &&
            currentFileInFolder >= this._subStreams.NumUnpackStreams[currentFolder]) {
            ++currentFolder;
            currentFileInFolder = 0;
          }
        }
        else
          mapping[i] = (-1, -1);
      }

      this._entries.Add(entry);
    }

    return mapping;
  }

  private static string GetMethodName(SevenZipFolder folder) {
    // Find the primary compression coder (skip filters like BCJ, Delta, AES)
    foreach (var coder in folder.Coders) {
      var id = coder.CodecId;
      if (id.Length == 0) continue;
      if (id.AsSpan().SequenceEqual(SevenZipConstants.CodecLzma2)) return "LZMA2";
      if (id.AsSpan().SequenceEqual(SevenZipConstants.CodecLzma)) return "LZMA";
      if (id.AsSpan().SequenceEqual(SevenZipConstants.CodecDeflate)) return "Deflate";
      if (id.AsSpan().SequenceEqual(SevenZipConstants.CodecBzip2)) return "BZip2";
      if (id.AsSpan().SequenceEqual(SevenZipConstants.CodecPpmd)) return "PPMd";
      if (id.AsSpan().SequenceEqual(SevenZipConstants.CodecCopy)) return "Copy";
    }
    return "7z";
  }

  /// <summary>
  /// Calculates the global pack stream index for a given folder.
  /// Each folder consumes (total input streams - bind pairs) pack streams.
  /// </summary>
  private int GetFirstPackStreamIndex(int folderIndex) {
    var packStreamIndex = 0;
    for (var i = 0; i < folderIndex; ++i) {
      var f = this._folders[i];
      var totalIn = f.Coders.Sum(c => c.NumInStreams);
      packStreamIndex += totalIn - f.BindPairs.Count;
    }
    return packStreamIndex;
  }

  /// <summary>
  /// Reads the pack streams for a folder and returns them as an array.
  /// </summary>
  private byte[][] ReadFolderPackStreams(int folderIndex) {
    var folder = this._folders[folderIndex];
    var totalIn = folder.Coders.Sum(c => c.NumInStreams);
    var numPackStreams = totalIn - folder.BindPairs.Count;

    var firstPackIndex = GetFirstPackStreamIndex(folderIndex);

    // Calculate base offset for the first pack stream
    var baseOffset = SevenZipConstants.SignatureHeaderSize + this._packInfo.PackPos;
    for (var i = 0; i < firstPackIndex; ++i)
      baseOffset += this._packInfo.PackSizes[i];

    var packStreams = new byte[numPackStreams][];
    var offset = baseOffset;
    for (var i = 0; i < numPackStreams; ++i) {
      var size = (int)this._packInfo.PackSizes[firstPackIndex + i];
      this._stream.Position = offset;
      packStreams[i] = new byte[size];
      ReadExact(packStreams[i], 0, size);
      offset += size;
    }

    return packStreams;
  }

  private byte[] DecompressFolder(int folderIndex) {
    var folder = this._folders[folderIndex];
    if (folder.Coders.Count == 0)
      throw new InvalidDataException("Folder has no coders.");

    var packStreams = ReadFolderPackStreams(folderIndex);

    if (folder.Coders.Count == 1) {
      var unpackSize = folder.UnpackSizes.Length > 0 ? folder.UnpackSizes[0] : -1;
      return DecompressSingleCoder(folder.Coders[0], packStreams[0], unpackSize);
    }

    // Check for BCJ2 multi-stream coder
    var bcj2CoderIndex = -1;
    for (var i = 0; i < folder.Coders.Count; ++i) {
      if (folder.Coders[i].CodecId.AsSpan().SequenceEqual(SevenZipConstants.CodecBcj2)) {
        bcj2CoderIndex = i;
        break;
      }
    }

    if (bcj2CoderIndex >= 0)
      return DecompressBcj2Folder(folder, packStreams, bcj2CoderIndex);

    // Simple coder chain: single pack stream, sequential processing
    var data = packStreams[0];
    var coderOrder = BuildCoderOrder(folder);

    foreach (var coderIdx in coderOrder) {
      // Pass unpack size for the last coder in the chain (the final output)
      var unpackSize = coderIdx == coderOrder[^1] && folder.UnpackSizes.Length > 0
        ? folder.UnpackSizes[^1] : -1;
      data = DecompressSingleCoder(folder.Coders[coderIdx], data, unpackSize);
    }

    return data;
  }

  /// <summary>
  /// Decompresses a folder that uses the BCJ2 multi-stream filter.
  /// BCJ2 has 4 input streams: main, call, jump, range (from separate coders).
  /// </summary>
  private byte[] DecompressBcj2Folder(SevenZipFolder folder, byte[][] packStreams,
      int bcj2CoderIndex) {
    var bcj2Coder = folder.Coders[bcj2CoderIndex];

    // BCJ2 has 4 input streams. Each input stream is either:
    // - Bound from another coder's output (via bind pair)
    // - A direct pack stream
    // Build a map: for each coder, determine which pack stream it reads
    // and what its output feeds into.

    // Calculate stream index offsets for each coder
    var coderInStreamBase = new int[folder.Coders.Count];
    var coderOutStreamBase = new int[folder.Coders.Count];
    var inStreamIdx = 0;
    var outStreamIdx = 0;
    for (var i = 0; i < folder.Coders.Count; ++i) {
      coderInStreamBase[i] = inStreamIdx;
      coderOutStreamBase[i] = outStreamIdx;
      inStreamIdx += folder.Coders[i].NumInStreams;
      outStreamIdx += folder.Coders[i].NumOutStreams;
    }

    // Find which input streams are bound (fed by another coder's output)
    var boundInStreams = new HashSet<int>();
    foreach (var (inIdx, _) in folder.BindPairs)
      boundInStreams.Add(inIdx);

    // Map pack streams to unbound input streams
    var packStreamMap = new Dictionary<int, int>(); // inStreamIndex -> packStream index
    var packIdx = 0;
    for (var i = 0; i < inStreamIdx; ++i) {
      if (!boundInStreams.Contains(i))
        packStreamMap[i] = packIdx++;
    }

    // Decompress non-BCJ2 coders first (they produce streams for BCJ2)
    var coderOutputs = new Dictionary<int, byte[]>(); // outStreamIndex -> data
    for (var i = 0; i < folder.Coders.Count; ++i) {
      if (i == bcj2CoderIndex)
        continue;

      // This coder reads from a pack stream
      var coderInStream = coderInStreamBase[i];
      byte[] input;
      if (packStreamMap.TryGetValue(coderInStream, out var psi))
        input = packStreams[psi];
      else {
        // Input is bound from another coder — find which
        var found = false;
        input = [];
        foreach (var (inIndex, outIndex) in folder.BindPairs) {
          if (inIndex == coderInStream && coderOutputs.ContainsKey(outIndex)) {
            input = coderOutputs[outIndex];
            found = true;
            break;
          }
        }
        if (!found)
          throw new InvalidDataException("Could not resolve input for coder.");
      }

      var output = DecompressSingleCoder(folder.Coders[i], input, -1);
      coderOutputs[coderOutStreamBase[i]] = output;
    }

    // Now resolve BCJ2's 4 input streams
    var bcj2InBase = coderInStreamBase[bcj2CoderIndex];
    var bcj2Inputs = new byte[4][];
    for (var i = 0; i < 4; ++i) {
      var streamIdx = bcj2InBase + i;
      if (packStreamMap.TryGetValue(streamIdx, out var psi))
        bcj2Inputs[i] = packStreams[psi];
      else {
        // Find which coder output feeds this input
        foreach (var (inIndex, outIndex) in folder.BindPairs) {
          if (inIndex == streamIdx && coderOutputs.TryGetValue(outIndex, out var data)) {
            bcj2Inputs[i] = data;
            break;
          }
        }
      }
      bcj2Inputs[i] ??= [];
    }

    // BCJ2 streams: 0=main, 1=call, 2=jump, 3=range
    // Output size is the final unpack size of the folder
    var outputSize = (int)folder.UnpackSizes[^1];
    return Bcj2Filter.Decode(bcj2Inputs[0], bcj2Inputs[1], bcj2Inputs[2],
      bcj2Inputs[3], outputSize);
  }

  private static List<int> BuildCoderOrder(SevenZipFolder folder) {
    var numCoders = folder.Coders.Count;

    // Find which coders are fed by other coders (via bind pairs)
    var fedByOtherCoder = new HashSet<int>();
    foreach (var (inIndex, _) in folder.BindPairs)
      fedByOtherCoder.Add(inIndex);

    // The pack stream coder is the one NOT fed by another coder's output
    // For simple chains, this is coder 0
    var order = new List<int>();

    // Start with coder whose input comes from the pack stream
    // (the coder whose in-stream is not a bind pair target)
    var firstCoder = 0;
    for (var i = 0; i < numCoders; ++i) {
      if (!fedByOtherCoder.Contains(i)) {
        firstCoder = i;
        break;
      }
    }

    order.Add(firstCoder);

    // Follow bind pairs: after processing firstCoder, find which coder
    // consumes firstCoder's output
    var processed = new HashSet<int> { firstCoder };
    while (order.Count < numCoders) {
      var found = false;
      var lastProcessed = order[^1];

      // Find bind pair where outIndex matches the output stream of lastProcessed
      foreach (var (inIndex, outIndex) in folder.BindPairs) {
        // outIndex is the output stream index (typically = coder index for simple coders)
        // inIndex is the input stream index consumed by the next coder
        if (outIndex == lastProcessed && !processed.Contains(inIndex)) {
          // For simple 1-in-1-out coders, stream index == coder index
          order.Add(inIndex);
          processed.Add(inIndex);
          found = true;
          break;
        }
      }

      if (!found) {
        // Add remaining coders in order
        for (var i = 0; i < numCoders; ++i) {
          if (!processed.Contains(i)) {
            order.Add(i);
            processed.Add(i);
          }
        }
        break;
      }
    }

    return order;
  }

  private byte[] DecompressSingleCoder(SevenZipCoder coder, byte[] data, long unpackSize = -1) {
    var codecId = coder.CodecId;

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecLzma2)) {
      var dictByte = coder.Properties != null && coder.Properties.Length > 0
        ? coder.Properties[0]
        : (byte)0;
      var dictSize = DecodeDictionarySize(dictByte);
      using var stream = new MemoryStream(data);
      var decoder = new Lzma2Decoder(stream, dictSize);
      return decoder.Decode();
    }

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecLzma)) {
      if (coder.Properties == null || coder.Properties.Length < 5)
        throw new InvalidDataException("LZMA coder missing properties.");
      using var stream = new MemoryStream(data);
      var decoder = new LzmaDecoder(stream, coder.Properties, -1);
      return decoder.Decode();
    }

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecDeflate))
      return Compression.Core.Deflate.DeflateDecompressor.Decompress(data);

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecBzip2)) {
      using var input = new MemoryStream(data);
      using var bz2 = new FileFormat.Bzip2.Bzip2Stream(input,
        CompressionStreamMode.Decompress);
      using var output = new MemoryStream();
      bz2.CopyTo(output);
      return output.ToArray();
    }

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecPpmd)) {
      if (coder.Properties == null || coder.Properties.Length < 5)
        throw new InvalidDataException("PPMd coder missing properties.");
      int order = coder.Properties[0];
      var memSize = coder.Properties[1] | (coder.Properties[2] << 8) |
                    (coder.Properties[3] << 16) | (coder.Properties[4] << 24);
      using var stream = new MemoryStream(data);
      var rangeDecoder = new PpmdRangeDecoder(stream);
      var model = new PpmdModelH(order, memSize);
      if (unpackSize >= 0) {
        var result = new byte[unpackSize];
        for (long i = 0; i < unpackSize; ++i)
          result[i] = model.DecodeSymbol(rangeDecoder);
        return result;
      }
      using var output = new MemoryStream();
      try {
        while (true)
          output.WriteByte(model.DecodeSymbol(rangeDecoder));
      } catch (EndOfStreamException) { }
      return output.ToArray();
    }

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecBcj))
      return BcjFilter.DecodeX86(data);

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecBcjPpc))
      return BcjFilter.DecodePowerPC(data);

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecBcjIa64))
      return BcjFilter.DecodeIA64(data);

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecBcjArm))
      return BcjFilter.DecodeArm(data);

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecBcjArmThumb))
      return BcjFilter.DecodeArmThumb(data);

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecBcjSparc))
      return BcjFilter.DecodeSparc(data);

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecDelta)) {
      var distance = coder.Properties != null && coder.Properties.Length > 0
        ? coder.Properties[0] + 1
        : 1;
      return DeltaFilter.Decode(data, distance);
    }

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecCopy))
      return data;

    if (codecId.AsSpan().SequenceEqual(SevenZipConstants.CodecAes))
      return DecryptAes(coder, data);

    throw new NotSupportedException(
      $"Unsupported 7z codec: [{string.Join(", ", codecId.Select(b => $"0x{b:X2}"))}]");
  }

  private static int DecodeDictionarySize(byte encoded) {
    if (encoded == 0) return 4096;
    var bits = encoded / 2 + 12;
    if ((encoded & 1) == 0) return 1 << bits;
    return 3 << (bits - 1);
  }

  private byte[] DecryptAes(SevenZipCoder coder, byte[] data) {
    if (this._password == null)
      throw new InvalidOperationException("Archive is encrypted but no password was provided.");

    if (coder.Properties == null || coder.Properties.Length < 1)
      throw new InvalidDataException("7z AES coder missing properties.");

    // Parse properties: firstByte, optional sizes byte, salt, IV
    var props = coder.Properties;
    var firstByte = props[0];
    var numCyclesPower = firstByte & 0x3F;
    var hasSalt = (firstByte & 0x40) != 0;
    var hasIv = (firstByte & 0x80) != 0;

    var saltSize = 0;
    var ivSize = 0;
    var pos = 1;

    if (hasSalt || hasIv) {
      if (pos >= props.Length)
        throw new InvalidDataException("7z AES properties truncated.");
      var sizesByte = props[pos++];
      saltSize = hasSalt ? (sizesByte >> 4) + 1 : 0;
      ivSize = hasIv ? (sizesByte & 0x0F) + 1 : 0;
    }

    var salt = new byte[saltSize];
    if (saltSize > 0) {
      if (pos + saltSize > props.Length)
        throw new InvalidDataException("7z AES salt truncated.");
      Array.Copy(props, pos, salt, 0, saltSize);
      pos += saltSize;
    }

    var iv = new byte[16]; // AES IV is always 16 bytes, pad with zeros
    if (ivSize > 0) {
      if (pos + ivSize > props.Length)
        throw new InvalidDataException("7z AES IV truncated.");
      Array.Copy(props, pos, iv, 0, ivSize);
    }

    // Derive key
    var key = KeyDerivation.SevenZipDeriveKey(this._password, salt, numCyclesPower);

    // Decrypt using AES-256-CBC with no padding (7z handles padding itself)
    // Data must be aligned to 16 bytes
    var alignedLen = (data.Length + 15) & ~15;
    if (data.Length < alignedLen) {
      var padded = new byte[alignedLen];
      data.CopyTo(padded, 0);
      data = padded;
    }

    return AesCryptor.DecryptCbcNoPadding(data, key, iv);
  }

  private void ReadExact(byte[] buffer, int offset, int count) {
    var totalRead = 0;
    while (totalRead < count) {
      var read = this._stream.Read(buffer, offset + totalRead, count - totalRead);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of 7z stream.");
      totalRead += read;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
