using System.Text;

namespace FileFormat.Bsa;

/// <summary>
/// Creates BSA archives in TES4 format (version 105, Skyrim SE compatible).
/// </summary>
public sealed class BsaWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly bool _compress;
  private readonly List<(string Path, byte[] Data)> _files = [];

  public BsaWriter(Stream stream, bool leaveOpen = false, bool compress = false) {
    _stream = stream;
    _leaveOpen = leaveOpen;
    _compress = compress;
  }

  public void AddFile(string path, byte[] data) {
    _files.Add((path.Replace('/', '\\'), data));
  }

  public void Finish() {
    // Group files by folder
    var folders = _files.GroupBy(f => Path.GetDirectoryName(f.Path) ?? "")
      .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
      .ToList();

    var folderCount = folders.Count;
    var fileCount = _files.Count;
    var totalFolderNameLen = folders.Sum(f => f.Key.Length + 1); // includes null terminator counted by length byte
    var totalFileNameLen = _files.Sum(f => Path.GetFileName(f.Path).Length + 1);

    uint archiveFlags = 0x01 | 0x02; // has directory names + file names
    if (_compress) archiveFlags |= 0x04;

    using var bw = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: _leaveOpen);

    // Header
    bw.Write(0x00415342u); // "BSA\0"
    bw.Write(105); // version (Skyrim SE)
    bw.Write(36); // folder offset
    bw.Write(archiveFlags);
    bw.Write(folderCount);
    bw.Write(fileCount);
    bw.Write(totalFolderNameLen);
    bw.Write(totalFileNameLen);
    bw.Write((ushort)0); // file flags
    bw.Write((ushort)0); // padding to align header to 36 bytes

    // Folder record size for v105: 8 (hash) + 4 (count) + 4 (padding) + 8 (offset) = 24 bytes
    const int folderRecordSize = 24;
    // File record size: 8 (hash) + 4 (size) + 4 (offset) = 16 bytes
    const int fileRecordSize = 16;

    // Calculate where file data starts
    var folderRecordsStart = 36L;
    var fileBlockStart = folderRecordsStart + folderCount * folderRecordSize;

    // File block: for each folder: 1 byte name len + name bytes + null terminator + file records
    var fileBlockSize = 0L;
    foreach (var fg in folders) {
      fileBlockSize += 1 + fg.Key.Length + 1; // length byte + name + null
      fileBlockSize += fg.Count() * fileRecordSize;
    }
    var fileNamesStart = fileBlockStart + fileBlockSize;
    var dataStart = fileNamesStart + totalFileNameLen;

    // Write folder records
    var currentFileBlockOffset = fileBlockStart;
    foreach (var fg in folders) {
      bw.Write(ComputeHash(fg.Key)); // hash
      bw.Write(fg.Count()); // count
      bw.Write(0); // padding (v105)
      bw.Write(currentFileBlockOffset); // offset (8 bytes for v105)
      currentFileBlockOffset += 1 + fg.Key.Length + 1 + fg.Count() * fileRecordSize;
    }

    // Write file records per folder (with folder name prefix)
    var dataOffset = dataStart;
    var allFileData = new List<byte[]>();
    foreach (var fg in folders) {
      // Folder name: length byte (includes null), name bytes, null terminator
      var nameBytes = Encoding.ASCII.GetBytes(fg.Key);
      bw.Write((byte)(nameBytes.Length + 1)); // includes null
      bw.Write(nameBytes);
      bw.Write((byte)0);

      foreach (var (path, data) in fg) {
        bw.Write(ComputeHash(Path.GetFileName(path))); // hash
        bw.Write((uint)data.Length); // size
        bw.Write((uint)dataOffset); // offset
        allFileData.Add(data);
        dataOffset += data.Length;
      }
    }

    // Write file name table
    foreach (var (path, _) in _files) {
      var name = Path.GetFileName(path);
      bw.Write(Encoding.ASCII.GetBytes(name));
      bw.Write((byte)0);
    }

    // Write file data
    foreach (var data in allFileData) {
      bw.Write(data);
    }
  }

  public void Dispose() {
    if (!_leaveOpen) _stream.Dispose();
  }

  private static ulong ComputeHash(string name) {
    // Simplified BSA hash function
    if (name.Length == 0) return 0;
    var lower = name.ToLowerInvariant();
    ulong hash = 0;
    for (var i = 0; i < lower.Length; i++)
      hash = hash * 0x1003F + lower[i];
    return hash;
  }
}
