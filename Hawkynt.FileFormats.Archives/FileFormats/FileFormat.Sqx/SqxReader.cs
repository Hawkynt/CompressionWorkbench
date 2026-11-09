using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Sqx;

namespace FileFormat.Sqx;

/// <summary>
/// Reads SQX archives (formats 1.1 and 2.0) as Squeez-c (MIT) reads them: main, file and
/// directory blocks with their header checksums, stored and LZ77 + Huffman (methods 1-4) files,
/// solid chains and self-extracting archives whose stub precedes the main block. Audio (5),
/// text (6) and ultra (7) files, encryption and split files are refused.
/// </summary>
public sealed class SqxReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<SqxEntry> _entries = [];
  private readonly Dictionary<SqxEntry, byte[]> _solidCache = [];
  private bool _disposed;

  /// <summary>Gets the entries in the archive, in archive order.</summary>
  public IReadOnlyList<SqxEntry> Entries => this._entries;

  /// <summary>Whether the main block marks the archive solid.</summary>
  public bool IsSolid { get; private set; }

  /// <summary>Opens an SQX archive.</summary>
  /// <exception cref="InvalidDataException">No main block, or a block whose checksum fails.</exception>
  public SqxReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this.ReadArchive();
  }

  /// <summary>Extracts one file, decoding the files before it when it continues a solid chain.</summary>
  /// <exception cref="NotSupportedException">The file uses a method, encryption or splitting this reader does not handle.</exception>
  /// <exception cref="InvalidDataException">The stream is malformed or fails its CRC.</exception>
  public byte[] ExtractEntry(SqxEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      return [];
    if (this._solidCache.TryGetValue(entry, out var cached))
      return cached;
    if (!entry.IsSolid)
      return this.Decode(entry, null);

    // Decode the chain from its first non-solid file.
    var index = this._entries.IndexOf(entry);
    var start = index;
    while (start > 0 && (this._entries[start].IsDirectory || this._entries[start].IsSolid))
      --start;
    SqxLzDecoder? decoder = null;
    byte[] result = [];
    for (var i = start; i <= index; ++i) {
      var e = this._entries[i];
      if (e.IsDirectory)
        continue;
      decoder ??= new SqxLzDecoder(e.DictionarySize);
      result = this.Decode(e, decoder);
      if (i < index)
        this._solidCache[e] = result;
    }

    return result;
  }

  /// <summary>Extracts every file in archive order (directories yield empty arrays).</summary>
  public byte[][] ExtractAll() => [.. this._entries.Select(this.ExtractEntry)];

  private byte[] Decode(SqxEntry entry, SqxLzDecoder? solidDecoder) {
    if (entry.IsEncrypted)
      throw new NotSupportedException($"SQX: '{entry.FileName}' is encrypted, which is not supported.");
    if (entry.IsSplit)
      throw new NotSupportedException($"SQX: '{entry.FileName}' is split across volumes, which is not supported.");
    if (entry.CompressedSize > int.MaxValue || entry.OriginalSize > int.MaxValue)
      throw new NotSupportedException($"SQX: '{entry.FileName}' is larger than this reader handles.");

    this._stream.Position = entry.DataOffset;
    var packed = new byte[entry.CompressedSize];
    this._stream.ReadExactly(packed);

    var data = entry.Method switch {
      0 => entry.CompressedSize == entry.OriginalSize ? packed : throw new InvalidDataException($"SQX: stored '{entry.FileName}' has mismatched sizes."),
      >= 1 and <= 4 => entry.OriginalSize == 0
        ? []
        : (solidDecoder ?? new SqxLzDecoder(entry.DictionarySize)).Decode(packed, entry.OriginalSize, entry.IsSolid && solidDecoder != null, (entry.CompFlags & 1) != 0, entry.ArchiveVersion),
      5 => throw new NotSupportedException($"SQX: '{entry.FileName}' uses method 5 (audio), which is not supported."),
      6 => throw new NotSupportedException($"SQX: '{entry.FileName}' uses method 6 (text, PPMd), which is not supported."),
      7 => throw new NotSupportedException($"SQX: '{entry.FileName}' uses method 7 (ultra), which is not supported."),
      _ => throw new InvalidDataException($"SQX: '{entry.FileName}' uses unknown method {entry.Method}."),
    };

    var crc = SqxContainer.RawCrc32(data);
    if (crc != entry.Crc32)
      throw new InvalidDataException($"SQX: CRC-32 mismatch for '{entry.FileName}': expected 0x{entry.Crc32:X8}, computed 0x{crc:X8}.");
    return data;
  }

  private void ReadArchive() {
    var length = this._stream.Length;
    var data = new byte[length];
    this._stream.Position = 0;
    this._stream.ReadExactly(data);

    var position = FindMainBlock(data);
    if (position < 0)
      throw new InvalidDataException("Not an SQX archive: no main block.");

    while (position + SqxContainer.HeaderSize <= data.Length) {
      var crc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
      var type = data[position + 2];
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 3));
      var size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 5));
      if (size < SqxContainer.HeaderSize || position + size > data.Length)
        throw new InvalidDataException($"SQX block at {position} has an invalid size.");
      var header = data.AsSpan(position, size);
      if (SqxContainer.BlockCrc(header[2..]) != crc)
        throw new InvalidDataException($"SQX block at {position} fails its header checksum.");

      switch (type) {
        case SqxContainer.BlockMain:
          this.IsSolid = (flags & 0x0004) != 0;
          position += size;
          break;
        case SqxContainer.BlockFile:
          position = this.ReadFile(data, position, flags, header);
          break;
        case SqxContainer.BlockDirectory:
          this.ReadDirectory(header, flags);
          position += size;
          break;
        case SqxContainer.BlockEnd:
          return;
        default:
          // Comments, recovery and other records: header only, nothing extracted.
          position += size;
          break;
      }
    }

    throw new InvalidDataException("SQX archive ends without an end block.");
  }

  private int ReadFile(byte[] data, int position, ushort flags, ReadOnlySpan<byte> header) {
    var p = header[SqxContainer.HeaderSize..];
    if (p.Length < 28)
      throw new InvalidDataException("SQX file header is too short.");
    long compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(p[18..]);
    long originalSize = BinaryPrimitives.ReadUInt32LittleEndian(p[22..]);
    var at = 26;
    if ((flags & 0x0080) != 0) {
      if (p.Length < at + 10)
        throw new InvalidDataException("SQX file header is too short for 64-bit sizes.");
      compressedSize |= (long)BinaryPrimitives.ReadUInt32LittleEndian(p[at..]) << 32;
      originalSize |= (long)BinaryPrimitives.ReadUInt32LittleEndian(p[(at + 4)..]) << 32;
      at += 8;
    }

    var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(p[at..]);
    at += 2;
    if (p.Length < at + nameLength)
      throw new InvalidDataException("SQX file name runs past its header.");
    var dataOffset = position + header.Length;
    if (dataOffset + compressedSize > data.Length)
      throw new InvalidDataException("SQX file data runs past the end of the archive.");

    this._entries.Add(new SqxEntry {
      FileName = Name(p.Slice(at, nameLength)),
      CompFlags = p[0],
      ArchiveVersion = p[4],
      Method = p[5],
      Crc32 = BinaryPrimitives.ReadUInt32LittleEndian(p[6..]),
      Attributes = BinaryPrimitives.ReadUInt32LittleEndian(p[10..]),
      LastModified = FromDos(BinaryPrimitives.ReadUInt32LittleEndian(p[14..])),
      CompressedSize = compressedSize,
      OriginalSize = originalSize,
      Flags = flags,
      DataOffset = dataOffset,
    });
    return (int)(dataOffset + compressedSize);
  }

  private void ReadDirectory(ReadOnlySpan<byte> header, ushort flags) {
    var p = header[SqxContainer.HeaderSize..];
    if (p.Length < 10)
      throw new InvalidDataException("SQX directory header is too short.");
    var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(p[8..]);
    if (p.Length < 10 + nameLength)
      throw new InvalidDataException("SQX directory name runs past its header.");
    this._entries.Add(new SqxEntry {
      FileName = Name(p.Slice(10, nameLength)),
      IsDirectory = true,
      Attributes = BinaryPrimitives.ReadUInt32LittleEndian(p),
      LastModified = FromDos(BinaryPrimitives.ReadUInt32LittleEndian(p[4..])),
      Flags = flags,
    });
  }

  private static string Name(ReadOnlySpan<byte> raw) => Encoding.Latin1.GetString(raw).Replace('\\', '/');

  /// <summary>The main block's offset: 0, or after a self-extractor stub, the first <c>-sqx-</c> whose block checksum holds.</summary>
  private static int FindMainBlock(byte[] data) {
    for (var i = 0; i + SqxContainer.MainBlockSize <= data.Length; ++i) {
      var at = data.AsSpan(i + 7).IndexOf(SqxContainer.Signature);
      if (at < 0)
        return -1;
      i += at;
      if (i + SqxContainer.MainBlockSize > data.Length)
        return -1;
      if (data[i + 2] != SqxContainer.BlockMain)
        continue;
      var size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 5));
      if (size < SqxContainer.HeaderSize + 5 || i + size > data.Length)
        continue;
      if (SqxContainer.BlockCrc(data.AsSpan(i + 2, size - 2)) == BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i)))
        return i;
    }

    return -1;
  }

  private static DateTime FromDos(uint timestamp) {
    if (timestamp == 0)
      return DateTime.MinValue;
    var time = (int)(timestamp & 0xFFFF);
    var date = (int)(timestamp >> 16);
    try {
      return new DateTime(((date >> 9) & 0x7F) + 1980, Math.Max((date >> 5) & 0x0F, 1), Math.Max(date & 0x1F, 1),
        (time >> 11) & 0x1F, (time >> 5) & 0x3F, (time & 0x1F) * 2);
    } catch (ArgumentOutOfRangeException) {
      return DateTime.MinValue;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
