namespace FileFormat.Vpk;

/// <summary>
/// Reads Valve Pak (VPK) archives used by Source engine games.
/// Supports v1 and v2 format. Single-file and multi-part archives.
/// </summary>
public sealed class VpkReader {
  /// <summary>VPK signature: 0x55AA1234</summary>
  public const uint Signature = 0x55AA1234;

  private readonly Stream _stream;
  private readonly List<VpkEntry> _entries = [];
  private readonly int _version;
  private readonly long _dataOffset; // offset where embedded data starts (after directory tree)

  public IReadOnlyList<VpkEntry> Entries => _entries;
  public int Version => _version;

  public VpkReader(Stream stream) {
    _stream = stream;
    using var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

    var sig = br.ReadUInt32();
    if (sig != Signature)
      throw new InvalidDataException($"Not a VPK file (signature: 0x{sig:X8})");

    _version = br.ReadInt32();
    var treeSize = br.ReadInt32();

    // v2 has extra header fields
    if (_version == 2) {
      br.ReadInt32(); // file data section size
      br.ReadInt32(); // archive MD5 section size
      br.ReadInt32(); // other MD5 section size
      br.ReadInt32(); // signature section size
    } else if (_version != 1) {
      throw new InvalidDataException($"Unsupported VPK version: {_version}");
    }

    var treeStart = stream.Position;
    _dataOffset = treeStart + treeSize;

    // Parse directory tree: Extension → Path → FileName
    ReadDirectoryTree(br);
  }

  private void ReadDirectoryTree(BinaryReader br) {
    while (true) {
      var ext = ReadNullString(br);
      if (ext.Length == 0) break;

      while (true) {
        var path = ReadNullString(br);
        if (path.Length == 0) break;

        while (true) {
          var name = ReadNullString(br);
          if (name.Length == 0) break;

          var crc = br.ReadUInt32();
          var preloadBytes = br.ReadUInt16();
          var archiveIndex = br.ReadUInt16();
          var offset = br.ReadUInt32();
          var length = br.ReadUInt32();
          var terminator = br.ReadUInt16(); // 0xFFFF

          byte[] preload = preloadBytes > 0 ? br.ReadBytes(preloadBytes) : [];

          _entries.Add(new VpkEntry {
            FileName = name,
            DirectoryPath = path,
            Extension = ext,
            Crc32 = crc,
            PreloadBytes = preload,
            ArchiveIndex = archiveIndex,
            Offset = offset,
            Length = length,
          });
        }
      }
    }
  }

  /// <summary>Extracts entry data. Only works for single-file VPKs (archiveIndex 0x7FFF).</summary>
  public byte[] Extract(VpkEntry entry) {
    var result = new byte[entry.PreloadBytes.Length + entry.Length];
    entry.PreloadBytes.CopyTo(result, 0);

    if (entry.Length > 0) {
      if (entry.ArchiveIndex != 0x7FFF)
        throw new NotSupportedException("Multi-part VPK extraction requires separate archive files.");

      _stream.Position = _dataOffset + entry.Offset;
      var read = _stream.Read(result, entry.PreloadBytes.Length, (int)entry.Length);
      if (read < (int)entry.Length)
        throw new InvalidDataException("Unexpected end of VPK data.");
    }

    return result;
  }

  private static string ReadNullString(BinaryReader br) {
    var sb = new System.Text.StringBuilder();
    while (true) {
      var b = br.ReadByte();
      if (b == 0) break;
      sb.Append((char)b);
    }
    return sb.ToString();
  }
}
