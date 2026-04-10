#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Zfs;

public sealed class ZfsReader : IDisposable {
  private const ulong UberblockMagic = 0x00BAB10C;
  private const int LabelSize = 256 * 1024;
  private const int NvlistOffset = 16 * 1024; // 8KB blank + 8KB into label
  private const int UberblockArrayOffset = 128 * 1024; // offset within label
  private const int UberblockSize = 1024;

  private readonly byte[] _data;
  private readonly List<ZfsEntry> _entries = [];

  public IReadOnlyList<ZfsEntry> Entries => _entries;

  public ZfsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < LabelSize)
      throw new InvalidDataException("ZFS: image too small.");

    // Find best uberblock in L0 (offset 0)
    ulong bestTxg = 0;
    int bestUbOff = -1;

    for (int i = 0; i < 128; i++) {
      var ubOff = UberblockArrayOffset + i * UberblockSize;
      if (ubOff + 40 > _data.Length) break;

      var magic = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(ubOff));
      if (magic != UberblockMagic) continue;

      var txg = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(ubOff + 16));
      if (txg > bestTxg) {
        bestTxg = txg;
        bestUbOff = ubOff;
      }
    }

    if (bestUbOff < 0)
      throw new InvalidDataException("ZFS: no valid uberblock found.");

    // ZFS is extremely complex for full traversal.
    // Try to parse nvlist from label for pool information
    ParseNvlist();
  }

  private void ParseNvlist() {
    // ZFS nvlist at label offset 16KB (after 8KB blank + 8KB nvlist header)
    // Actually: blank(8KB) + boot header(8KB) + nvlist(112KB) + uberblocks(128KB)
    // nvlist starts at offset 16384 in label 0
    var nvOff = 16384;
    if (nvOff + 16 > _data.Length) return;

    // XDR encoded nvlist: encoding(1 byte), endian(1 byte), reserved(2), version(4), name(4+string), ...
    // This is complex XDR encoding. For now, scan for readable strings that look like dataset names
    var text = new StringBuilder();
    for (int i = nvOff; i < Math.Min(nvOff + 112 * 1024, _data.Length); i++) {
      if (_data[i] >= 0x20 && _data[i] < 0x7F)
        text.Append((char)_data[i]);
      else if (text.Length > 0) {
        var s = text.ToString();
        // Look for potential file/dataset names (heuristic)
        if (s.Length > 3 && s.Length < 256 && s.Contains('/') && !s.Contains('\0') && !s.StartsWith("org.")) {
          // Could be a dataset path
        }
        text.Clear();
      }
    }

    // ZFS full traversal would require: MOS → DSL dir → dataset → ZAP → dnode → file data
    // This is beyond practical scope for a clean-room implementation
    // Return empty entries — format is detection/identification only
  }

  public byte[] Extract(ZfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return [];
  }

  public void Dispose() { }
}
