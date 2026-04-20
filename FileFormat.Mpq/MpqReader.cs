using System.Text;

namespace FileFormat.Mpq;

/// <summary>
/// Reads Blizzard MPQ (Mike O'Brien Pack) archives.
/// Supports v1 format. Read-only — MPQ creation is extremely complex.
/// Used by Diablo, StarCraft, Warcraft III, World of Warcraft.
/// </summary>
public sealed class MpqReader {
  /// <summary>MPQ header magic.</summary>
  public const uint HeaderMagic = 0x1A51504D; // "MPQ\x1A"
  /// <summary>User data magic.</summary>
  public const uint UserDataMagic = 0x1B51504D; // "MPQ\x1B"

  private readonly Stream _stream;
  private readonly List<MpqEntry> _entries = [];
  private readonly long _headerOffset;
  private readonly uint[] _hashTableA;
  private readonly uint[] _hashTableB;
  private readonly uint[] _hashBlockIndex;
  private readonly uint[] _blockOffsets;
  private readonly uint[] _blockCompSizes;
  private readonly uint[] _blockOrigSizes;
  private readonly uint[] _blockFlags;
  private readonly uint _sectorSize;

  public IReadOnlyList<MpqEntry> Entries => _entries;

  public MpqReader(Stream stream) {
    _stream = stream;
    using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

    // Find header (may have user data block before it)
    var magic = br.ReadUInt32();
    _headerOffset = 0;

    if (magic == UserDataMagic) {
      // User data block: skip to real header
      br.ReadUInt32(); // user data size
      var headerOffset = br.ReadUInt32();
      _headerOffset = headerOffset;
      stream.Position = headerOffset;
      magic = br.ReadUInt32();
    }

    if (magic != HeaderMagic)
      throw new InvalidDataException($"Not an MPQ file (magic: 0x{magic:X8})");

    var headerSize = br.ReadUInt32();
    var archiveSize = br.ReadUInt32();
    var formatVersion = br.ReadUInt16();
    var sectorSizeShift = br.ReadUInt16();
    var hashTableOffset = br.ReadUInt32();
    var blockTableOffset = br.ReadUInt32();
    var hashTableEntries = br.ReadUInt32();
    var blockTableEntries = br.ReadUInt32();

    _sectorSize = (uint)(512 << sectorSizeShift);

    // Read hash table
    stream.Position = _headerOffset + hashTableOffset;
    var hashData = br.ReadBytes((int)(hashTableEntries * 16));
    MpqCrypto.DecryptBlock(hashData, MpqCrypto.HashString("(hash table)", MpqCrypto.HashTypeFileKey));

    _hashTableA = new uint[hashTableEntries];
    _hashTableB = new uint[hashTableEntries];
    _hashBlockIndex = new uint[hashTableEntries];

    using (var hbr = new BinaryReader(new MemoryStream(hashData))) {
      for (var i = 0; i < hashTableEntries; i++) {
        _hashTableA[i] = hbr.ReadUInt32();
        _hashTableB[i] = hbr.ReadUInt32();
        hbr.ReadUInt16(); // locale
        hbr.ReadUInt16(); // platform
        _hashBlockIndex[i] = hbr.ReadUInt32();
      }
    }

    // Read block table
    stream.Position = _headerOffset + blockTableOffset;
    var blockData = br.ReadBytes((int)(blockTableEntries * 16));
    MpqCrypto.DecryptBlock(blockData, MpqCrypto.HashString("(block table)", MpqCrypto.HashTypeFileKey));

    _blockOffsets = new uint[blockTableEntries];
    _blockCompSizes = new uint[blockTableEntries];
    _blockOrigSizes = new uint[blockTableEntries];
    _blockFlags = new uint[blockTableEntries];

    using (var bbr = new BinaryReader(new MemoryStream(blockData))) {
      for (var i = 0; i < blockTableEntries; i++) {
        _blockOffsets[i] = bbr.ReadUInt32();
        _blockCompSizes[i] = bbr.ReadUInt32();
        _blockOrigSizes[i] = bbr.ReadUInt32();
        _blockFlags[i] = bbr.ReadUInt32();
      }
    }

    // Try to read (listfile) to get filenames
    var listfileNames = TryReadListfile();

    // Build entries from block table
    if (listfileNames != null) {
      foreach (var name in listfileNames) {
        var blockIndex = FindFile(name);
        if (blockIndex < 0 || blockIndex >= blockTableEntries) continue;
        if ((_blockFlags[blockIndex] & 0x80000000) == 0) continue;
        _entries.Add(new MpqEntry {
          FileName = name,
          OriginalSize = _blockOrigSizes[blockIndex],
          CompressedSize = _blockCompSizes[blockIndex],
          Flags = _blockFlags[blockIndex],
          FileOffset = _blockOffsets[blockIndex],
        });
      }
    }

    // Add any remaining blocks without names
    for (var i = 0; i < blockTableEntries; i++) {
      if ((_blockFlags[i] & 0x80000000) == 0) continue;
      var alreadyListed = false;
      foreach (var e in _entries) {
        if (e.FileOffset == _blockOffsets[i]) { alreadyListed = true; break; }
      }
      if (!alreadyListed) {
        _entries.Add(new MpqEntry {
          FileName = $"File{i:D5}",
          OriginalSize = _blockOrigSizes[i],
          CompressedSize = _blockCompSizes[i],
          Flags = _blockFlags[i],
          FileOffset = _blockOffsets[i],
        });
      }
    }
  }

