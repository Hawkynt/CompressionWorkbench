using System.Security.Cryptography;
using Compression.Core.Checksums;
using Compression.Core.Crypto;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.Entropy.Ppmd;
using Compression.Core.Streams;
using Compression.Core.Transforms;

namespace FileFormat.SevenZip;

/// <summary>
/// Creates a 7z archive using solid compression with a selectable codec.
/// </summary>
/// <remarks>
/// All file entries are compressed together in a single solid block for
/// maximum compression ratio. Call <c>Finish()</c> for single-block
/// or <c>Finish(maxThreads, maxBlockSize)</c> for parallel multi-block.
/// </remarks>
public sealed class SevenZipWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly int _dictionarySize;
  private readonly SevenZipCodec _codec;
  private readonly SevenZipFilter _filter;
  private readonly int _deltaDistance;
  private readonly int _ppmdOrder;
  private readonly int _ppmdMemorySize;
  private readonly string? _password;
  private readonly bool _encryptHeaders;
  private readonly List<(SevenZipEntry Entry, byte[] Data)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="SevenZipWriter"/>.
  /// </summary>
  /// <param name="stream">A seekable stream to write the archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="dictionarySize">The LZMA/LZMA2 dictionary size in bytes.</param>
  public SevenZipWriter(Stream stream, bool leaveOpen = false, int dictionarySize = 1 << 23)
    : this(stream, SevenZipCodec.Lzma2, leaveOpen, dictionarySize) { }

  /// <summary>
  /// Initializes a new <see cref="SevenZipWriter"/> with an explicit codec.
  /// </summary>
  /// <param name="stream">A seekable stream to write the archive to.</param>
  /// <param name="codec">The compression codec to use.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="dictionarySize">The LZMA/LZMA2 dictionary size in bytes.</param>
  /// <param name="ppmdOrder">PPMd model order (2-32). Default 6.</param>
  /// <param name="ppmdMemorySize">PPMd sub-allocator memory in bytes. Default 16 MB.</param>
  /// <param name="filter">An optional pre-filter to apply before compression.</param>
  /// <param name="deltaDistance">Delta filter distance (1-256). Only used when filter is Delta.</param>
  /// <param name="password">Optional password for AES-256 encryption.</param>
  /// <param name="encryptHeaders">When true and password is set, also encrypt the archive header (file names).</param>
  public SevenZipWriter(Stream stream, SevenZipCodec codec, bool leaveOpen = false,
      int dictionarySize = 1 << 23, int ppmdOrder = 6, int ppmdMemorySize = 1 << 24,
      SevenZipFilter filter = SevenZipFilter.None, int deltaDistance = 1,
      string? password = null, bool encryptHeaders = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    ArgumentOutOfRangeException.ThrowIfLessThan(dictionarySize, 4096);
    ArgumentOutOfRangeException.ThrowIfLessThan(ppmdOrder, 2);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(ppmdOrder, 32);
    ArgumentOutOfRangeException.ThrowIfLessThan(ppmdMemorySize, 1 << 20);
    ArgumentOutOfRangeException.ThrowIfLessThan(deltaDistance, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(deltaDistance, 256);
    this._leaveOpen = leaveOpen;
    this._dictionarySize = dictionarySize;
    this._codec = codec;
    this._filter = filter;
    this._deltaDistance = deltaDistance;
    this._ppmdOrder = ppmdOrder;
    this._ppmdMemorySize = ppmdMemorySize;
    this._password = string.IsNullOrEmpty(password) ? null : password;
    this._encryptHeaders = encryptHeaders && this._password != null;

    // Write placeholder signature header (32 zero bytes)
    var placeholder = new byte[SevenZipConstants.SignatureHeaderSize];
    this._stream.Write(placeholder, 0, placeholder.Length);
  }

  /// <summary>
  /// Adds a file entry from a byte span.
  /// </summary>
  /// <param name="entry">The entry metadata.</param>
  /// <param name="data">The file data.</param>
  public void AddEntry(SevenZipEntry entry, ReadOnlySpan<byte> data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    var dataCopy = data.ToArray();
    entry.Size = dataCopy.Length;
    entry.Crc = Crc32.Compute(dataCopy);
    entry.IsDirectory = false;
    this._entries.Add((entry, dataCopy));
  }

  /// <summary>
  /// Adds a file entry from a stream.
  /// </summary>
  /// <param name="entry">The entry metadata.</param>
  /// <param name="data">The stream containing file data.</param>
  public void AddEntry(SevenZipEntry entry, Stream data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    using var ms = new MemoryStream();
    data.CopyTo(ms);
    AddEntry(entry, ms.ToArray());
  }

  /// <summary>
  /// Adds a directory entry.
  /// </summary>
  /// <param name="name">The directory name.</param>
  public void AddDirectory(string name) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    var entry = new SevenZipEntry {
      Name = name,
      IsDirectory = true,
      Size = 0,
    };
    this._entries.Add((entry, []));
  }

  /// <summary>
  /// Finalizes the archive by compressing all data and writing the header.
  /// All entries are compressed as a single solid block.
  /// </summary>
  public void Finish() => Finish(maxThreads: 1, maxBlockSize: 0);

  /// <summary>
  /// Describes per-block codec/filter overrides for <see cref="FinishWithBlocks"/>.
  /// Entry indices refer to file entries (non-directory, non-empty) in the order they were added.
  /// </summary>
  public sealed class BlockDescriptor {
    /// <summary>Indices of file entries belonging to this block (in add-order among file entries).</summary>
    public required int[] EntryIndices { get; init; }
    /// <summary>Codec override for this block, or null to use the writer's default.</summary>
    public SevenZipCodec? Codec { get; init; }
    /// <summary>Filter override for this block, or null to use the writer's default.</summary>
    public SevenZipFilter? Filter { get; init; }
    /// <summary>Dictionary size override, or null to use the writer's default.</summary>
    public int? DictionarySize { get; init; }
  }

  /// <summary>
  /// Finalizes the archive with per-block codec overrides.
  /// Each block descriptor specifies which entries go into the block and
  /// optionally overrides the codec and filter for that block.
  /// Entries not covered by any descriptor are placed into a default block.
  /// </summary>
  public void FinishWithBlocks(IReadOnlyList<BlockDescriptor> blockDescs, int maxThreads = 1) {
    if (this._finished) return;
    this._finished = true;

    var fileEntries = new List<(SevenZipEntry Entry, byte[] Data)>();
    var emptyEntries = new List<SevenZipEntry>();
    foreach (var (entry, data) in this._entries) {
      if (entry.IsDirectory || data.Length == 0)
        emptyEntries.Add(entry);
      else
        fileEntries.Add((entry, data));
    }

    var fileInfos = new List<SevenZipFileInfo>();
    var compressedDataStart = this._stream.Position;
    var packInfo = new SevenZipPackInfo { PackPos = 0 };
    var folders = new List<SevenZipFolder>();
    var subStreams = new SevenZipSubStreamsInfo();

    if (fileEntries.Count > 0) {
      // Track which entries are claimed by descriptors
      var claimed = new HashSet<int>();
      var orderedBlocks = new List<(List<(SevenZipEntry Entry, byte[] Data)> Block,
        SevenZipCodec Codec, SevenZipFilter Filter, int DictSize)>();

      foreach (var desc in blockDescs) {
        var block = new List<(SevenZipEntry Entry, byte[] Data)>();
        foreach (var idx in desc.EntryIndices) {
          if (idx >= 0 && idx < fileEntries.Count && claimed.Add(idx))
            block.Add(fileEntries[idx]);
        }
        if (block.Count > 0) {
          orderedBlocks.Add((block,
            desc.Codec ?? this._codec,
            desc.Filter ?? this._filter,
            desc.DictionarySize ?? this._dictionarySize));
        }
      }

      // Unclaimed entries go into a default block
      var unclaimed = new List<(SevenZipEntry Entry, byte[] Data)>();
      for (var i = 0; i < fileEntries.Count; i++)
        if (!claimed.Contains(i))
          unclaimed.Add(fileEntries[i]);
      if (unclaimed.Count > 0)
        orderedBlocks.Add((unclaimed, this._codec, this._filter, this._dictionarySize));

      // Add file infos in block order
      foreach (var (block, _, _, _) in orderedBlocks)
        foreach (var (entry, _) in block)
          fileInfos.Add(MakeFileInfo(entry));

      // Compress each block with its own codec
      var packSizes = new List<long>();
      var packCrcs = new List<uint?>();
      var numUnpack = new List<int>();
      var unpackSizes = new List<long>();
      var digests = new List<uint>();

      foreach (var (block, codec, filter, dictSize) in orderedBlocks) {
        CompressBlockWithCodec(block, codec, filter, dictSize,
          folders, packSizes, packCrcs, numUnpack, unpackSizes, digests);
      }

      packInfo.PackSizes = packSizes.ToArray();
      packInfo.PackCrcs = packCrcs.ToArray();
      subStreams.NumUnpackStreams = numUnpack.ToArray();
      subStreams.UnpackSizes = unpackSizes.ToArray();
      subStreams.Digests = digests.ToArray();
    }
    else {
      packInfo.PackSizes = [];
      packInfo.PackCrcs = [];
      subStreams.NumUnpackStreams = [];
      subStreams.UnpackSizes = [];
      subStreams.Digests = [];
    }

    foreach (var entry in emptyEntries)
      fileInfos.Add(MakeFileInfo(entry));

    FinalizeHeader(packInfo, folders, subStreams, fileInfos);
  }

  /// <summary>
  /// Compresses a block with a specific codec/filter (for per-block codec support).
  /// </summary>
  private void CompressBlockWithCodec(
      List<(SevenZipEntry Entry, byte[] Data)> block,
      SevenZipCodec codec, SevenZipFilter filter, int dictSize,
      List<SevenZipFolder> folders, List<long> packSizes, List<uint?> packCrcs,
      List<int> numUnpack, List<long> unpackSizes, List<uint> digests) {

    var (solidData, totalSize) = ConcatenateBlock(block);

    var dataToCompress = solidData;
    SevenZipCoder? filterCoder = null;
    if (filter != SevenZipFilter.None && filter != SevenZipFilter.Bcj2)
      dataToCompress = ApplyFilter(solidData, filter, out filterCoder);

    var compressedData = CompressDataWithCodec(dataToCompress, codec, dictSize, out var compressionCoder);

    SevenZipCoder? aesCoder = null;
    var compressedSize = compressedData.Length;
    if (this._password != null)
      compressedData = EncryptAes(compressedData, out aesCoder);

    this._stream.Write(compressedData, 0, compressedData.Length);
    packSizes.Add(compressedData.Length);
    packCrcs.Add(null);

    var folder = BuildFolder(compressionCoder, filterCoder, aesCoder,
      compressedSize, dataToCompress.Length, totalSize);
    folder.UnpackCrc = Crc32.Compute(solidData);
    folders.Add(folder);

    numUnpack.Add(block.Count);
    foreach (var (entry, data) in block) {
      unpackSizes.Add(data.Length);
      digests.Add(entry.Crc ?? Crc32.Compute(data));
    }
  }

  /// <summary>
  /// Compresses data with a specific codec (not necessarily the writer's default).
  /// </summary>
  private byte[] CompressDataWithCodec(byte[] data, SevenZipCodec codec, int dictSize, out SevenZipCoder coder) {
    switch (codec) {
      case SevenZipCodec.Lzma2: {
        var encoder = new Lzma2Encoder(dictSize);
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
        var encoder = new LzmaEncoder(dictSize);
        using var ms = new MemoryStream();
        encoder.Encode(ms, data, writeEndMarker: true);
        coder = new SevenZipCoder {
          CodecId = SevenZipConstants.CodecLzma.ToArray(),
          NumInStreams = 1, NumOutStreams = 1,
          Properties = encoder.Properties,
        };
        return ms.ToArray();
      }
      case SevenZipCodec.Deflate:
        return CompressDeflate(data, out coder);
      case SevenZipCodec.BZip2:
        return CompressBzip2(data, out coder);
      case SevenZipCodec.PPMd:
        return CompressPpmd(data, out coder);
      default: {
        // Copy (store) — no compression
        coder = new SevenZipCoder {
          CodecId = SevenZipConstants.CodecCopy.ToArray(),
          NumInStreams = 1, NumOutStreams = 1,
        };
        return data;
      }
    }
  }

  /// <summary>
  /// Finalizes the archive with parallel multi-block compression.
  /// Entries are split into solid blocks of at most <paramref name="maxBlockSize"/> bytes,
  /// and each block is compressed on a separate thread.
  /// </summary>
  /// <param name="maxThreads">Maximum number of parallel compression threads. 1 = single-threaded.</param>
  /// <param name="maxBlockSize">Maximum uncompressed size per solid block in bytes.
  /// 0 = single block (all entries together).</param>
  public void Finish(int maxThreads = 1, long maxBlockSize = 0) {
    if (this._finished)
      return;
    this._finished = true;

    // Separate file entries (with data) from empty entries (directories/empty files)
    var fileEntries = new List<(SevenZipEntry Entry, byte[] Data)>();
    var emptyEntries = new List<SevenZipEntry>();

    foreach (var (entry, data) in this._entries) {
      if (entry.IsDirectory || data.Length == 0)
        emptyEntries.Add(entry);
      else
        fileEntries.Add((entry, data));
    }

    // Build file info list: file entries first (grouped by block), then empty/directory entries
    var fileInfos = new List<SevenZipFileInfo>();

    var compressedDataStart = this._stream.Position;
    var packInfo = new SevenZipPackInfo { PackPos = 0 };
    var folders = new List<SevenZipFolder>();
    var subStreams = new SevenZipSubStreamsInfo();

    if (fileEntries.Count > 0) {
      // Split into blocks if multi-block is requested
      var blocks = SplitIntoBlocks(fileEntries, maxBlockSize);

      // Add file infos in block order (files within each block, then across blocks)
      foreach (var block in blocks)
        foreach (var (entry, _) in block)
          fileInfos.Add(MakeFileInfo(entry));

      if (blocks.Count == 1 || maxThreads <= 1) {
        // Single-threaded path (original behavior or single block)
        var packSizes = new List<long>();
        var packCrcs = new List<uint?>();
        var numUnpack = new List<int>();
        var unpackSizes = new List<long>();
        var digests = new List<uint>();

        foreach (var block in blocks) {
          CompressAndWriteBlock(block, folders, packSizes, packCrcs,
            numUnpack, unpackSizes, digests);
        }

        packInfo.PackSizes = packSizes.ToArray();
        packInfo.PackCrcs = packCrcs.ToArray();
        subStreams.NumUnpackStreams = numUnpack.ToArray();
        subStreams.UnpackSizes = unpackSizes.ToArray();
        subStreams.Digests = digests.ToArray();
      }
      else {
        // Multi-threaded: compress blocks in parallel, then write sequentially
        var results = new (byte[] Compressed, SevenZipCoder Coder,
          long TotalUnpackSize, long[] FileSizes, uint[] FileCrcs)[blocks.Count];

        var options = new ParallelOptions { MaxDegreeOfParallelism = maxThreads };
        Parallel.For(0, blocks.Count, options, i => {
          results[i] = CompressBlockParallel(blocks[i]);
        });

        // Write results sequentially
        var packSizes = new List<long>();
        var packCrcs = new List<uint?>();
        var numUnpack = new List<int>();
        var unpackSizes = new List<long>();
        var digests = new List<uint>();

        foreach (var (compressed, coder, totalUnpackSize, fileSizes, fileCrcs) in results) {
          this._stream.Write(compressed);
          packSizes.Add(compressed.Length);
          packCrcs.Add(null);

          var folder = new SevenZipFolder();
          folder.Coders.Add(coder);
          folder.UnpackSizes = [totalUnpackSize];
          folders.Add(folder);

          numUnpack.Add(fileSizes.Length);
          unpackSizes.AddRange(fileSizes.Select(s => (long)s));
          digests.AddRange(fileCrcs);
        }

        packInfo.PackSizes = packSizes.ToArray();
        packInfo.PackCrcs = packCrcs.ToArray();
        subStreams.NumUnpackStreams = numUnpack.ToArray();
        subStreams.UnpackSizes = unpackSizes.ToArray();
        subStreams.Digests = digests.ToArray();
      }
    }
    else {
      packInfo.PackSizes = [];
      packInfo.PackCrcs = [];
      subStreams.NumUnpackStreams = [];
      subStreams.UnpackSizes = [];
      subStreams.Digests = [];
    }

    // Add empty/directory entries at the end of file info list
    foreach (var entry in emptyEntries)
      fileInfos.Add(MakeFileInfo(entry));

    FinalizeHeader(packInfo, folders, subStreams, fileInfos);
  }

  private void FinalizeHeader(SevenZipPackInfo packInfo, List<SevenZipFolder> folders,
      SevenZipSubStreamsInfo subStreams, List<SevenZipFileInfo> fileInfos) {
    // Serialize the real header
    using var headerStream = new MemoryStream();
    SevenZipHeaderCodec.WriteHeader(headerStream, packInfo, folders, subStreams, fileInfos);
    var headerData = headerStream.ToArray();

    if (this._encryptHeaders) {
      // Compress the header with LZMA2
      var lzma2Enc = new Lzma2Encoder(1 << 20); // 1MB dict for header
      using var compStream = new MemoryStream();
      lzma2Enc.Encode(compStream, headerData);
      var compressedHeader = compStream.ToArray();
      var lzma2Coder = new SevenZipCoder {
        CodecId = SevenZipConstants.CodecLzma2.ToArray(),
        NumInStreams = 1, NumOutStreams = 1,
        Properties = [lzma2Enc.DictionarySizeByte],
      };

      // Encrypt with AES
      var encrypted = EncryptAes(compressedHeader, out var aesCoder);

      // Write encrypted header data
      var packedDataPos = this._stream.Position - SevenZipConstants.SignatureHeaderSize;
      this._stream.Write(encrypted, 0, encrypted.Length);

      // Build folder: LZMA2 → AES
      var headerFolder = new SevenZipFolder();
      headerFolder.Coders.Add(lzma2Coder);
      headerFolder.Coders.Add(aesCoder);
      headerFolder.BindPairs.Add((0, 1));
      headerFolder.UnpackSizes = [compressedHeader.Length, headerData.Length];

      // Build the EncodedHeader descriptor
      var encPackInfo = new SevenZipPackInfo {
        PackPos = packedDataPos,
        PackSizes = [encrypted.Length],
        PackCrcs = [null],
      };

      // Write EncodedHeader
      var encodedHeaderOffset = this._stream.Position - SevenZipConstants.SignatureHeaderSize;
      using var ehStream = new MemoryStream();
      SevenZipHeaderCodec.WriteEncodedHeader(ehStream, encPackInfo, [headerFolder]);
      var ehData = ehStream.ToArray();
      this._stream.Write(ehData, 0, ehData.Length);

      // Update signature header
      var ehCrc = Crc32.Compute(ehData);
      this._stream.Position = 0;
      new SevenZipHeader {
        NextHeaderOffset = encodedHeaderOffset,
        NextHeaderSize = ehData.Length,
        NextHeaderCrc = ehCrc,
      }.Write(this._stream);
    }
    else {
      // Plain header
      var headerOffset = this._stream.Position - SevenZipConstants.SignatureHeaderSize;
      this._stream.Write(headerData, 0, headerData.Length);
      var headerCrc = Crc32.Compute(headerData);
      this._stream.Position = 0;
      new SevenZipHeader {
        NextHeaderOffset = headerOffset,
        NextHeaderSize = headerData.Length,
        NextHeaderCrc = headerCrc,
      }.Write(this._stream);
    }
    this._stream.Seek(0, SeekOrigin.End);
  }

  private static SevenZipFileInfo MakeFileInfo(SevenZipEntry entry) => new() {
    Name = entry.Name,
    IsDirectory = entry.IsDirectory,
    IsEmptyStream = entry.IsDirectory || entry.Size == 0,
    IsEmptyFile = !entry.IsDirectory && entry.Size == 0,
    LastWriteTime = entry.LastWriteTime,
    CreationTime = entry.CreationTime,
    Attributes = entry.Attributes,
  };

  /// <summary>
  /// Splits file entries into solid blocks, each not exceeding maxBlockSize.
  /// If maxBlockSize is 0, all entries go into a single block.
  /// </summary>
  private static List<List<(SevenZipEntry Entry, byte[] Data)>> SplitIntoBlocks(
      List<(SevenZipEntry Entry, byte[] Data)> entries, long maxBlockSize) {
    if (maxBlockSize <= 0)
      return [entries];

    var blocks = new List<List<(SevenZipEntry Entry, byte[] Data)>>();
    var current = new List<(SevenZipEntry Entry, byte[] Data)>();
    long currentSize = 0;

    foreach (var entry in entries) {
      if (current.Count > 0 && currentSize + entry.Data.Length > maxBlockSize) {
        blocks.Add(current);
        current = [];
        currentSize = 0;
      }
      current.Add(entry);
      currentSize += entry.Data.Length;
    }
    if (current.Count > 0)
      blocks.Add(current);

    return blocks;
  }

  /// <summary>
  /// Compresses one block and writes it to the output stream (single-threaded path).
  /// </summary>
  private void CompressAndWriteBlock(List<(SevenZipEntry Entry, byte[] Data)> block,
      List<SevenZipFolder> folders, List<long> packSizes, List<uint?> packCrcs,
      List<int> numUnpack, List<long> unpackSizes, List<uint> digests) {

    var (solidData, totalSize) = ConcatenateBlock(block);
    SevenZipFolder folder;

    if (this._filter == SevenZipFilter.Bcj2) {
      folder = WriteBcj2Folder(solidData, totalSize, packSizes, packCrcs);
    }
    else {
      var dataToCompress = solidData;
      SevenZipCoder? filterCoder = null;
      if (this._filter != SevenZipFilter.None)
        dataToCompress = ApplyFilter(solidData, out filterCoder);

      var compressedData = CompressData(dataToCompress, out var compressionCoder);

      SevenZipCoder? aesCoder = null;
      var compressedSize = compressedData.Length;
      if (this._password != null)
        compressedData = EncryptAes(compressedData, out aesCoder);

      this._stream.Write(compressedData, 0, compressedData.Length);
      packSizes.Add(compressedData.Length);
      packCrcs.Add(null);

      folder = BuildFolder(compressionCoder, filterCoder, aesCoder,
        compressedSize, dataToCompress.Length, totalSize);
    }

    folder.UnpackCrc = Crc32.Compute(solidData);
    folders.Add(folder);

    numUnpack.Add(block.Count);
    foreach (var (entry, data) in block) {
      unpackSizes.Add(data.Length);
      digests.Add(entry.Crc ?? Crc32.Compute(data));
    }
  }

  /// <summary>
  /// Compresses one block entirely in memory (thread-safe, for parallel path).
  /// </summary>
  private (byte[] Compressed, SevenZipCoder Coder, long TotalUnpackSize,
      long[] FileSizes, uint[] FileCrcs)
      CompressBlockParallel(List<(SevenZipEntry Entry, byte[] Data)> block) {

    var (solidData, totalSize) = ConcatenateBlock(block);
    var compressed = CompressData(solidData, out var coder);

    var fileSizes = new long[block.Count];
    var fileCrcs = new uint[block.Count];
    for (var i = 0; i < block.Count; i++) {
      fileSizes[i] = block[i].Data.Length;
      fileCrcs[i] = block[i].Entry.Crc ?? Crc32.Compute(block[i].Data);
    }

    return (compressed, coder, totalSize, fileSizes, fileCrcs);
  }

  private static (byte[] SolidData, int TotalSize) ConcatenateBlock(
      List<(SevenZipEntry Entry, byte[] Data)> block) {
    var totalSize = 0;
    foreach (var (_, data) in block)
      totalSize += data.Length;

    var solidData = new byte[totalSize];
    var offset = 0;
    foreach (var (_, data) in block) {
      data.AsSpan().CopyTo(solidData.AsSpan(offset));
      offset += data.Length;
    }
    return (solidData, totalSize);
  }

  private static SevenZipFolder BuildFolder(SevenZipCoder compressionCoder,
      SevenZipCoder? filterCoder, SevenZipCoder? aesCoder,
      int compressedSize, int filteredSize, int totalSize) {
    var folder = new SevenZipFolder();
    if (aesCoder != null && filterCoder != null) {
      folder.Coders.Add(compressionCoder);
      folder.Coders.Add(filterCoder);
      folder.Coders.Add(aesCoder);
      folder.BindPairs.Add((1, 0));
      folder.BindPairs.Add((0, 2));
      folder.UnpackSizes = [compressedSize, filteredSize, totalSize];
    }
    else if (aesCoder != null) {
      folder.Coders.Add(compressionCoder);
      folder.Coders.Add(aesCoder);
      folder.BindPairs.Add((0, 1));
      folder.UnpackSizes = [compressedSize, totalSize];
    }
    else if (filterCoder != null) {
      folder.Coders.Add(compressionCoder);
      folder.Coders.Add(filterCoder);
      folder.BindPairs.Add((1, 0));
      folder.UnpackSizes = [filteredSize, totalSize];
    }
    else {
      folder.Coders.Add(compressionCoder);
      folder.UnpackSizes = [totalSize];
    }
    return folder;
  }

  /// <summary>
  /// Writes a BCJ2 folder with 4 pack streams: compressed main + raw call/jump/range.
  /// </summary>
  private SevenZipFolder WriteBcj2Folder(byte[] solidData, int totalSize,
      List<long> packSizes, List<uint?> packCrcs) {
    // Encode BCJ2: split into 4 sub-streams
    var (main, call, jump, range) = Bcj2Filter.Encode(solidData);

    // Compress the main stream with the selected codec
    var compressedMain = CompressData(main, out var compressionCoder);

    // Write all 4 pack streams: compressed main, call, jump, range
    this._stream.Write(compressedMain, 0, compressedMain.Length);
    this._stream.Write(call, 0, call.Length);
    this._stream.Write(jump, 0, jump.Length);
    this._stream.Write(range, 0, range.Length);

    packSizes.Add(compressedMain.Length);
    packSizes.Add(call.Length);
    packSizes.Add(jump.Length);
    packSizes.Add(range.Length);
    packCrcs.AddRange([null, null, null, null]);

    var bcj2Coder = new SevenZipCoder {
      CodecId = SevenZipConstants.CodecBcj2.ToArray(),
      NumInStreams = 4,
      NumOutStreams = 1,
    };

    var folder = new SevenZipFolder();
    folder.Coders.Add(compressionCoder);
    folder.Coders.Add(bcj2Coder);
    folder.BindPairs.Add((1, 0));
    folder.UnpackSizes = [main.Length, totalSize];

    return folder;
  }

  /// <summary>
  /// Applies the selected pre-filter to the data and returns the filtered bytes
  /// along with a coder descriptor for the filter.
  /// </summary>
  private byte[] ApplyFilter(byte[] data, out SevenZipCoder coder)
    => ApplyFilter(data, this._filter, out coder);

  private byte[] ApplyFilter(byte[] data, SevenZipFilter filter, out SevenZipCoder coder) {
    switch (filter) {
      case SevenZipFilter.Copy:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecCopy.ToArray() };
        return data;
      case SevenZipFilter.BcjX86:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecBcj.ToArray() };
        return BcjFilter.EncodeX86(data);
      case SevenZipFilter.BcjArm:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecBcjArm.ToArray() };
        return BcjFilter.EncodeArm(data);
      case SevenZipFilter.BcjArmThumb:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecBcjArmThumb.ToArray() };
        return BcjFilter.EncodeArmThumb(data);
      case SevenZipFilter.BcjPowerPC:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecBcjPpc.ToArray() };
        return BcjFilter.EncodePowerPC(data);
      case SevenZipFilter.BcjSparc:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecBcjSparc.ToArray() };
        return BcjFilter.EncodeSparc(data);
      case SevenZipFilter.BcjIA64:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecBcjIa64.ToArray() };
        return BcjFilter.EncodeIA64(data);
      case SevenZipFilter.Delta:
        coder = new SevenZipCoder {
          CodecId = SevenZipConstants.CodecDelta.ToArray(),
          Properties = [(byte)(this._deltaDistance - 1)],
        };
        return DeltaFilter.Encode(data, this._deltaDistance);
      default:
        throw new NotSupportedException($"Unsupported filter: {this._filter}");
    }
  }

  /// <summary>
  /// Compresses the solid data block using the selected codec and returns the
  /// compressed bytes along with the coder descriptor for the folder header.
  /// </summary>
  private byte[] CompressData(byte[] solidData, out SevenZipCoder coder) =>
    this._codec switch {
      SevenZipCodec.Lzma2 => CompressLzma2(solidData, out coder),
      SevenZipCodec.Lzma => CompressLzma(solidData, out coder),
      SevenZipCodec.Deflate => CompressDeflate(solidData, out coder),
      SevenZipCodec.BZip2 => CompressBzip2(solidData, out coder),
      SevenZipCodec.PPMd => CompressPpmd(solidData, out coder),
      _ => throw new NotSupportedException($"Unsupported codec: {this._codec}"),
    };

  private byte[] CompressLzma2(byte[] data, out SevenZipCoder coder) {
    var encoder = new Lzma2Encoder(this._dictionarySize);
    using var compressedStream = new MemoryStream();
    encoder.Encode(compressedStream, data);
    coder = new SevenZipCoder {
      CodecId = SevenZipConstants.CodecLzma2.ToArray(),
      NumInStreams = 1,
      NumOutStreams = 1,
      Properties = [encoder.DictionarySizeByte],
    };
    return compressedStream.ToArray();
  }

  private byte[] CompressLzma(byte[] data, out SevenZipCoder coder) {
    var encoder = new LzmaEncoder(this._dictionarySize);
    using var compressedStream = new MemoryStream();
    encoder.Encode(compressedStream, data, writeEndMarker: true);
    coder = new SevenZipCoder {
      CodecId = SevenZipConstants.CodecLzma.ToArray(),
      NumInStreams = 1,
      NumOutStreams = 1,
      Properties = encoder.Properties,
    };
    return compressedStream.ToArray();
  }

  private static byte[] CompressDeflate(byte[] data, out SevenZipCoder coder) {
    var compressed = DeflateCompressor.Compress(data);
    coder = new SevenZipCoder {
      CodecId = SevenZipConstants.CodecDeflate.ToArray(),
      NumInStreams = 1,
      NumOutStreams = 1,
    };
    return compressed;
  }

  private static byte[] CompressBzip2(byte[] data, out SevenZipCoder coder) {
    using var compressedStream = new MemoryStream();
    using (var bz2 = new FileFormat.Bzip2.Bzip2Stream(compressedStream,
        CompressionStreamMode.Compress, leaveOpen: true)) {
      bz2.Write(data, 0, data.Length);
    }
    coder = new SevenZipCoder {
      CodecId = SevenZipConstants.CodecBzip2.ToArray(),
      NumInStreams = 1,
      NumOutStreams = 1,
    };
    return compressedStream.ToArray();
  }

  private byte[] CompressPpmd(byte[] data, out SevenZipCoder coder) {
    using var compressedStream = new MemoryStream();
    var rangeEncoder = new PpmdRangeEncoder(compressedStream);
    var model = new PpmdModelH(this._ppmdOrder, this._ppmdMemorySize);
    foreach (var b in data)
      model.EncodeSymbol(rangeEncoder, b);
    rangeEncoder.Finish();

    // PPMd properties: 1 byte order + 4 bytes memory size (little-endian)
    var props = new byte[5];
    props[0] = (byte)this._ppmdOrder;
    props[1] = (byte)this._ppmdMemorySize;
    props[2] = (byte)(this._ppmdMemorySize >> 8);
    props[3] = (byte)(this._ppmdMemorySize >> 16);
    props[4] = (byte)(this._ppmdMemorySize >> 24);

    coder = new SevenZipCoder {
      CodecId = SevenZipConstants.CodecPpmd.ToArray(),
      NumInStreams = 1,
      NumOutStreams = 1,
      Properties = props,
    };
    return compressedStream.ToArray();
  }

  private byte[] EncryptAes(byte[] data, out SevenZipCoder coder) {
    const int numCyclesPower = 19; // 2^19 = 524288 iterations

    // Generate random salt and IV
    var salt = new byte[8];
    var iv = new byte[8];
    using (var rng = RandomNumberGenerator.Create()) {
      rng.GetBytes(salt);
      rng.GetBytes(iv);
    }

    // Derive key
    var key = KeyDerivation.SevenZipDeriveKey(this._password!, salt, numCyclesPower);

    // Pad data to 16-byte boundary
    var paddedLen = (data.Length + 15) & ~15;
    if (paddedLen > data.Length) {
      var padded = new byte[paddedLen];
      data.AsSpan().CopyTo(padded);
      data = padded;
    }

    // Encrypt
    var fullIv = new byte[16];
    iv.CopyTo(fullIv, 0);
    var encrypted = AesCryptor.EncryptCbcNoPadding(data, key, fullIv);

    // Build properties: firstByte | sizesByte | salt | iv
    var firstByte = (byte)(numCyclesPower | 0x40 | 0x80); // hasSalt | hasIV
    var sizesByte = (byte)(((salt.Length - 1) << 4) | (iv.Length - 1));
    var props = new byte[2 + salt.Length + iv.Length];
    props[0] = firstByte;
    props[1] = sizesByte;
    salt.CopyTo(props, 2);
    iv.CopyTo(props, 2 + salt.Length);

    coder = new SevenZipCoder {
      CodecId = SevenZipConstants.CodecAes.ToArray(),
      NumInStreams = 1,
      NumOutStreams = 1,
      Properties = props,
    };
    return encrypted;
  }

  /// <summary>
  /// Creates a 7z archive split into multiple volumes.
  /// </summary>
  /// <param name="maxVolumeSize">Maximum size of each volume in bytes.</param>
  /// <param name="entries">The entries to add (name, data pairs).</param>
  /// <param name="codec">The compression codec to use.</param>
  /// <param name="password">Optional password for encryption.</param>
  /// <returns>An array of byte arrays, one per volume.</returns>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      SevenZipCodec codec = SevenZipCodec.Lzma2,
      string? password = null) {
    using var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, codec, leaveOpen: true, password: password)) {
      foreach (var (name, data) in entries)
        writer.AddEntry(new SevenZipEntry { Name = name }, data);
      writer.Finish();
    }

    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(ms.ToArray(), maxVolumeSize);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._finished)
        Finish();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
