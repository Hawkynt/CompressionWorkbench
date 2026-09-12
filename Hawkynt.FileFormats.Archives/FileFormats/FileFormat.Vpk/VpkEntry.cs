namespace FileFormat.Vpk;

/// <summary>Entry in a VPK archive.</summary>
public sealed class VpkEntry {
  /// <summary>File name without extension.</summary>
  public string FileName { get; init; } = "";
  /// <summary>Directory path within the archive.</summary>
  public string DirectoryPath { get; init; } = "";
  /// <summary>File extension (without dot).</summary>
  public string Extension { get; init; } = "";
  /// <summary>CRC32 of the file data.</summary>
  public uint Crc32 { get; init; }
  /// <summary>Preload data bytes embedded in directory.</summary>
  public byte[] PreloadBytes { get; init; } = [];
  /// <summary>Which archive part contains data (0x7FFF = in _dir file).</summary>
  public ushort ArchiveIndex { get; init; }
  /// <summary>Offset within the archive file.</summary>
  public uint Offset { get; init; }
  /// <summary>Length of file data in archive.</summary>
  public uint Length { get; init; }

  /// <summary>Full path: dir/name.ext</summary>
  public string FullPath {
    get {
      var dir = DirectoryPath == " " || DirectoryPath == "" ? "" : DirectoryPath + "/";
      var ext = Extension == " " || Extension == "" ? "" : "." + Extension;
      return dir + FileName + ext;
    }
  }
}
