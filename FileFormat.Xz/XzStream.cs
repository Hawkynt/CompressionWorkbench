using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.Streams;
using Compression.Core.Transforms;

namespace FileFormat.Xz;

/// <summary>
/// Stream for reading and writing XZ format data.
/// </summary>
public sealed class XzStream : CompressionStream {
  private readonly int _dictionarySize;
  private readonly byte _checkType;
  private readonly List<(ulong FilterId, byte[] Properties)> _preFilters;

  // Compression state
  private MemoryStream? _compressBuffer;

  // Decompression state
  private byte[]? _decompressedData;
  private int _decompressPos;
  private bool _headerRead;
  private bool _finished;

  /// <summary>
  /// Initializes a new <see cref="XzStream"/>.
  /// </summary>
  /// <param name="stream">The underlying stream.</param>
  /// <param name="mode">Whether to compress or decompress.</param>
  /// <param name="dictionarySize">The LZMA2 dictionary size.</param>
  /// <param name="leaveOpen">Whether to leave the inner stream open.</param>
  public XzStream(Stream stream, CompressionStreamMode mode,
    int dictionarySize = 1 << 23, bool leaveOpen = false)
    : this(stream, mode, dictionarySize, XzConstants.CheckCrc64, leaveOpen) {
  }

  /// <summary>
  /// Initializes a new <see cref="XzStream"/> with a specific check type.
  /// </summary>
  /// <param name="stream">The underlying stream.</param>
  /// <param name="mode">Whether to compress or decompress.</param>
  /// <param name="dictionarySize">The LZMA2 dictionary size.</param>
  /// <param name="checkType">The integrity check type.</param>
  /// <param name="leaveOpen">Whether to leave the inner stream open.</param>
  public XzStream(Stream stream, CompressionStreamMode mode,
    int dictionarySize, byte checkType, bool leaveOpen = false)
    : this(stream, mode, dictionarySize, checkType, null, leaveOpen) {
  }

  /// <summary>
  /// Initializes a new <see cref="XzStream"/> with a specific check type and pre-filters.
  /// </summary>
  public XzStream(Stream stream, CompressionStreamMode mode,
    int dictionarySize, byte checkType,
    IEnumerable<(ulong FilterId, byte[] Properties)>? preFilters,
    bool leaveOpen = false)
    : base(stream, mode, leaveOpen) {
    this._dictionarySize = dictionarySize;
    this._checkType = checkType;
    this._preFilters = preFilters != null ? new(preFilters) : [];

    if (mode == CompressionStreamMode.Compress)
      this._compressBuffer = new MemoryStream();
  }

  /// <inheritdoc />
  protected override int DecompressBlock(byte[] buffer, int offset, int count) {
    if (this._finished)
      return 0;

    if (!this._headerRead) {
      DecompressAll();
      this._headerRead = true;
    }

    if (this._decompressedData == null || this._decompressPos >= this._decompressedData.Length) {
      this._finished = true;
      return 0;
    }

    var available = this._decompressedData.Length - this._decompressPos;
    var toCopy = Math.Min(available, count);
    this._decompressedData.AsSpan(this._decompressPos, toCopy).CopyTo(buffer.AsSpan(offset));
    this._decompressPos += toCopy;
    return toCopy;
  }

  /// <inheritdoc />
  protected override void CompressBlock(byte[] buffer, int offset, int count) {
    this._compressBuffer!.Write(buffer, offset, count);
  }

