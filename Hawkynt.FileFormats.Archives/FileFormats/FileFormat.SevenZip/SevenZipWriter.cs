using System.Security.Cryptography;
using Compression.Core.Checksums;
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
/// <para>
/// All file entries are compressed together in a single solid block for
/// maximum compression ratio. Call <c>Finish()</c> for single-block
/// or <c>Finish(maxThreads, maxBlockSize)</c> for parallel multi-block.
/// </para>
/// <para>
/// Every folder is written the way 7-Zip 26 lays it out: coders from the packed side to the
/// output, one output per coder, the unpack sizes in coder order, the packed streams listed
/// explicitly when a folder has several. With a password every packed stream goes through its
/// own AES-256 coder (one key per archive, a fresh IV per coder); BCJ2 folders carry the four
/// BCJ2 streams of 7-Zip's coder.
/// </para>
/// </remarks>
public sealed class SevenZipWriter : IDisposable {
  /// <summary>The key-derivation cost written into the AES properties: 2^19 SHA-256 rounds, 7-Zip's default.</summary>
  private const int AesCyclesPower = 19;

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
  private (byte[] Salt, byte[] Key)? _aesKey;
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
  /// Adds a directory entry carrying its own times and attributes.
  /// </summary>
  /// <param name="entry">The directory's metadata; its name and times are kept.</param>
  public void AddDirectory(SevenZipEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    entry.IsDirectory = true;
    entry.Size = 0;
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

  /// <summary>One solid block to encode: its files and how.</summary>
  private sealed record PlannedBlock(List<(SevenZipEntry Entry, byte[] Data)> Files, SevenZipCodec Codec, SevenZipFilter Filter, int DictionarySize);

  /// <summary>An encoded block: the packed streams in folder order, and the folder describing them.</summary>
  private sealed record EncodedBlock(byte[][] PackStreams, SevenZipFolder Folder, PlannedBlock Plan);

  /// <summary>
  /// Finalizes the archive with per-block codec overrides.
  /// Each block descriptor specifies which entries go into the block and
  /// optionally overrides the codec and filter for that block.
  /// Entries not covered by any descriptor are placed into a default block.
  /// </summary>
  public void FinishWithBlocks(IReadOnlyList<BlockDescriptor> blockDescs, int maxThreads = 1) {
    if (this._finished) return;
    this._finished = true;

    var (fileEntries, emptyEntries) = this.SplitEntries();
    var claimed = new HashSet<int>();
    var blocks = new List<PlannedBlock>();
    foreach (var desc in blockDescs) {
      var block = new List<(SevenZipEntry Entry, byte[] Data)>();
      foreach (var idx in desc.EntryIndices)
        if (idx >= 0 && idx < fileEntries.Count && claimed.Add(idx))
          block.Add(fileEntries[idx]);
      if (block.Count > 0)
        blocks.Add(new(block, desc.Codec ?? this._codec, desc.Filter ?? this._filter, desc.DictionarySize ?? this._dictionarySize));
    }

    var unclaimed = new List<(SevenZipEntry Entry, byte[] Data)>();
    for (var i = 0; i < fileEntries.Count; i++)
      if (!claimed.Contains(i))
        unclaimed.Add(fileEntries[i]);
    if (unclaimed.Count > 0)
      blocks.Add(new(unclaimed, this._codec, this._filter, this._dictionarySize));

    this.WriteArchive(blocks, emptyEntries, maxThreads);
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

    var (fileEntries, emptyEntries) = this.SplitEntries();
    var blocks = fileEntries.Count == 0
      ? []
      : SplitIntoBlocks(fileEntries, maxBlockSize).Select(b => new PlannedBlock(b, this._codec, this._filter, this._dictionarySize)).ToList();
    this.WriteArchive(blocks, emptyEntries, maxThreads);
  }

  private (List<(SevenZipEntry Entry, byte[] Data)> Files, List<SevenZipEntry> Empty) SplitEntries() {
    var files = new List<(SevenZipEntry Entry, byte[] Data)>();
    var empty = new List<SevenZipEntry>();
    foreach (var (entry, data) in this._entries) {
      if (entry.IsDirectory || data.Length == 0)
        empty.Add(entry);
      else
        files.Add((entry, data));
    }
    return (files, empty);
  }

  /// <summary>Encodes every block (in parallel when allowed), writes the packed streams in order, then the header.</summary>
  private void WriteArchive(List<PlannedBlock> blocks, List<SevenZipEntry> emptyEntries, int maxThreads) {
    if (this._password != null && blocks.Count > 0)
      this.EnsureKey();

    var encoded = new EncodedBlock[blocks.Count];
    if (maxThreads > 1 && blocks.Count > 1)
      Parallel.For(0, blocks.Count, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, i => encoded[i] = this.EncodeBlock(blocks[i]));
    else
      for (var i = 0; i < blocks.Count; ++i)
        encoded[i] = this.EncodeBlock(blocks[i]);

    var packSizes = new List<long>();
    var folders = new List<SevenZipFolder>();
    var numUnpack = new List<int>();
    var unpackSizes = new List<long>();
    var digests = new List<uint?>();
    var fileInfos = new List<SevenZipFileInfo>();
    foreach (var block in encoded) {
      foreach (var stream in block.PackStreams) {
        this._stream.Write(stream);
        packSizes.Add(stream.Length);
      }
      folders.Add(block.Folder);
      numUnpack.Add(block.Plan.Files.Count);
      foreach (var (entry, data) in block.Plan.Files) {
        unpackSizes.Add(data.Length);
        digests.Add(entry.Crc ?? Crc32.Compute(data));
        fileInfos.Add(MakeFileInfo(entry));
      }
    }

    foreach (var entry in emptyEntries)
      fileInfos.Add(MakeFileInfo(entry));

    // Attributes for all or for none, as 7-Zip writes them: libarchive misreads a partly defined
    // attribute vector. A file that has none gets FILE_ATTRIBUTE_ARCHIVE, 7-Zip's plain file.
    if (fileInfos.Exists(static f => f.Attributes.HasValue))
      foreach (var info in fileInfos)
        info.Attributes ??= 0x20u;

    var packInfo = new SevenZipPackInfo { PackPos = 0, PackSizes = [.. packSizes], PackCrcs = new uint?[packSizes.Count] };
    var subStreams = new SevenZipSubStreamsInfo { NumUnpackStreams = [.. numUnpack], UnpackSizes = [.. unpackSizes], Digests = [.. digests] };
    this.FinalizeHeader(packInfo, folders, subStreams, fileInfos);
  }

  /// <summary>Filters, compresses and (with a password) encrypts one block. Thread-safe.</summary>
  /// <remarks>
  /// Coders are listed from the packed side to the output, each bound to the one before — the
  /// order 7-Zip 26 writes (<c>[7zAES,] compressor[, filter]</c>, bind pairs sorted by the coder
  /// that feeds them). Any order is valid 7z; libarchive reads filter chains only in this one.
  /// </remarks>
  private EncodedBlock EncodeBlock(PlannedBlock plan) {
    var solid = ConcatenateBlock(plan.Files);
    if (plan.Filter == SevenZipFilter.Bcj2)
      return this.EncodeBcj2Block(solid, plan);

    var folder = new SevenZipFolder();
    var sizes = new List<long>();
    var data = solid;
    SevenZipCoder? filterCoder = null;
    if (plan.Filter != SevenZipFilter.None)
      data = this.ApplyFilter(solid, plan.Filter, out filterCoder);
    var packed = this.Compress(data, plan.Codec, plan.DictionarySize, out var compressor);

    if (this._password != null) {
      var plain = packed;
      packed = this.EncryptAes(plain, out var aes);
      folder.Coders.Add(aes);
      sizes.Add(plain.Length);
    }
    folder.Coders.Add(compressor);
    sizes.Add(data.Length);
    if (filterCoder != null) {
      folder.Coders.Add(filterCoder);
      sizes.Add(solid.Length);
    }
    for (var i = 1; i < folder.Coders.Count; ++i)
      folder.BindPairs.Add((i, i - 1)); // coder i's input is coder i-1's output
    folder.UnpackSizes = [.. sizes];
    folder.PackedStreams = [0];
    return new EncodedBlock([packed], folder, plan);
  }

  /// <summary>
  /// The BCJ2 folder exactly as 7-Zip 26 lays it out: LZMA for the jump and call streams, the
  /// block's compressor for the main stream, BCJ2 last with inputs main, call, jump, range coder;
  /// packed streams in the order main, range coder, call, jump. With a password one AES coder per
  /// packed stream comes first (jump, call, range coder, main), as 7-Zip writes them.
  /// </summary>
  private EncodedBlock EncodeBcj2Block(byte[] solid, PlannedBlock plan) {
    var (main, call, jump, rangeCoded) = Bcj2Filter.Encode(solid);
    var packedMain = this.Compress(main, plan.Codec, plan.DictionarySize, out var mainCoder);
    var packedCall = this.Compress(call, SevenZipCodec.Lzma, 1 << 20, out var callCoder);
    var packedJump = this.Compress(jump, SevenZipCodec.Lzma, 1 << 20, out var jumpCoder);
    var bcj2 = new SevenZipCoder { CodecId = SevenZipConstants.CodecBcj2.ToArray(), NumInStreams = 4, NumOutStreams = 1 };
    var folder = new SevenZipFolder();

    if (this._password == null) {
      // Coders: 0 LZMA (jump), 1 LZMA (call), 2 main compressor, 3 BCJ2 (in-streams 3..6).
      folder.Coders.AddRange([jumpCoder, callCoder, mainCoder, bcj2]);
      folder.BindPairs.AddRange([(5, 0), (4, 1), (3, 2)]);
      folder.PackedStreams = [2, 6, 1, 0];
      folder.UnpackSizes = [jump.Length, call.Length, main.Length, solid.Length];
      return new EncodedBlock([packedMain, rangeCoded, packedCall, packedJump], folder, plan);
    }

    // Coders: 0-3 AES (jump, call, range coder, main), 4 LZMA (jump), 5 LZMA (call),
    // 6 main compressor, 7 BCJ2 (in-streams 7..10).
    var encJump = this.EncryptAes(packedJump, out var aesJump);
    var encCall = this.EncryptAes(packedCall, out var aesCall);
    var encRange = this.EncryptAes(rangeCoded, out var aesRange);
    var encMain = this.EncryptAes(packedMain, out var aesMain);
    folder.Coders.AddRange([aesJump, aesCall, aesRange, aesMain, jumpCoder, callCoder, mainCoder, bcj2]);
    folder.BindPairs.AddRange([(4, 0), (5, 1), (10, 2), (6, 3), (9, 4), (8, 5), (7, 6)]);
    folder.PackedStreams = [3, 2, 1, 0];
    folder.UnpackSizes = [packedJump.Length, packedCall.Length, rangeCoded.Length, packedMain.Length, jump.Length, call.Length, main.Length, solid.Length];
    return new EncodedBlock([encMain, encRange, encCall, encJump], folder, plan);
  }

  private void FinalizeHeader(SevenZipPackInfo packInfo, List<SevenZipFolder> folders,
      SevenZipSubStreamsInfo subStreams, List<SevenZipFileInfo> fileInfos) {
    using var headerStream = new MemoryStream();
    SevenZipHeaderCodec.WriteHeader(headerStream, packInfo, folders, subStreams, fileInfos);
    var headerData = headerStream.ToArray();

    if (this._encryptHeaders) {
      this.EnsureKey();
      // The header is packed with LZMA and encrypted, the folder 7-Zip's -mhe writes:
      // coder 0 AES, coder 1 LZMA fed by it (the output), the header's CRC on the folder.
      var compressed = this.Compress(headerData, SevenZipCodec.Lzma, 1 << 20, out var lzmaCoder);
      var encrypted = this.EncryptAes(compressed, out var aesCoder);
      var headerFolder = new SevenZipFolder { UnpackCrc = Crc32.Compute(headerData), PackedStreams = [0] };
      headerFolder.Coders.Add(aesCoder);
      headerFolder.Coders.Add(lzmaCoder);
      headerFolder.BindPairs.Add((1, 0));
      headerFolder.UnpackSizes = [compressed.Length, headerData.Length];

      var packedDataPos = this._stream.Position - SevenZipConstants.SignatureHeaderSize;
      this._stream.Write(encrypted);
      var encPackInfo = new SevenZipPackInfo { PackPos = packedDataPos, PackSizes = [encrypted.Length], PackCrcs = [null] };

      using var ehStream = new MemoryStream();
      SevenZipHeaderCodec.WriteEncodedHeader(ehStream, encPackInfo, [headerFolder]);
      headerData = ehStream.ToArray();
    }

    var headerOffset = this._stream.Position - SevenZipConstants.SignatureHeaderSize;
    this._stream.Write(headerData, 0, headerData.Length);
    this._stream.Position = 0;
    new SevenZipHeader {
      NextHeaderOffset = headerOffset,
      NextHeaderSize = headerData.Length,
      NextHeaderCrc = Crc32.Compute(headerData),
    }.Write(this._stream);
    this._stream.Seek(0, SeekOrigin.End);
  }

  private static SevenZipFileInfo MakeFileInfo(SevenZipEntry entry) => new() {
    Name = entry.Name,
    IsDirectory = entry.IsDirectory,
    IsEmptyStream = entry.IsDirectory || entry.Size == 0,
    IsEmptyFile = !entry.IsDirectory && entry.Size == 0,
    LastWriteTime = entry.LastWriteTime,
    CreationTime = entry.CreationTime,
    LastAccessTime = entry.LastAccessTime,
    // A directory always carries FILE_ATTRIBUTE_DIRECTORY, as 7-Zip writes it: readers such as
    // py7zr tell directories from empty files by that bit, not by the empty-stream vectors.
    Attributes = entry.IsDirectory ? (entry.Attributes ?? 0) | 0x10u : entry.Attributes,
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

  private static byte[] ConcatenateBlock(List<(SevenZipEntry Entry, byte[] Data)> block) {
    var totalSize = 0L;
    foreach (var (_, data) in block)
      totalSize += data.Length;
    if (totalSize > Array.MaxLength)
      throw new NotSupportedException($"7z: a solid block of {totalSize} bytes exceeds what this writer holds in memory; use a smaller solid block size.");

    var solidData = new byte[totalSize];
    var offset = 0;
    foreach (var (_, data) in block) {
      data.AsSpan().CopyTo(solidData.AsSpan(offset));
      offset += data.Length;
    }
    return solidData;
  }

  /// <summary>Applies a length-preserving filter and returns its coder.</summary>
  private byte[] ApplyFilter(byte[] data, SevenZipFilter filter, out SevenZipCoder coder) {
    switch (filter) {
      case SevenZipFilter.Copy:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecCopy.ToArray() };
        return data;
      case SevenZipFilter.BcjX86:
        return Branch(data, BranchArchitecture.X86, SevenZipConstants.CodecBcj, out coder);
      case SevenZipFilter.BcjArm:
        return Branch(data, BranchArchitecture.Arm, SevenZipConstants.CodecBcjArm, out coder);
      case SevenZipFilter.BcjArmThumb:
        return Branch(data, BranchArchitecture.ArmThumb, SevenZipConstants.CodecBcjArmThumb, out coder);
      case SevenZipFilter.BcjPowerPC:
        return Branch(data, BranchArchitecture.PowerPC, SevenZipConstants.CodecBcjPpc, out coder);
      case SevenZipFilter.BcjSparc:
        return Branch(data, BranchArchitecture.Sparc, SevenZipConstants.CodecBcjSparc, out coder);
      case SevenZipFilter.BcjIA64:
        return Branch(data, BranchArchitecture.IA64, SevenZipConstants.CodecBcjIa64, out coder);
      case SevenZipFilter.Delta:
        coder = new SevenZipCoder {
          CodecId = SevenZipConstants.CodecDelta.ToArray(),
          Properties = [(byte)(this._deltaDistance - 1)],
        };
        return DeltaFilter.Encode(data, this._deltaDistance);
      case SevenZipFilter.Swap2:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecSwap2.ToArray() };
        return ByteSwapFilter.Swap2(data);
      case SevenZipFilter.Swap4:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecSwap4.ToArray() };
        return ByteSwapFilter.Swap4(data);
      default:
        throw new NotSupportedException($"Unsupported filter: {filter}");
    }

