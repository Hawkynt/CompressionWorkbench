using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Mpq;

/// <summary>
/// Reads Blizzard MPQ (Mike O'Brien Pack) archives: format versions 1 and 2 (and the classic
/// tables of 3 and 4), files stored, compressed with any MPQ compression mask, PKWARE-imploded,
/// encrypted (with or without the adjusted key), single-unit or split into sectors, with or
/// without sector checksums. Used by Diablo, StarCraft, Warcraft III and World of Warcraft.
/// </summary>
/// <remarks>
/// Layout and behaviour follow Ladislav Zezula's MPQ documentation and StormLib (MIT), which
/// also serves as the oracle: archives StormLib writes extract here to the bytes StormLib
/// extracts. Where StormLib tolerates damage by filling zeros, this reader refuses.
/// </remarks>
public sealed class MpqReader {
  /// <summary>MPQ header magic.</summary>
  public const uint HeaderMagic = 0x1A51504D; // "MPQ\x1A"
  /// <summary>User data magic.</summary>
  public const uint UserDataMagic = 0x1B51504D; // "MPQ\x1B"

  private const uint HashFree = 0xFFFFFFFF;
  private const uint HashDeleted = 0xFFFFFFFE;
  private const string ListfileName = "(listfile)";
  private static readonly string[] InternalNames = ["(listfile)", "(attributes)", "(signature)", "(user data)", "(patch_metadata)"];

  private readonly Stream _stream;
  private readonly List<MpqEntry> _entries = [];
  private readonly long _headerOffset;
  private readonly int _formatVersion;
  private readonly uint _sectorSize;
  private readonly HashSlot[] _hashes;
  private readonly BlockRecord[] _blocks;
  private uint[]? _attributeCrc32;

  private readonly record struct HashSlot(uint NameA, uint NameB, ushort Locale, uint BlockIndex);
  private readonly record struct BlockRecord(long Offset, uint CompressedSize, uint OriginalSize, uint Flags);

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<MpqEntry> Entries => _entries;

  /// <summary>Gets the byte offset of the MPQ header within the stream.</summary>
  public long HeaderOffset => _headerOffset;

  /// <summary>Gets the header's format version (0 = MPQ v1, 1 = v2, ...).</summary>
  public int FormatVersion => _formatVersion;

  /// <summary>Gets the sector size in bytes.</summary>
  public int SectorSize => (int)_sectorSize;