  /// <inheritdoc />
  protected override void FinishCompression() {
    var data = this._compressBuffer!.ToArray();

    // Write stream header
    var header = new XzStreamHeader(this._checkType);
    header.Write(InnerStream);

    if (data.Length == 0) {
      // Empty stream: write index and footer only
      var emptyIndex = new XzIndex();
      var indexStart = InnerStream.Position;
      emptyIndex.Write(InnerStream);
      var indexSize = InnerStream.Position - indexStart;

      var backwardSize = (uint)(indexSize / 4 - 1);
      var footer = new XzStreamFooter(backwardSize, this._checkType);
      footer.Write(InnerStream);
      return;
    }

    // Apply pre-filters before LZMA2 compression
    var filteredData = data;
    foreach (var (filterId, props) in this._preFilters)
      filteredData = ApplyForwardFilter(filterId, props, filteredData);

    // LZMA2 compress
    var lzma2Encoder = new Lzma2Encoder(this._dictionarySize);
    using var compressedBlock = new MemoryStream();
    lzma2Encoder.Encode(compressedBlock, filteredData);
    var compressedData = compressedBlock.ToArray();

    // Write block header
    var blockStartPos = InnerStream.Position;
    var blockHeader = new XzBlockHeader {
      CompressedSize = compressedData.Length,
      UncompressedSize = data.Length,
    };
    // Add pre-filters first, then LZMA2
    foreach (var (filterId, props) in this._preFilters)
      blockHeader.Filters.Add((filterId, props));
    blockHeader.Filters.Add((XzConstants.FilterLzma2, [lzma2Encoder.DictionarySizeByte]));
    blockHeader.Write(InnerStream);

    var blockHeaderEnd = InnerStream.Position;

    // Write compressed data
    InnerStream.Write(compressedData);

    // Pad compressed data to 4-byte alignment (padding comes before check per XZ spec)
    var compressedEnd = InnerStream.Position;
    var blockPadding = (int)((4 - ((compressedEnd - blockStartPos) % 4)) % 4);
    for (var i = 0; i < blockPadding; ++i)
      InnerStream.WriteByte(0);

    // Write check after padding
    WriteCheck(data);

    // Unpadded size = block header + compressed data + check (excludes padding)
    var blockHeaderSize = blockHeaderEnd - blockStartPos;
    var checkSize = XzConstants.CheckSize(this._checkType);
    var unpaddedSize = blockHeaderSize + compressedData.Length + checkSize;

    // Write index
    var index = new XzIndex();
    index.Records.Add((unpaddedSize, data.Length));
    var indexStart2 = InnerStream.Position;
    index.Write(InnerStream);
    var indexSize2 = InnerStream.Position - indexStart2;

    // Write stream footer
    var backwardSize2 = (uint)(indexSize2 / 4 - 1);
    var footer2 = new XzStreamFooter(backwardSize2, this._checkType);
    footer2.Write(InnerStream);
  }

  private void WriteCheck(byte[] data) {
    switch (this._checkType) {
      case XzConstants.CheckNone:
        break;
      case XzConstants.CheckCrc32:
        var crc32 = Crc32.Compute(data);
        InnerStream.WriteByte((byte)crc32);
        InnerStream.WriteByte((byte)(crc32 >> 8));
        InnerStream.WriteByte((byte)(crc32 >> 16));
        InnerStream.WriteByte((byte)(crc32 >> 24));
        break;
      case XzConstants.CheckCrc64:
        var crc64 = Crc64.Compute(data);
        for (var i = 0; i < 8; ++i)
          InnerStream.WriteByte((byte)(crc64 >> (i * 8)));
        break;
      case XzConstants.CheckSha256:
        var shaW = new Sha256();
        shaW.Update(data);
        shaW.Finish();
        var hash = shaW.Hash;
        InnerStream.Write(hash);
        break;
      default:
        throw new NotSupportedException($"Check type 0x{this._checkType:X2} not supported.");
    }
  }

  private void DecompressAll() {
    // Read stream header
    var header = XzStreamHeader.Read(InnerStream);
    var checkType = header.CheckType;

    using var output = new MemoryStream();

    // Read blocks until index
    while (true) {
      // Peek at next byte: 0x00 means index
      var peekByte = InnerStream.ReadByte();
      if (peekByte < 0)
        throw new EndOfStreamException("Unexpected end of XZ stream.");

      if (peekByte == 0x00) {
        // This is the index indicator — put it back by seeking
        --InnerStream.Position;
        break;
      }

      // Block header
      --InnerStream.Position; // Put back the size byte
      var blockHeader = XzBlockHeader.Read(InnerStream);

      // Find LZMA2 filter
      var dictSize = this._dictionarySize;
      foreach (var (filterId, props) in blockHeader.Filters) {
        if (filterId == XzConstants.FilterLzma2 && props.Length > 0)
          dictSize = DecodeDictionarySize(props[0]);
      }

      // Read compressed data
      int compressedSize;
      if (blockHeader.CompressedSize.HasValue)
        compressedSize = (int)blockHeader.CompressedSize.Value;
      else
        throw new NotSupportedException("XZ blocks without compressed size not supported.");

      var compressedData = new byte[compressedSize];
      var totalRead = 0;
      while (totalRead < compressedSize) {
        var read = InnerStream.Read(compressedData, totalRead, compressedSize - totalRead);
        if (read == 0) throw new EndOfStreamException("Truncated XZ block.");
        totalRead += read;
      }

      // Decompress with LZMA2
      using var compressedStream = new MemoryStream(compressedData);
      var lzma2Decoder = new Lzma2Decoder(compressedStream, dictSize);
      var decompressed = lzma2Decoder.Decode();

      // Apply non-compression filters in reverse order
      // In XZ, filters are listed first-to-last (pre-filters before LZMA2)
      // During decompression, we need to reverse pre-filters after LZMA2 decode
      for (var fi = blockHeader.Filters.Count - 2; fi >= 0; --fi) {
        var (filterId, props) = blockHeader.Filters[fi];
        decompressed = ApplyReverseFilter(filterId, props, decompressed);
      }

      output.Write(decompressed);

      // Skip block padding (padding aligns header + compressed data to 4 bytes, before the check)
      var bytesBeforePadding = blockHeader.HeaderSize + compressedSize;
      var blockPadding = (int)((4 - (bytesBeforePadding % 4)) % 4);
      for (var i = 0; i < blockPadding; ++i)
        InnerStream.ReadByte();

      // Verify check (comes after padding per XZ spec)
      VerifyCheck(checkType, decompressed);
    }

    // Read index
    _ = XzIndex.Read(InnerStream);

    // Read stream footer
    _ = XzStreamFooter.Read(InnerStream);

    this._decompressedData = output.ToArray();
    this._decompressPos = 0;
  }

