#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.PackDisk;

/// <summary>
/// Reads PackDisk/xMash/xDisk/GDC/DCS/MDC Amiga disk archives.
/// These all use XPK-based compression for individual tracks.
/// Common structure: 4-byte magic, track table, XPK-compressed track data.
/// We support listing and extracting stored/raw data.
/// </summary>
public sealed class PackDiskReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<PackDiskEntry> _entries = [];
  private string _format;

  public IReadOnlyList<PackDiskEntry> Entries => _entries;
  public string Format => _format;

  // Known magics
  private static readonly Dictionary<string, string> KnownMagics = new() {
    ["PDSK"] = "PackDisk",
    ["XMSH"] = "xMash",
    ["XDSK"] = "xDisk",
    ["GDC\0"] = "GDC",
    ["DCS\0"] = "DCS",
    ["MDC\0"] = "MDC",
  };

  private const int TrackSize = 11 * 512; // Amiga DD

  public PackDiskReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    _format = "PackDisk";
    Parse();
  }

  private void Parse() {
    if (_data.Length < 8)
      throw new InvalidDataException("PackDisk: file too small.");

    var magic = Encoding.ASCII.GetString(_data, 0, 4);
    if (!KnownMagics.TryGetValue(magic, out var fmt))
      throw new InvalidDataException($"PackDisk: unknown magic '{magic}'.");
    _format = fmt;

    // Generic layout: magic(4) + flags/version(4) + track entries
    var pos = 8;

    // Read track offset table or sequential tracks
    // Most of these formats store tracks sequentially with a per-track header
    var trackNum = 0;
    while (pos + 4 <= _data.Length) {
      // Check for XPKF header (XPK compressed chunk)
      if (pos + 8 <= _data.Length && _data[pos] == (byte)'X' && _data[pos + 1] == (byte)'P' &&
          _data[pos + 2] == (byte)'K' && _data[pos + 3] == (byte)'F') {
        // XPK chunk: "XPKF" + uint32 BE total length
        var xpkLen = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(pos + 4));
        if (xpkLen <= 0 || pos + 8 + xpkLen > _data.Length) break;

        _entries.Add(new PackDiskEntry {
          Name = $"track_{trackNum:D3}.raw",
          Size = TrackSize,
          CompressedSize = xpkLen + 8,
          Offset = pos,
        });
        pos += 8 + xpkLen;
        // Pad to even
        if ((pos & 1) != 0 && pos < _data.Length) pos++;
      } else if (pos + TrackSize <= _data.Length) {
        // Stored track (no compression header)
        _entries.Add(new PackDiskEntry {
          Name = $"track_{trackNum:D3}.raw",
          Size = TrackSize,
          CompressedSize = TrackSize,
          Offset = pos,
        });
        pos += TrackSize;
      } else {
        break;
      }
      trackNum++;
    }
  }

  public byte[] Extract(PackDiskEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset + entry.CompressedSize > _data.Length)
      throw new InvalidDataException("PackDisk: data extends beyond file.");

    // If it's an XPK chunk, return the raw data (we don't decompress XPK)
    // If it's stored (same size), return as-is
    return _data.AsSpan(entry.Offset, (int)entry.CompressedSize).ToArray();
  }

  public void Dispose() { }
}