  /// <summary>
  /// Initializes a new instance of <see cref="MpqReader"/>.
  /// </summary>
  /// <exception cref="InvalidDataException">No MPQ header is found or its tables lie outside the stream.</exception>
  /// <exception cref="NotSupportedException">The archive keeps its file table only in HET/BET form.</exception>
  public MpqReader(Stream stream) {
    _stream = stream;
    _headerOffset = FindHeader(stream);

    var header = new byte[0x2C];
    stream.Position = _headerOffset;
    var headerRead = stream.ReadAtLeast(header, 0x20, throwOnEndOfStream: false);
    if (headerRead < 0x20)
      throw new InvalidDataException("MPQ header is truncated.");

    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
    var formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
    var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
    long hashTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
    long blockTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
    var hashTableEntries = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
    var blockTableEntries = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28));

    // A version-2+ header must be long enough to carry the 64-bit fields; anything else
    // (notably protected Warcraft III maps) is read as version 1, as Storm does.
    long hiBlockTableOffset = 0;
    if (formatVersion >= 1 && headerSize >= 0x2C && headerRead >= 0x2C) {
      _formatVersion = formatVersion;
      hiBlockTableOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x20));
      hashTableOffset |= (long)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x28)) << 32;
      blockTableOffset |= (long)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x2A)) << 32;
    }

    if (sectorShift > 23)
      throw new InvalidDataException($"MPQ sector size shift {sectorShift} is implausible.");
    _sectorSize = 512u << sectorShift;

    if (hashTableEntries == 0 && _formatVersion >= 2)
      throw new NotSupportedException("MPQ archives that keep their file table only as HET/BET tables are not supported.");

    var hashBytes = ReadTable(hashTableOffset, hashTableEntries, MpqCrypto.HashTableKey);
    _hashes = new HashSlot[hashBytes.Length / 16];
    for (var i = 0; i < _hashes.Length; ++i) {
      var s = hashBytes.AsSpan(i * 16, 16);
      _hashes[i] = new(
        BinaryPrimitives.ReadUInt32LittleEndian(s),
        BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
        BinaryPrimitives.ReadUInt16LittleEndian(s[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(s[12..]));
    }

    var blockBytes = ReadTable(blockTableOffset, blockTableEntries, MpqCrypto.BlockTableKey);
    var hiBlocks = new ushort[blockBytes.Length / 16];
    if (hiBlockTableOffset != 0) {
      var raw = ReadRaw(hiBlockTableOffset, hiBlocks.Length * 2L);
      for (var i = 0; i < raw.Length / 2; ++i)
        hiBlocks[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2));
    }
    _blocks = new BlockRecord[blockBytes.Length / 16];
    for (var i = 0; i < _blocks.Length; ++i) {
      var s = blockBytes.AsSpan(i * 16, 16);
      _blocks[i] = new(
        BinaryPrimitives.ReadUInt32LittleEndian(s) | ((long)hiBlocks[i] << 32),
        BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(s[12..]));
    }

    var names = ReadNames();
    for (var slot = 0; slot < _hashes.Length; ++slot) {
      var hash = _hashes[slot];
      if (hash.BlockIndex >= (uint)_blocks.Length)
        continue;
      var block = _blocks[hash.BlockIndex];
      if ((block.Flags & 0x80000000) == 0)
        continue;
      names.TryGetValue((hash.NameA, hash.NameB), out var nameBytes);
      _entries.Add(new MpqEntry {
        FileName = nameBytes != null ? DecodeName(nameBytes) : $"File{hash.BlockIndex:D5}",
        NameBytes = nameBytes,
        IsNameKnown = nameBytes != null,
        Locale = hash.Locale,
        OriginalSize = block.OriginalSize,
        CompressedSize = block.CompressedSize,
        Flags = block.Flags,
        FileOffset = block.Offset,
        BlockIndex = (int)hash.BlockIndex,
      });
    }

    _attributeCrc32 = ReadAttributeChecksums();
  }

  /// <summary>Extracts a file by entry.</summary>
  /// <exception cref="NotSupportedException">The entry is a patch file, or encrypted without a recoverable key.</exception>
  /// <exception cref="InvalidDataException">The entry's data is damaged.</exception>
  public byte[] Extract(MpqEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var lossy = false;
    var data = ExtractCore(entry, ref lossy);
    // (attributes) carries the CRC-32 of each file's original bytes; ADPCM is lossy, so those
    // files cannot match it and are not checked (StormLib reports them as mismatches too).
    if (!lossy && _attributeCrc32 != null && entry.BlockIndex >= 0 && entry.BlockIndex < _attributeCrc32.Length) {
      var expected = _attributeCrc32[entry.BlockIndex];
      if (expected != 0 && Compression.Core.Checksums.Crc32.Compute(data) != expected)
        throw new InvalidDataException($"MPQ file '{entry.FileName}' does not match the CRC-32 recorded in (attributes).");
    }
    return data;
  }

  private byte[] ExtractCore(MpqEntry entry, ref bool lossy) {
    if (entry.IsPatchFile)
      throw new NotSupportedException($"MPQ file '{entry.FileName}' is an incremental patch, not file content.");
    if (entry.IsDeleteMarker || entry.OriginalSize == 0)
      return [];
    if (entry.OriginalSize > int.MaxValue)
      throw new NotSupportedException($"MPQ file '{entry.FileName}' is larger than 2 GiB.");

    var originalSize = (int)entry.OriginalSize;
    var start = _headerOffset + entry.FileOffset;
    var compressedMask = entry.Flags & 0x0000FF00;
    var key = entry.IsEncrypted ? FileKey(entry) : 0;

    if (entry.IsSingleUnit)
      return this.ExtractSingleUnit(entry, start, originalSize, ref key, ref lossy);

    var sectorCount = (int)((entry.OriginalSize + _sectorSize - 1) / _sectorSize);
    var output = new byte[originalSize];

    if (compressedMask == 0) {
      // Stored: sectors follow each other at sector size, each encrypted with key + index.
      var raw = ReadRaw(start, originalSize);
      if (raw.Length < originalSize)
        throw new InvalidDataException($"MPQ file '{entry.FileName}' is truncated.");
      if (entry.IsEncrypted) {
        if (key == 0)
          key = MpqCrypto.DetectKeyByContent(raw, entry.OriginalSize > uint.MaxValue ? 0 : (uint)entry.OriginalSize);
        RequireKey(entry, key);
        for (var i = 0; i < sectorCount; ++i) {
          var length = (int)Math.Min(_sectorSize, originalSize - (long)i * _sectorSize);
          MpqCrypto.DecryptBlock(raw.AsSpan((int)(i * _sectorSize), length), unchecked(key + (uint)i));
        }
      }
      return raw;
    }

    var tableEntries = sectorCount + 1 + (entry.HasSectorChecksums ? 1 : 0);
    var table = ReadRaw(start, tableEntries * 4L);
    if (table.Length < tableEntries * 4)
      throw new InvalidDataException($"MPQ file '{entry.FileName}' has a truncated sector table.");
    if (entry.IsEncrypted) {
      if (key == 0)
        key = MpqCrypto.DetectKeyBySectorTable(table, (uint)(tableEntries * 4), _sectorSize);
      RequireKey(entry, key);
      MpqCrypto.DecryptBlock(table, unchecked(key - 1));
    }
    var offsets = new uint[tableEntries];
    for (var i = 0; i < tableEntries; ++i)
      offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 4));
    for (var i = 0; i < sectorCount; ++i)
      if (offsets[i + 1] < offsets[i] || offsets[i + 1] - offsets[i] > _sectorSize)
        throw new InvalidDataException($"MPQ file '{entry.FileName}' has a corrupt sector table.");

    var checksums = entry.HasSectorChecksums ? this.ReadSectorChecksums(start, offsets, sectorCount) : null;

    for (var i = 0; i < sectorCount; ++i) {
      var expected = (int)Math.Min(_sectorSize, originalSize - (long)i * _sectorSize);
      var sector = ReadRaw(start + offsets[i], offsets[i + 1] - offsets[i]);
      if (sector.Length < offsets[i + 1] - offsets[i])
        throw new InvalidDataException($"MPQ file '{entry.FileName}' sector {i} is truncated.");
      if (entry.IsEncrypted)
        MpqCrypto.DecryptBlock(sector, unchecked(key + (uint)i));
      if (checksums != null && checksums[i] != 0 && checksums[i] != 0xFFFFFFFF
          && SectorAdler32(sector) != checksums[i])
        throw new InvalidDataException($"MPQ file '{entry.FileName}' sector {i} fails its Adler-32 checksum.");
      DecodeSector(entry, sector, expected, ref lossy).CopyTo(output, (long)i * _sectorSize);
    }
    return output;
  }

  private byte[] ExtractSingleUnit(MpqEntry entry, long start, int originalSize, ref uint key, ref bool lossy) {
    var raw = ReadRaw(start, entry.CompressedSize);
    if (raw.Length < entry.CompressedSize)
      throw new InvalidDataException($"MPQ file '{entry.FileName}' is truncated.");
    if (entry.IsEncrypted) {
      if (key == 0)
        key = MpqCrypto.DetectKeyByContent(raw, (uint)originalSize);
      RequireKey(entry, key);
      MpqCrypto.DecryptBlock(raw, key);
    }
    if ((entry.Flags & 0x0000FF00) == 0) {
      if (raw.Length < originalSize)
        throw new InvalidDataException($"MPQ file '{entry.FileName}' is truncated.");
      return raw[..originalSize];
    }
    return DecodeSector(entry, raw, originalSize, ref lossy);
  }

  /// <summary>Decodes one sector (or a whole single-unit file) that is stored, compressed or imploded.</summary>
  private byte[] DecodeSector(MpqEntry entry, byte[] sector, int expected, ref bool lossy) {
    if (sector.Length >= expected)
      return sector.Length == expected ? sector : sector[..expected];
    if (!entry.IsImploded && (sector[0] & (MpqCompression.AdpcmMono | MpqCompression.AdpcmStereo)) != 0)
      lossy = true;
    if (entry.IsImploded)
      return Compression.Core.Dictionary.PkwareDcl.PkwareDclDecoder.Decompress(sector, expected);
    return MpqCompression.Decompress(sector, expected, _formatVersion);
  }

  /// <summary>
  /// The CRC-32 column of "(attributes)", indexed by block, when the file is present, readable
  /// and of one of the sizes Storm writes (all blocks, or all but its own); otherwise null.
  /// </summary>
  private uint[]? ReadAttributeChecksums() {
    var entry = _entries.FirstOrDefault(e => e.IsNameKnown && e.FileName == "(attributes)");
    if (entry == null)
      return null;
    byte[] data;
    var ignored = false;
    try {
      data = this.ExtractCore(entry, ref ignored);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      return null;
    }
    if (data.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 100)
      return null;
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
    if ((flags & 1) == 0 || (flags & ~0xFu) != 0)
      return null;
    foreach (var count in new[] { _blocks.Length, _blocks.Length - 1 }) {
      if (count <= 0)
        continue;
      long size = 8 + 4L * count;
      if ((flags & 2) != 0) size += 8L * count;
      if ((flags & 4) != 0) size += 16L * count;
      var withBits = size + ((flags & 8) != 0 ? (_blocks.Length + 6) / 8 : 0);
      if (data.Length != withBits && data.Length != size)
        continue;
      var crcs = new uint[count];
      for (var i = 0; i < count; ++i)
        crcs[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8 + i * 4));
      return crcs;
    }
    return null;
  }

  private uint[]? ReadSectorChecksums(long start, uint[] offsets, int sectorCount) {
    // The table only exists when the offset table has exactly one extra entry; its size is
    // then the gap between the last two offsets, and it is itself a compressed block.
    var tableStart = offsets[sectorCount];
    var tableEnd = offsets[sectorCount + 1];
    if (offsets[0] != (sectorCount + 2) * 4u || tableEnd <= tableStart)
      return null;
    var stored = tableEnd - tableStart;
    var size = sectorCount * 4;
    if (stored < 4 || stored > _sectorSize)
      return null;
    var raw = ReadRaw(start + tableStart, stored);
    byte[] decoded;
    try {
      decoded = raw.Length >= size ? raw[..size] : MpqCompression.Decompress(raw, size, _formatVersion);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) {
      return null; // like StormLib, an unreadable checksum table is ignored rather than trusted
    }
    var result = new uint[sectorCount];
    for (var i = 0; i < sectorCount; ++i)
      result[i] = BinaryPrimitives.ReadUInt32LittleEndian(decoded.AsSpan(i * 4));
    return result;
  }

  /// <summary>Adler-32 as Storm computes sector checksums: zlib's adler32 started from 0, not 1.</summary>
  internal static uint SectorAdler32(ReadOnlySpan<byte> data) {
    uint a = 0, b = 0;
    foreach (var value in data) {
      a = (a + value) % 65521;
      b = (b + a) % 65521;
    }
    return (b << 16) | a;
  }

  private static void RequireKey(MpqEntry entry, uint key) {
    if (key == 0)
      throw new NotSupportedException($"MPQ file '{entry.FileName}' is encrypted and its name is unknown; the key could not be recovered.");
  }

  private static uint FileKey(MpqEntry entry)
    => entry.NameBytes == null
      ? 0
      : MpqCrypto.FileKey(entry.NameBytes, (uint)entry.FileOffset, (uint)entry.OriginalSize, entry.Flags);

  /// <summary>
  /// Decodes one MPQ block (a sector, or a whole single-unit file) of a version-1 archive. A block
  /// no shorter than its expected size is stored; otherwise its first byte is the compression mask.
  /// </summary>
  /// <exception cref="NotSupportedException">The mask names an unknown method.</exception>
  /// <exception cref="InvalidDataException">The compressed block is damaged or decodes to the wrong size.</exception>
  internal static byte[] DecompressMpqBlock(byte[] data, int expectedSize) {
    if (data.Length == 0) return [];
    if (data.Length >= expectedSize) return data[..expectedSize];
    return MpqCompression.Decompress(data, expectedSize, 0);
  }

  private static long FindHeader(Stream stream) {
    var buffer = new byte[16];
    for (long offset = 0; offset + 32 <= stream.Length; offset += 0x200) {
      stream.Position = offset;
      if (stream.ReadAtLeast(buffer, 16, throwOnEndOfStream: false) < 16)
        break;
      var magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
      if (magic == HeaderMagic)
        return offset;
      if (magic != UserDataMagic)
        continue;
      var target = offset + BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8));
      if (target + 32 > stream.Length)
        continue;
      stream.Position = target;
      if (stream.ReadAtLeast(buffer, 4, throwOnEndOfStream: false) >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(buffer) == HeaderMagic)
        return target;
    }
    stream.Position = 0;
    var head = new byte[4];
    var got = stream.ReadAtLeast(head, 4, throwOnEndOfStream: false);
    throw new InvalidDataException(got < 4 ? "Not an MPQ file (too short)." : $"Not an MPQ file (magic: 0x{BinaryPrimitives.ReadUInt32LittleEndian(head):X8})");
  }

  /// <summary>Reads and decrypts a hash or block table, keeping only entries that lie inside the stream.</summary>
  private byte[] ReadTable(long relativeOffset, uint entries, uint key) {
    var bytes = ReadRaw(relativeOffset, Math.Min(entries, 0x8000000u) * 16L, relative: true);
    var whole = bytes.Length / 16 * 16;
    var table = bytes[..whole];
    MpqCrypto.DecryptBlock(table, key);
    return table;
  }

  private byte[] ReadRaw(long offset, long length, bool relative = false) {
    var absolute = relative ? _headerOffset + offset : offset;
    if (absolute < 0 || absolute >= _stream.Length || length <= 0)
      return [];
    var available = Math.Min(length, _stream.Length - absolute);
    var data = new byte[available];
    _stream.Position = absolute;
    _stream.ReadExactly(data);
    return data;
  }

  /// <summary>Maps (hash A, hash B) to name bytes for every name in the listfile plus the internal names.</summary>
  private Dictionary<(uint, uint), byte[]> ReadNames() {
    var names = new Dictionary<(uint, uint), byte[]>();
    void Add(byte[] name) {
      if (name.Length == 0) return;
      names.TryAdd((MpqCrypto.HashString(name, MpqCrypto.HashTypeNameA), MpqCrypto.HashString(name, MpqCrypto.HashTypeNameB)), name);
    }
    foreach (var internalName in InternalNames)
      Add(Encoding.ASCII.GetBytes(internalName));

    var listfile = this.FindSlot(Encoding.ASCII.GetBytes(ListfileName));
    if (listfile < 0)
      return names;
    var hash = _hashes[listfile];
    if (hash.BlockIndex >= (uint)_blocks.Length)
      return names;
    var block = _blocks[hash.BlockIndex];
    byte[] data;
    try {
      data = this.Extract(new MpqEntry {
        FileName = ListfileName,
        NameBytes = Encoding.ASCII.GetBytes(ListfileName),
        OriginalSize = block.OriginalSize,
        CompressedSize = block.CompressedSize,
        Flags = block.Flags,
        FileOffset = block.Offset,
      });
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      return names;
    }

    var start = 0;
    for (var i = 0; i <= data.Length; ++i) {
      if (i < data.Length && data[i] is not ((byte)'\r' or (byte)'\n' or (byte)';' or 0))
        continue;
      Add(data[start..i]);
      start = i + 1;
    }
    return names;
  }

  private int FindSlot(byte[] name) {
    if (_hashes.Length == 0) return -1;
    var hashA = MpqCrypto.HashString(name, MpqCrypto.HashTypeNameA);
    var hashB = MpqCrypto.HashString(name, MpqCrypto.HashTypeNameB);
    var start = (int)(MpqCrypto.HashString(name, MpqCrypto.HashTypeOffset) % (uint)_hashes.Length);
    for (var n = 0; n < _hashes.Length; ++n) {
      var i = (start + n) % _hashes.Length;
      var slot = _hashes[i];
      if (slot.BlockIndex == HashFree) return -1;
      if (slot.BlockIndex != HashDeleted && slot.NameA == hashA && slot.NameB == hashB) return i;
    }
    return -1;
  }

  private static string DecodeName(byte[] name) {
    try {
      return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(name);
    } catch (DecoderFallbackException) {
      return Encoding.Latin1.GetString(name);
    }
  }
}
