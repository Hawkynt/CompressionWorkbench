using System.Text;

namespace FileFormat.Bsa;

/// <summary>
/// Reads Bethesda Softworks Archive (BSA) files.
/// Supports TES3 (Morrowind), TES4/FO3/SSE (Oblivion through Skyrim SE), and BA2 (Fallout 4/76).
/// </summary>
public sealed class BsaReader {
  private readonly Stream _stream;
  private readonly List<BsaEntry> _entries = [];
  private readonly BsaFormat _format;

  public IReadOnlyList<BsaEntry> Entries => _entries;
  public BsaFormat Format => _format;

  public enum BsaFormat { Tes3, Tes4, Ba2 }

  public BsaReader(Stream stream) {
    _stream = stream;
    using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
    var magic = br.ReadUInt32();

    if (magic == 0x00000100) {
      _format = BsaFormat.Tes3;
      ReadTes3(br);
    } else if (magic == 0x00415342) { // "BSA\0"
      _format = BsaFormat.Tes4;
      ReadTes4(br);
    } else if (magic == 0x58445442) { // "BTDX"
      _format = BsaFormat.Ba2;
      ReadBa2(br);
    } else {
      throw new InvalidDataException($"Unknown BSA magic: 0x{magic:X8}");
    }
  }

  private void ReadTes3(BinaryReader br) {
    var hashTableOffset = br.ReadUInt32();
    var fileCount = br.ReadInt32();
    // Read file sizes and offsets
    var sizes = new uint[fileCount];
    var offsets = new uint[fileCount];
    for (var i = 0; i < fileCount; i++) {
      sizes[i] = br.ReadUInt32();
      offsets[i] = br.ReadUInt32();
    }
    // Read name offsets
    var nameOffsets = new uint[fileCount];
    for (var i = 0; i < fileCount; i++)
      nameOffsets[i] = br.ReadUInt32();
    // Read names
    var nameBytes = br.ReadBytes((int)(hashTableOffset - (_stream.Position - 12)));
    // Parse names from the name table
    var names = new string[fileCount];
    for (var i = 0; i < fileCount; i++) {
      var end = (int)nameOffsets[i];
      while (end < nameBytes.Length && nameBytes[end] != 0) end++;
      names[i] = Encoding.ASCII.GetString(nameBytes, (int)nameOffsets[i], end - (int)nameOffsets[i]);
    }
    // Data starts after header (12) + size/offset pairs + name offsets + names + hash table
    // offsets in TES3 are relative to the data section start; we store them as-is and resolve on extract
    for (var i = 0; i < fileCount; i++) {
      var path = names[i].Replace('/', '\\');
      var folder = Path.GetDirectoryName(path) ?? "";
      var file = Path.GetFileName(path);
      _entries.Add(new BsaEntry {
        FileName = file,
        FolderPath = folder,
        OriginalSize = sizes[i],
        CompressedSize = sizes[i],
        IsCompressed = false,
        Offset = offsets[i],
      });
    }
  }

  private void ReadTes4(BinaryReader br) {
    var version = br.ReadInt32(); // 103=Oblivion, 104=FO3/NV, 105=Skyrim SE
    br.ReadInt32(); // folder offset (always 36)
    var archiveFlags = br.ReadUInt32();
    var folderCount = br.ReadInt32();
    var fileCount = br.ReadInt32();
    br.ReadInt32(); // totalFolderNameLen
    br.ReadInt32(); // totalFileNameLen
    br.ReadUInt16(); // fileFlags
    br.ReadUInt16(); // padding

    var defaultCompressed = (archiveFlags & 0x04) != 0;
    var hasDirectoryNames = (archiveFlags & 0x01) != 0;
    var hasFileNames = (archiveFlags & 0x02) != 0;

    // Folder records differ by version:
    //   v103/v104: hash(8) + count(4) + offset(4) = 16 bytes
    //   v105 (SSE): hash(8) + count(4) + padding(4) + offset(8) = 24 bytes
    var folders = new (ulong Hash, int Count, long Offset)[folderCount];
    for (var i = 0; i < folderCount; i++) {
      folders[i].Hash = br.ReadUInt64();
      folders[i].Count = br.ReadInt32();
      if (version == 105) {
        br.ReadInt32(); // padding
        folders[i].Offset = br.ReadInt64();
      } else {
        folders[i].Offset = br.ReadUInt32();
      }
    }

    // Read file records for each folder
    var folderNames = new string[folderCount];
    var allFileRecords = new List<(string Folder, ulong Hash, uint Size, uint Offset)>();
    for (var i = 0; i < folderCount; i++) {
      if (hasDirectoryNames) {
        var nameLen = br.ReadByte();
        var nameBytes = br.ReadBytes(nameLen);
        // Remove trailing null if present
        var len = nameLen;
        if (len > 0 && nameBytes[len - 1] == 0) len--;
        folderNames[i] = Encoding.ASCII.GetString(nameBytes, 0, len);
      }
      for (var j = 0; j < folders[i].Count; j++) {
        var fileHash = br.ReadUInt64();
        var rawSize = br.ReadUInt32();
        var offset = br.ReadUInt32();
        allFileRecords.Add((folderNames[i] ?? "", fileHash, rawSize, offset));
      }
    }

    // Read file names
    var fileNames = new string[fileCount];
    if (hasFileNames) {
      for (var i = 0; i < fileCount; i++) {
        var sb = new StringBuilder();
        byte b;
        while ((b = br.ReadByte()) != 0) sb.Append((char)b);
        fileNames[i] = sb.ToString();
      }
    }

    for (var i = 0; i < allFileRecords.Count && i < fileCount; i++) {
      var (folder, _, rawSize, offset) = allFileRecords[i];
      var compressed = defaultCompressed;
      // Bit 30 toggles compression for this file
      if ((rawSize & 0x40000000) != 0) {
        compressed = !compressed;
        rawSize &= 0x3FFFFFFF;
      }
      _entries.Add(new BsaEntry {
        FileName = i < fileNames.Length ? fileNames[i] : $"file_{i}",
        FolderPath = folder,
        OriginalSize = rawSize,
        CompressedSize = compressed ? -1 : rawSize,
        IsCompressed = compressed,
        Offset = offset,
      });
    }
  }

