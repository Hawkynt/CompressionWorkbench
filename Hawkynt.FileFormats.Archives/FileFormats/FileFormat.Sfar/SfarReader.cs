using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Sfar;

/// <summary>
/// Read-only parser for BioWare's Sirius File ARchive (Mass Effect 3 DLC) format.
/// </summary>
/// <remarks>
/// Writing SFARs requires LZX-compressed block packing plus SHA-1 and MD5 hash-table generation
/// against canonical game paths, so creation is intentionally out of scope. Per-block LZX
/// decompression is also not wired up — entries packed with <c>"lzx\0"</c> compression will throw
/// <see cref="NotSupportedException"/> when extracted.
/// </remarks>
public sealed class SfarReader : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly bool _isLzxCompressed;
  private readonly int _maxBlockSize;
  private readonly ushort[] _blockSizes;
  private bool _disposed;

  /// <summary>Maximum block size advertised by the archive header (typically 64 KiB).</summary>
  public int MaxBlockSize => this._maxBlockSize;

  /// <summary>True when the archive declares <c>"lzx\0"</c> in its compression slot.</summary>
  public bool IsLzxCompressed => this._isLzxCompressed;

  /// <summary>All entries discovered in this archive.</summary>
  public IReadOnlyList<SfarEntry> Entries { get; }

  /// <summary>
  /// Parses the archive header, entry table and block table from <paramref name="stream"/>.
  /// </summary>
  /// <param name="stream">Seekable stream positioned anywhere; absolute offsets are used.</param>
  /// <param name="leaveOpen">Whether to leave the underlying stream open on dispose.</param>
  public SfarReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("SFAR reading requires a seekable stream.", nameof(stream));

    this._stream = stream;
    this._leaveOpen = leaveOpen;

    if (stream.Length < SfarConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid SFAR archive.");

    Span<byte> header = stackalloc byte[SfarConstants.HeaderSize];
    stream.Position = 0;
    ReadExact(stream, header);

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
    if (magic != SfarConstants.Magic)
      throw new InvalidDataException($"Invalid SFAR magic: 0x{magic:X8} (expected 0x{SfarConstants.Magic:X8}).");

    // header[4..8] is Version — ignored; reader is permissive across known revisions
    var dataOffset       = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
    var entriesOffset    = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
    var fileCount        = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
    var blockTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);
    var maxBlockSize     = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);

    this._isLzxCompressed = header[28..32].SequenceEqual(SfarConstants.CompressionLzx);
    this._maxBlockSize = maxBlockSize == 0 ? SfarConstants.DefaultMaxBlockSize : (int)maxBlockSize;

    if (fileCount > int.MaxValue / SfarConstants.EntrySize)
      throw new InvalidDataException($"SFAR file count is unreasonable: {fileCount}.");

    var rawEntries = ReadEntries(stream, entriesOffset, (int)fileCount);
    this._blockSizes = ReadBlockTable(stream, blockTableOffset, rawEntries, this._maxBlockSize, dataOffset);

    var names = TryResolveFilenamesTxt(stream, rawEntries, this._blockSizes, this._isLzxCompressed, this._maxBlockSize);
    this.Entries = MaterializeEntries(rawEntries, names);
  }

  /// <summary>
  /// Decompresses (or copies, for stored blocks) the entry's payload by walking its block list.
  /// </summary>
  public byte[] Extract(SfarEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    var blockCount = (int)((entry.Size + this._maxBlockSize - 1) / this._maxBlockSize);
    var output = new byte[entry.Size];
    var written = 0;
    var blockOffset = entry.DataOffset;

    for (var i = 0; i < blockCount; ++i) {
      var blockTableSlot = entry.BlockTableIndex + i;
      if (blockTableSlot < 0 || blockTableSlot >= this._blockSizes.Length)
        throw new InvalidDataException($"Block table index {blockTableSlot} out of range (have {this._blockSizes.Length}).");

      var declared = this._blockSizes[blockTableSlot];
      var remaining = (int)Math.Min(this._maxBlockSize, entry.Size - written);

      // Stored-block sentinel: spec says block_size == MaxBlockSize means "stored, full block".
      // In practice MaxBlockSize is 0x10000 (64 KiB) which doesn't fit in a UInt16, so the
      // on-disk value wraps to 0. Reader must accept either encoding.
      var stored = declared == 0 || declared == (this._maxBlockSize & 0xFFFF);
      var onDiskSize = stored ? remaining : declared;

      this._stream.Position = blockOffset;
      var raw = new byte[onDiskSize];
      ReadExact(this._stream, raw);

      if (stored || !this._isLzxCompressed) {
        if (raw.Length != remaining)
          throw new InvalidDataException($"Stored block size mismatch (got {raw.Length}, expected {remaining}).");
        Buffer.BlockCopy(raw, 0, output, written, remaining);
      } else {
        ThrowLzxNotSupported();
      }

      written += remaining;
      blockOffset += onDiskSize;
    }

    return output;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  private readonly record struct RawEntry(byte[] Hash, int BlockTableIndex, long Size, long DataOffset);

  private static List<RawEntry> ReadEntries(Stream stream, long offset, int count) {
    stream.Position = offset;
    var buf = new byte[SfarConstants.EntrySize];
    var list = new List<RawEntry>(count);
    for (var i = 0; i < count; ++i) {
      ReadExact(stream, buf);
      var hash = buf.AsSpan(0, 16).ToArray();
      var blockTableIndex = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(16, 4));
      var size = ReadFiveByteLittleEndian(buf.AsSpan(20, 5));
      var dataOffset = ReadFiveByteLittleEndian(buf.AsSpan(25, 5));
      list.Add(new RawEntry(hash, blockTableIndex, size, dataOffset));
    }
    return list;
  }

  private static ushort[] ReadBlockTable(Stream stream, long offset, List<RawEntry> entries, int maxBlockSize, long dataOffset) {
    // Block count = sum over entries of ceil(size / maxBlockSize), but the table may extend
    // beyond the highest used slot (game tools sometimes pad). Compute the maximum slot we
    // need so we can read enough entries; trust BlockTableIndex+blockCount per entry.
    var maxSlot = 0;
    foreach (var e in entries) {
      if (e.Size <= 0) continue;
      var blocks = (int)((e.Size + maxBlockSize - 1) / maxBlockSize);
      var top = e.BlockTableIndex + blocks;
      if (top > maxSlot) maxSlot = top;
    }

    if (maxSlot == 0) return [];

    // Be tolerant of truncated archives: read up to what's actually available so we
    // can still surface entry metadata even if some block-size cells aren't present.
    // Extract() will catch out-of-range slots later with a clearer error.
    stream.Position = offset;
    var available = (int)Math.Min((long)maxSlot * 2, Math.Max(0, stream.Length - offset));
    var slotsAvailable = available / 2;
    var bytes = new byte[available];
    var read = 0;
    while (read < available) {
      var n = stream.Read(bytes, read, available - read);
      if (n == 0) break;
      read += n;
    }
    var arr = new ushort[slotsAvailable];
    for (var i = 0; i < slotsAvailable; ++i)
      arr[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));

    _ = dataOffset; // dataOffset retained in API for callers; not needed for table parsing
    return arr;
  }

  private static long ReadFiveByteLittleEndian(ReadOnlySpan<byte> bytes) {
    // SFAR uses 5-byte LE integers (max 2^40 - 1). NOT to be confused with PSARC's 5-byte BE.
    return bytes[0]
         | ((long)bytes[1] << 8)
         | ((long)bytes[2] << 16)
         | ((long)bytes[3] << 24)
         | ((long)bytes[4] << 32);
  }

  private static string?[] TryResolveFilenamesTxt(Stream stream, List<RawEntry> raw, ushort[] blockSizes,
                                                  bool isLzxCompressed, int maxBlockSize) {
    var names = new string?[raw.Count];
    if (raw.Count <= 1) return names;

    // Convention: entry 0 is "Filenames.txt" with one path per line for entries 1..N-1.
    // Some SFARs include this; many do not. Only accept the manifest if every safety check passes.
    try {
      var entry0 = raw[0];
      if (entry0.Size <= 0 || entry0.Size > 8 * 1024 * 1024) return names;

      var bytes = TryExtractStored(stream, entry0, blockSizes, isLzxCompressed, maxBlockSize);
      if (bytes == null) return names;

      // Reject anything with embedded NUL bytes — manifest is plain UTF-8 text
      if (Array.IndexOf(bytes, (byte)0) >= 0) return names;

      var text = StrictUtf8Decode(bytes);
      if (text == null) return names;

      var lines = text.Split('\n');
      // Trailing newline produces a final empty element; drop it if present
      var trimmed = lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
      if (trimmed.Length != raw.Count - 1) return names;

      for (var i = 0; i < trimmed.Length; ++i)
        names[i + 1] = trimmed[i].TrimEnd('\r');
    } catch {
      // Any parsing failure at all → fall back to synthetic names; never propagate.
    }
    return names;
  }

  private static byte[]? TryExtractStored(Stream stream, RawEntry entry, ushort[] blockSizes,
                                          bool isLzxCompressed, int maxBlockSize) {
    var blockCount = (int)((entry.Size + maxBlockSize - 1) / maxBlockSize);
    var output = new byte[entry.Size];
    var written = 0;
    var blockOffset = entry.DataOffset;

    for (var i = 0; i < blockCount; ++i) {
      var slot = entry.BlockTableIndex + i;
      if (slot < 0 || slot >= blockSizes.Length) return null;

      var declared = blockSizes[slot];
      var remaining = (int)Math.Min(maxBlockSize, entry.Size - written);
      var stored = declared == 0 || declared == (maxBlockSize & 0xFFFF);

      // If the archive is LZX-compressed AND this block is not stored, we can't read it here.
      if (!stored && isLzxCompressed) return null;

      var onDisk = stored ? remaining : declared;
      stream.Position = blockOffset;
      var raw2 = new byte[onDisk];
      ReadExact(stream, raw2);

      if (raw2.Length != remaining) return null;
      Buffer.BlockCopy(raw2, 0, output, written, remaining);

      written += remaining;
      blockOffset += onDisk;
    }
    return output;
  }

  private static string? StrictUtf8Decode(byte[] bytes) {
    try {
      var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
      return enc.GetString(bytes);
    } catch (DecoderFallbackException) {
      return null;
    }
  }

  private static List<SfarEntry> MaterializeEntries(List<RawEntry> raw, string?[] resolved) {
    var list = new List<SfarEntry>(raw.Count);
    for (var i = 0; i < raw.Count; ++i) {
      var r = raw[i];
      var name = resolved[i] ?? Convert.ToHexString(r.Hash) + ".bin";
      list.Add(new SfarEntry {
        Name = name,
        PathHash = r.Hash,
        Size = r.Size,
        BlockTableIndex = r.BlockTableIndex,
        DataOffset = r.DataOffset,
      });
    }
    return list;
  }

  private static void ReadExact(Stream stream, Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = stream.Read(buffer[total..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of SFAR stream.");
      total += read;
    }
  }

  private static void ThrowLzxNotSupported() =>
    throw new NotSupportedException(
      "SFAR per-block LZX decompression is not yet wired up. " +
      "Mass Effect 3 SFARs declared with \"lzx\\0\" compression cannot be extracted by this reader.");
}