  /// <summary>Extracts a file by entry.</summary>
  public byte[] Extract(MpqEntry entry) {
    if (entry.IsEncrypted)
      throw new NotSupportedException("Encrypted MPQ files are not supported.");

    _stream.Position = _headerOffset + entry.FileOffset;

    if (!entry.IsCompressed) {
      var data = new byte[entry.OriginalSize];
      _stream.ReadExactly(data);
      return data;
    }

    if (entry.IsSingleUnit) {
      var compData = new byte[entry.CompressedSize];
      _stream.ReadExactly(compData);
      return DecompressMpqBlock(compData, (int)entry.OriginalSize);
    }

    // Sector-based decompression
    var numSectors = (int)((entry.OriginalSize + _sectorSize - 1) / _sectorSize);
    using var br = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);

    // Read sector offset table
    var sectorOffsets = new uint[numSectors + 1];
    for (var i = 0; i <= numSectors; i++)
      sectorOffsets[i] = br.ReadUInt32();

    using var output = new MemoryStream();
    for (var i = 0; i < numSectors; i++) {
      var sectorStart = _headerOffset + entry.FileOffset + sectorOffsets[i];
      var sectorLen = (int)(sectorOffsets[i + 1] - sectorOffsets[i]);
      var expectedSize = (int)Math.Min(_sectorSize, entry.OriginalSize - i * (long)_sectorSize);

      _stream.Position = sectorStart;
      var sectorData = br.ReadBytes(sectorLen);

      if (sectorLen < expectedSize) {
        var decompressed = DecompressMpqBlock(sectorData, expectedSize);
        output.Write(decompressed);
      } else {
        output.Write(sectorData);
      }
    }

    return output.ToArray();
  }

  private static byte[] DecompressMpqBlock(byte[] data, int expectedSize) {
    if (data.Length == 0) return [];
    if (data.Length >= expectedSize) return data[..expectedSize];

    var method = data[0];

    // zlib (0x02): data[1..] is a zlib-wrapped deflate stream
    if ((method & 0x02) != 0) {
      try {
        return FileFormat.Zlib.ZlibStream.Decompress(data.AsSpan(1));
      } catch {
        // Fallback: try raw deflate (skip method byte only)
        return Compression.Core.Deflate.DeflateDecompressor.Decompress(data.AsSpan(1));
      }
    }

    // bzip2 (0x10) — not implemented, return payload raw
    if ((method & 0x10) != 0)
      return data[1..];

    // PKWare DCL / Huffman / LZMA / other — return raw data as fallback
    return data;
  }

  private string[]? TryReadListfile() {
    var blockIndex = FindFile("(listfile)");
    if (blockIndex < 0 || blockIndex >= _blockOffsets.Length) return null;
    if ((_blockFlags[blockIndex] & 0x80000000) == 0) return null;

    var tempEntry = new MpqEntry {
      FileName = "(listfile)",
      OriginalSize = _blockOrigSizes[blockIndex],
      CompressedSize = _blockCompSizes[blockIndex],
      Flags = _blockFlags[blockIndex],
      FileOffset = _blockOffsets[blockIndex],
    };

    try {
      var data = Extract(tempEntry);
      var text = Encoding.UTF8.GetString(data);
      return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    } catch {
      return null;
    }
  }

  private int FindFile(string filename) {
    var hashA = MpqCrypto.HashString(filename, MpqCrypto.HashTypeNameA);
    var hashB = MpqCrypto.HashString(filename, MpqCrypto.HashTypeNameB);
    var start = MpqCrypto.HashString(filename, MpqCrypto.HashTypeOffset) % (uint)_hashTableA.Length;

    for (var i = start; ; ) {
      if (_hashBlockIndex[i] == 0xFFFFFFFF) return -1; // empty slot
      if (_hashTableA[i] == hashA && _hashTableB[i] == hashB)
        return (int)_hashBlockIndex[i];
      i = (i + 1) % (uint)_hashTableA.Length;
      if (i == start) return -1;
    }
  }
}