  private void VerifyCheck(byte checkType, byte[] data) {
    var checkSize = XzConstants.CheckSize(checkType);
    if (checkSize == 0)
      return;

    var checkBuf = new byte[checkSize];
    if (InnerStream.Read(checkBuf, 0, checkSize) != checkSize)
      throw new EndOfStreamException("Truncated XZ check.");

    switch (checkType) {
      case XzConstants.CheckCrc32:
        var storedCrc32 = (uint)(checkBuf[0] | (checkBuf[1] << 8) |
                     (checkBuf[2] << 16) | (checkBuf[3] << 24));
        var computedCrc32 = Crc32.Compute(data);
        if (storedCrc32 != computedCrc32)
          throw new InvalidDataException("XZ block CRC-32 mismatch.");
        break;
      case XzConstants.CheckCrc64:
        ulong storedCrc64 = 0;
        for (var i = 0; i < 8; ++i)
          storedCrc64 |= (ulong)checkBuf[i] << (i * 8);
        var computedCrc64 = Crc64.Compute(data);
        if (storedCrc64 != computedCrc64)
          throw new InvalidDataException("XZ block CRC-64 mismatch.");
        break;
      case XzConstants.CheckSha256:
        var sha2 = new Sha256();
        sha2.Update(data);
        sha2.Finish();
        var computedHash = sha2.Hash;
        if (!checkBuf.AsSpan().SequenceEqual(computedHash))
          throw new InvalidDataException("XZ block SHA-256 mismatch.");
        break;
    }
  }

  private static byte[] ApplyReverseFilter(ulong filterId, byte[] props, byte[] data) {
    return filterId switch {
      XzConstants.FilterDelta => DeltaFilter.Decode(data,
        props.Length > 0 ? props[0] + 1 : 1),
      XzConstants.FilterBcjX86 => BcjFilter.DecodeX86(data),
      XzConstants.FilterBcjPowerPc => BcjFilter.DecodePowerPC(data),
      XzConstants.FilterBcjIa64 => BcjFilter.DecodeIA64(data),
      XzConstants.FilterBcjArm => BcjFilter.DecodeArm(data),
      XzConstants.FilterBcjArmThumb => BcjFilter.DecodeArmThumb(data),
      XzConstants.FilterBcjSparc => BcjFilter.DecodeSparc(data),
      _ => throw new NotSupportedException($"Unsupported XZ filter: 0x{filterId:X}")
    };
  }

  private static byte[] ApplyForwardFilter(ulong filterId, byte[] props, byte[] data) {
    return filterId switch {
      XzConstants.FilterDelta => DeltaFilter.Encode(data,
        props.Length > 0 ? props[0] + 1 : 1),
      XzConstants.FilterBcjX86 => BcjFilter.EncodeX86(data),
      XzConstants.FilterBcjPowerPc => BcjFilter.EncodePowerPC(data),
      XzConstants.FilterBcjIa64 => BcjFilter.EncodeIA64(data),
      XzConstants.FilterBcjArm => BcjFilter.EncodeArm(data),
      XzConstants.FilterBcjArmThumb => BcjFilter.EncodeArmThumb(data),
      XzConstants.FilterBcjSparc => BcjFilter.EncodeSparc(data),
      _ => throw new NotSupportedException($"Unsupported XZ filter: 0x{filterId:X}")
    };
  }

  private static int DecodeDictionarySize(byte encoded) {
    if (encoded == 0)
      return 4096;

    var bits = encoded / 2 + 12;
    if ((encoded & 1) == 0)
      return 1 << bits;
    return (3 << (bits - 1));
  }
}
