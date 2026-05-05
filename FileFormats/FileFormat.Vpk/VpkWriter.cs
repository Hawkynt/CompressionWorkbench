namespace FileFormat.Vpk;

/// <summary>
/// Creates single-file VPK v1 archives (all data in _dir file, archiveIndex = 0x7FFF).
/// </summary>
public sealed class VpkWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(VpkEntry Entry, byte[] Data)> _pendingEntries = [];

  public VpkWriter(Stream stream, bool leaveOpen = false) {
    _stream = stream;
    _leaveOpen = leaveOpen;
  }

  /// <summary>Adds a file to the archive.</summary>
  public void AddFile(string path, byte[] data) {
    // Split path into extension, directory, filename
    var ext = Path.GetExtension(path).TrimStart('.');
    if (ext == "") ext = " ";
    var name = Path.GetFileNameWithoutExtension(path);
    if (name == "") name = " ";
    var dir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
    if (dir == "") dir = " ";

    var entry = new VpkEntry {
      FileName = name,
      DirectoryPath = dir,
      Extension = ext,
      Crc32 = ComputeCrc32(data),
      ArchiveIndex = 0x7FFF,
      Offset = 0, // will be set during write
      Length = (uint)data.Length,
    };
    _pendingEntries.Add((entry, data));
  }

  /// <summary>Writes the VPK file.</summary>
  public void Finish() {
    // Group entries by Extension → Path for directory tree structure
    var grouped = _pendingEntries
      .GroupBy(e => e.Entry.Extension)
      .OrderBy(g => g.Key);

    // First pass: build the directory tree in memory to calculate tree size
    using var treeMs = new MemoryStream();
    using var datMs = new MemoryStream();

    foreach (var extGroup in grouped) {
      WriteNullString(treeMs, extGroup.Key);
      var byPath = extGroup.GroupBy(e => e.Entry.DirectoryPath).OrderBy(g => g.Key);
      foreach (var pathGroup in byPath) {
        WriteNullString(treeMs, pathGroup.Key);
        foreach (var (entry, data) in pathGroup) {
          WriteNullString(treeMs, entry.FileName);
          var dataOffset = (uint)datMs.Position;
          datMs.Write(data);

          using var bw = new BinaryWriter(treeMs, System.Text.Encoding.UTF8, leaveOpen: true);
          bw.Write(entry.Crc32);
          bw.Write((ushort)0); // preload bytes
          bw.Write((ushort)0x7FFF); // archive index (embedded)
          bw.Write(dataOffset);
          bw.Write((uint)data.Length);
          bw.Write((ushort)0xFFFF); // terminator
        }
        treeMs.WriteByte(0); // end of path's files
      }
      treeMs.WriteByte(0); // end of extension's paths
    }
    treeMs.WriteByte(0); // end of extensions

    // Write header
    using var bw2 = new BinaryWriter(_stream, System.Text.Encoding.UTF8, leaveOpen: true);
    bw2.Write(VpkReader.Signature);
    bw2.Write(1); // version 1
    bw2.Write((int)treeMs.Length); // tree size

    // Write tree
    treeMs.Position = 0;
    treeMs.CopyTo(_stream);

    // Write data
    datMs.Position = 0;
    datMs.CopyTo(_stream);
  }

  public void Dispose() {
    if (!_leaveOpen) _stream.Dispose();
  }

  private static void WriteNullString(Stream s, string str) {
    foreach (var c in str) s.WriteByte((byte)c);
    s.WriteByte(0);
  }

  private static uint ComputeCrc32(byte[] data) {
    // Simple CRC32 (IEEE 802.3)
    var crc = 0xFFFFFFFFu;
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; i++)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
    }
    return ~crc;
  }
}
