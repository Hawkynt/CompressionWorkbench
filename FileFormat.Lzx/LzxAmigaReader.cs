using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzx;

namespace FileFormat.Lzx;

/// <summary>
/// Reads entries from an Amiga LZX archive.
/// </summary>
/// <remarks>
/// The Amiga LZX format supports merged (solid) groups where multiple entries share
/// a single compressed block. The reader tracks these groups so that extraction
/// decompresses the entire group and returns only the requested entry's slice.
/// </remarks>
public sealed class LzxAmigaReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<LzxAmigaEntry> _entries = [];
  private bool _disposed;

  /// <summary>Gets the entries present in the archive.</summary>
  public IReadOnlyList<LzxAmigaEntry> Entries => this._entries;

  /// <summary>
  /// Initializes a new <see cref="LzxAmigaReader"/> and reads the archive directory.
  /// </summary>
  /// <param name="stream">A seekable stream containing an Amiga LZX archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this reader is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="InvalidDataException">Thrown when the stream does not contain a valid Amiga LZX archive.</exception>
  public LzxAmigaReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this.ReadArchive();
  }

  /// <summary>
  /// Extracts the uncompressed data for the specified entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The decompressed file data.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
  /// <exception cref="InvalidDataException">Thrown when decompression fails or CRC does not match.</exception>
  public byte[] Extract(LzxAmigaEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    return entry.Method switch {
      LzxAmigaConstants.MethodStored => this.ExtractStored(entry),
      LzxAmigaConstants.MethodLzx => this.ExtractLzx(entry),
      _ => throw new InvalidDataException($"Unsupported Amiga LZX compression method: {entry.Method}.")
    };
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  // ── Archive parsing ────────────────────────────────────────────────────

  private void ReadArchive() {
    // Validate magic
    Span<byte> magic = stackalloc byte[LzxAmigaConstants.MagicLength];
    if (this._stream.Read(magic) < LzxAmigaConstants.MagicLength)
      throw new InvalidDataException("Stream is too short to contain an Amiga LZX archive.");

    if (!magic.SequenceEqual(LzxAmigaConstants.Magic))
      throw new InvalidDataException(
        $"Invalid Amiga LZX magic: expected 0x4C5A58, got 0x{magic[0]:X2}{magic[1]:X2}{magic[2]:X2}.");

    // Read entries sequentially
    while (this._stream.Position < this._stream.Length) {
      var entry = this.ReadEntryHeader();
      if (entry is null)
        break;

      this._entries.Add(entry);

      // Skip compressed data (only present on the final entry of a group)
      if (entry.CompressedSize > 0)
        this._stream.Seek(entry.CompressedSize, SeekOrigin.Current);
    }

    // Assign group metadata
    this.AssignGroups();
  }

  private LzxAmigaEntry? ReadEntryHeader() {
    // Try to read the fixed header
    var header = new byte[LzxAmigaConstants.FixedHeaderSize];
    var bytesRead = this._stream.Read(header, 0, header.Length);
    if (bytesRead == 0)
      return null; // end of archive
    if (bytesRead < header.Length)
      throw new InvalidDataException("Truncated Amiga LZX entry header.");

    var attributes = (uint)(header[LzxAmigaConstants.OffsetAttributes] |
                           (header[LzxAmigaConstants.OffsetAttributes + 1] << 8));
    var uncompressedSize = BitConverter.ToUInt32(header, LzxAmigaConstants.OffsetUncompressedSize);
    var compressedSize = BitConverter.ToUInt32(header, LzxAmigaConstants.OffsetCompressedSize);
    var machineType = header[LzxAmigaConstants.OffsetMachineType];
    var method = header[LzxAmigaConstants.OffsetMethod];
    var flags = header[LzxAmigaConstants.OffsetFlags];
    var commentLength = header[LzxAmigaConstants.OffsetCommentLength];
    var filenameLength = header[LzxAmigaConstants.OffsetFilenameLength];

    // Read date stamp (4 bytes LE — packed Amiga date)
    var dateRaw = BitConverter.ToUInt32(header, LzxAmigaConstants.OffsetDate);
    var lastModified = DecodeAmigaDate(dateRaw);

    // Read CRC-32 of uncompressed data
    var dataCrc = BitConverter.ToUInt32(header, LzxAmigaConstants.OffsetDataCrc);

    // Read header CRC (we read but don't verify for tolerance)
    // var headerCrc = BitConverter.ToUInt32(header, LzxAmigaConstants.OffsetHeaderCrc);

    // Read variable-length filename
    var filenameBytes = new byte[filenameLength];
    if (this._stream.Read(filenameBytes, 0, filenameLength) < filenameLength)
      throw new InvalidDataException("Truncated Amiga LZX entry filename.");
    var fileName = Encoding.Latin1.GetString(filenameBytes);

    // Read variable-length comment
    var comment = string.Empty;
    if (commentLength > 0) {
      var commentBytes = new byte[commentLength];
      if (this._stream.Read(commentBytes, 0, commentLength) < commentLength)
        throw new InvalidDataException("Truncated Amiga LZX entry comment.");
      comment = Encoding.Latin1.GetString(commentBytes);
    }

    var isMerged = (flags & LzxAmigaConstants.FlagMerged) != 0;
    var dataOffset = this._stream.Position;

    return new LzxAmigaEntry {
      FileName = fileName,
      OriginalSize = uncompressedSize,
      CompressedSize = compressedSize,
      Method = method,
      Attributes = attributes,
      LastModified = lastModified,
      Comment = comment,
      Crc = dataCrc,
      IsMerged = isMerged,
      MachineType = machineType,
      DataOffset = dataOffset,
      GroupIndex = 0,
      GroupSize = 1,
      GroupDataEntryIndex = this._entries.Count
    };
  }

  private void AssignGroups() {
    // Walk entries and group consecutive merged entries together.
    // A group consists of zero or more merged entries followed by one non-merged entry.
    var i = 0;
    while (i < this._entries.Count) {
      var groupStartIdx = i;

      // Advance past merged entries
      while (i < this._entries.Count && this._entries[i].IsMerged)
        ++i;

      // Include the final non-merged entry
      if (i < this._entries.Count)
        ++i;

      var groupSize = i - groupStartIdx;
      var dataEntryIdx = i - 1; // last entry holds the compressed data

      for (var j = groupStartIdx; j < i; ++j) {
        var e = this._entries[j];
        this._entries[j] = new LzxAmigaEntry {
          FileName = e.FileName,
          OriginalSize = e.OriginalSize,
          CompressedSize = e.CompressedSize,
          Method = e.Method,
          Attributes = e.Attributes,
          LastModified = e.LastModified,
          Comment = e.Comment,
          Crc = e.Crc,
          IsMerged = e.IsMerged,
          MachineType = e.MachineType,
          DataOffset = e.DataOffset,
          GroupIndex = j - groupStartIdx,
          GroupSize = groupSize,
          GroupDataEntryIndex = dataEntryIdx
        };
      }
    }
  }

  // ── Extraction ─────────────────────────────────────────────────────────

  private byte[] ExtractStored(LzxAmigaEntry entry) {
    // For stored entries the data is inline (even in a merged group, stored means raw).
    // However, if grouped, we still need to find the right slice.
    if (entry.GroupSize == 1) {
      this._stream.Position = entry.DataOffset;
      var data = new byte[entry.OriginalSize];
      if (this._stream.Read(data, 0, data.Length) < data.Length)
        throw new InvalidDataException("Truncated Amiga LZX stored entry data.");
      VerifyCrc(data, entry.Crc, entry.FileName);
      return data;
    }

    // Merged stored group: concatenated raw data at the final entry's offset
    return this.ExtractFromGroup(entry);
  }

  private byte[] ExtractLzx(LzxAmigaEntry entry) => this.ExtractFromGroup(entry);

  private byte[] ExtractFromGroup(LzxAmigaEntry entry) {
    // Find the data entry (final entry in the group)
    var dataEntry = this._entries[entry.GroupDataEntryIndex];

    // Seek to the compressed data
    this._stream.Position = dataEntry.DataOffset;
    var compressedData = new byte[dataEntry.CompressedSize];
    if (this._stream.Read(compressedData, 0, compressedData.Length) < compressedData.Length)
      throw new InvalidDataException("Truncated Amiga LZX compressed data.");

    // Calculate total uncompressed size for the group
    var groupStartIdx = entry.GroupDataEntryIndex - entry.GroupSize + 1;
    var totalUncompressed = 0u;
    for (var i = groupStartIdx; i <= entry.GroupDataEntryIndex; ++i)
      totalUncompressed += this._entries[i].OriginalSize;

    // Decompress the entire group
    byte[] decompressedGroup;
    if (dataEntry.Method == LzxAmigaConstants.MethodStored) {
      decompressedGroup = compressedData;
    } else {
      using var compressedStream = new MemoryStream(compressedData);
      var decompressor = new LzxDecompressor(compressedStream, LzxAmigaConstants.DefaultWindowBits);
      decompressedGroup = decompressor.Decompress((int)totalUncompressed);
    }

    // Extract the correct slice for the requested entry
    var offset = 0u;
    for (var i = groupStartIdx; i < groupStartIdx + entry.GroupIndex; ++i)
      offset += this._entries[i].OriginalSize;

    var result = new byte[entry.OriginalSize];
    Array.Copy(decompressedGroup, offset, result, 0, entry.OriginalSize);

    VerifyCrc(result, entry.Crc, entry.FileName);
    return result;
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static void VerifyCrc(byte[] data, uint expectedCrc, string fileName) {
    var actualCrc = Crc32.Compute(data);
    if (actualCrc != expectedCrc)
      throw new InvalidDataException(
        $"CRC-32 mismatch for '{fileName}': expected 0x{expectedCrc:X8}, got 0x{actualCrc:X8}.");
  }

  /// <summary>
  /// Decodes an Amiga-style packed date (32-bit LE) into a <see cref="DateTime"/>.
  /// </summary>
  /// <remarks>
  /// The packed format stores: year (bits 17-23, offset from 1970), month (bits 13-16, 1-12),
  /// day (bits 8-12, 1-31), hour (bits 3-7), minute (bits 0-5 of the next byte area).
  /// Actual layout for the Amiga LZX archiver:
  ///   bits 31-25: year (0 = 1970)
  ///   bits 24-21: month (1-12)
  ///   bits 20-16: day (1-31)
  ///   bits 15-11: hour (0-23)
  ///   bits 10-5:  minute (0-59)
  ///   bits 4-0:   second / 2 (0-29)
  /// </remarks>
  private static DateTime DecodeAmigaDate(uint packed) {
    var year = (int)((packed >> 25) & 0x7F) + 1970;
    var month = (int)((packed >> 21) & 0x0F);
    var day = (int)((packed >> 16) & 0x1F);
    var hour = (int)((packed >> 11) & 0x1F);
    var minute = (int)((packed >> 5) & 0x3F);
    var second = (int)(packed & 0x1F) * 2;

    // Clamp to valid ranges
    if (month is < 1 or > 12) month = 1;
    if (day is < 1 or > 31) day = 1;
    if (hour > 23) hour = 0;
    if (minute > 59) minute = 0;
    if (second > 59) second = 0;

    try {
      return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
    } catch (ArgumentOutOfRangeException) {
      return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
    }
  }

  internal static uint EncodeAmigaDate(DateTime dt) {
    var year = (uint)(dt.Year - 1970) & 0x7F;
    var month = (uint)dt.Month & 0x0F;
    var day = (uint)dt.Day & 0x1F;
    var hour = (uint)dt.Hour & 0x1F;
    var minute = (uint)dt.Minute & 0x3F;
    var second = (uint)(dt.Second / 2) & 0x1F;

    return (year << 25) | (month << 21) | (day << 16) |
           (hour << 11) | (minute << 5) | second;
  }
}