    static byte[] Branch(byte[] data, BranchArchitecture architecture, ReadOnlySpan<byte> id, out SevenZipCoder coder) {
      coder = new SevenZipCoder { CodecId = id.ToArray() };
      var filtered = data.ToArray();
      BranchConverter.ConvertAll(architecture, filtered, decoding: false);
      return filtered;
    }
  }

  /// <summary>Compresses with <paramref name="codec"/> and returns its coder. Thread-safe.</summary>
  private byte[] Compress(byte[] data, SevenZipCodec codec, int dictionarySize, out SevenZipCoder coder) {
    switch (codec) {
      case SevenZipCodec.Lzma2: {
        var encoder = new Lzma2Encoder(dictionarySize);
        using var ms = new MemoryStream();
        encoder.Encode(ms, data);
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecLzma2.ToArray(), Properties = [encoder.DictionarySizeByte] };
        return ms.ToArray();
      }
      case SevenZipCodec.Lzma: {
        // The folder records the size, so the stream needs no end marker; 7-Zip writes none.
        var encoder = new LzmaEncoder(dictionarySize);
        using var ms = new MemoryStream();
        encoder.Encode(ms, data, writeEndMarker: false);
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecLzma.ToArray(), Properties = encoder.Properties };
        return ms.ToArray();
      }
      case SevenZipCodec.Deflate:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecDeflate.ToArray() };
        return DeflateCompressor.Compress(data);
      case SevenZipCodec.BZip2: {
        using var ms = new MemoryStream();
        using (var bz2 = new FileFormat.Bzip2.Bzip2Stream(ms, CompressionStreamMode.Compress, leaveOpen: true))
          bz2.Write(data, 0, data.Length);
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecBzip2.ToArray() };
        return ms.ToArray();
      }
      case SevenZipCodec.PPMd:
        return this.CompressPpmd(data, out coder);
      case SevenZipCodec.Copy:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecCopy.ToArray() };
        return data;
      default:
        throw new NotSupportedException($"Unsupported codec: {codec}");
    }
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

    coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecPpmd.ToArray(), Properties = props };
    return compressedStream.ToArray();
  }

  /// <summary>Derives the archive's AES key once: 2^19 SHA-256 rounds are the expensive part.</summary>
  private void EnsureKey() {
    if (this._aesKey != null)
      return;
    var salt = RandomNumberGenerator.GetBytes(16);
    this._aesKey = (salt, SevenZipKeyCache.DeriveKey(this._password!, salt, AesCyclesPower));
  }

  /// <summary>
  /// Encrypts with AES-256-CBC under the archive key and a fresh 16-byte IV, zero-padding to
  /// whole blocks (the coder's unpack size cuts the padding off again). Thread-safe.
  /// </summary>
  private byte[] EncryptAes(byte[] data, out SevenZipCoder coder) {
    var (salt, key) = this._aesKey ?? throw new InvalidOperationException("7z: no key derived.");
    var iv = RandomNumberGenerator.GetBytes(16);

    var padded = new byte[(data.Length + 15) & ~15];
    data.CopyTo(padded, 0);
    using var aes = Aes.Create();
    aes.Key = key;
    var encrypted = aes.EncryptCbc(padded, iv, PaddingMode.None);

    // Byte 0: NumCyclesPower | 0x80 (salt present) | 0x40 (IV present); byte 1: (salt-1) << 4 | (IV-1).
    var props = new byte[2 + salt.Length + iv.Length];
    props[0] = (byte)(AesCyclesPower | 0x80 | 0x40);
    props[1] = (byte)(((salt.Length - 1) << 4) | (iv.Length - 1));
    salt.CopyTo(props, 2);
    iv.CopyTo(props, 2 + salt.Length);

    coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecAes.ToArray(), Properties = props };
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