  private void ReadBa2(BinaryReader br) {
    br.ReadInt32(); // version
    var type = br.ReadUInt32(); // GNRL=0x4C524E47 or DX10=0x30315844
    var fileCount = br.ReadInt32();
    var nameTableOffset = br.ReadInt64();

    var isGnrl = type == 0x4C524E47;

    var records = new (long Offset, uint CompSize, uint OrigSize)[fileCount];
    for (var i = 0; i < fileCount; i++) {
      if (isGnrl) {
        br.ReadUInt32(); // name hash
        br.ReadBytes(4); // ext
        br.ReadUInt32(); // dir hash
        br.ReadUInt32(); // flags
        var offset = br.ReadInt64();
        var compSize = br.ReadUInt32();
        var origSize = br.ReadUInt32();
        br.ReadUInt32(); // align (BAADF00D)
        records[i] = (offset, compSize, origSize);
      } else {
        // DX10 texture - simplified read
        br.ReadUInt32(); // name hash
        br.ReadBytes(4); // ext
        br.ReadUInt32(); // dir hash
        br.ReadBytes(1); // unknown
        br.ReadBytes(1); // num chunks
        br.ReadUInt16(); // chunk header size
        br.ReadUInt16(); // height
        br.ReadUInt16(); // width
        br.ReadBytes(1); // num mips
        br.ReadBytes(1); // format
        br.ReadUInt16(); // tile mode (or isCubemap)
        var offset = br.ReadInt64();
        var compSize = br.ReadUInt32();
        var origSize = br.ReadUInt32();
        br.ReadUInt32(); // align
        records[i] = (offset, compSize, origSize);
      }
    }

    // Read name table
    if (nameTableOffset > 0 && nameTableOffset < _stream.Length) {
      _stream.Position = nameTableOffset;
      for (var i = 0; i < fileCount; i++) {
        var nameLen = br.ReadUInt16();
        var nameBytes = br.ReadBytes(nameLen);
        var name = Encoding.UTF8.GetString(nameBytes);
        var folder = Path.GetDirectoryName(name) ?? "";
        var file = Path.GetFileName(name);
        var compressed = records[i].CompSize > 0 && records[i].CompSize != records[i].OrigSize;
        _entries.Add(new BsaEntry {
          FileName = file,
          FolderPath = folder,
          OriginalSize = records[i].OrigSize,
          CompressedSize = compressed ? records[i].CompSize : records[i].OrigSize,
          IsCompressed = compressed,
          Offset = records[i].Offset,
        });
      }
    } else {
      for (var i = 0; i < fileCount; i++) {
        var compressed = records[i].CompSize > 0 && records[i].CompSize != records[i].OrigSize;
        _entries.Add(new BsaEntry {
          FileName = $"file_{i:D4}",
          FolderPath = "",
          OriginalSize = records[i].OrigSize,
          CompressedSize = compressed ? records[i].CompSize : records[i].OrigSize,
          IsCompressed = compressed,
          Offset = records[i].Offset,
        });
      }
    }
  }

  /// <summary>Extracts entry data.</summary>
  public byte[] Extract(BsaEntry entry) {
    _stream.Position = entry.Offset;
    using var br = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);

    if (_format == BsaFormat.Tes4 && entry.IsCompressed) {
      // TES4/SSE compressed entries: 4-byte original size prefix, then zlib-compressed data
      br.ReadUInt32(); // original size (redundant — already in entry)
      var compData = br.ReadBytes((int)(entry.OriginalSize - 4));
      return FileFormat.Zlib.ZlibStream.Decompress(compData);
    }

    if (_format == BsaFormat.Ba2 && entry.IsCompressed) {
      var compData = br.ReadBytes((int)entry.CompressedSize);
      return FileFormat.Zlib.ZlibStream.Decompress(compData);
    }

    var data = new byte[entry.OriginalSize];
    _stream.ReadExactly(data);
    return data;
  }
}
