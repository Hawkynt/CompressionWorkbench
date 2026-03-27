#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Wrapster;

/// <summary>
/// Reads Wrapster files — data files disguised as MP3 files.
/// Supports v1/v2 ("wrapster" signature) and v3 ("wwapster" signature).
/// </summary>
public sealed class WrapsterReader : IDisposable {
  // MP3 frame header (MPEG1 Layer3 128kbps stereo)
  private static readonly byte[] Mp3Header = [0xFF, 0xFB];

  private readonly byte[] _data;
  private readonly List<WrapsterEntry> _entries = [];

  public IReadOnlyList<WrapsterEntry> Entries => _entries;
  public int Version { get; private set; }

  public WrapsterReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 36)
      throw new InvalidDataException("Wrapster: file too small.");

    // Check for MP3 frame header
    if (_data[0] != 0xFF || (_data[1] & 0xE0) != 0xE0)
      throw new InvalidDataException("Wrapster: not an MP3 frame.");

    // Find Wrapster signature after MP3 frame header
    // v1/v2: "wrapster" at offset 4 within the first frame
    // v3: "wwapster" at offset 4

    var sig = FindSignature();
    if (sig < 0)
      throw new InvalidDataException("Wrapster: signature not found.");

    if (Version == 3) {
      ParseV3(sig);
    } else {
      ParseV1V2(sig);
    }
  }

  private int FindSignature() {
    // Search in first 4KB for the signature
    var limit = Math.Min(_data.Length - 8, 4096);
    for (var i = 2; i < limit; i++) {
      if (_data[i] == (byte)'w' && i + 8 <= _data.Length) {
        var candidate = Encoding.ASCII.GetString(_data, i, 8);
        if (candidate == "wrapster") { Version = 2; return i; }
        if (candidate == "wwapster") { Version = 3; return i; }
      }
    }
    return -1;
  }

  private void ParseV1V2(int sigOffset) {
    // After "wrapster" (8 bytes): 24-byte header per file
    // Format: 4 bytes file count, then per file: filename (null-terminated within block)
    var pos = sigOffset + 8;
    if (pos + 4 > _data.Length) return;

    // v2 header: 4-byte total data size, 4-byte file count
    var totalSize = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(pos));
    pos += 4;

    if (pos + 4 > _data.Length) return;
    var fileCount = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(pos));
    pos += 4;

    if (fileCount <= 0 || fileCount > 10000) {
      // Try as v1: single file, totalSize is the file size
      _entries.Add(new WrapsterEntry {
        Name = "unwrapped",
        Size = totalSize,
        Offset = sigOffset + 8 + 4,
        DataLength = totalSize,
      });
      return;
    }

    // Read file entries (each: 256-byte name + 4-byte size + 4-byte offset)
    var dataStart = pos + fileCount * 264;
    for (var i = 0; i < fileCount && pos + 264 <= _data.Length; i++) {
      var nameEnd = Array.IndexOf(_data, (byte)0, pos, 256);
      var nameLen = nameEnd >= 0 ? nameEnd - pos : 256;
      var name = Encoding.ASCII.GetString(_data, pos, nameLen);
      pos += 256;

      var size = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(pos));
      pos += 4;
      var offset = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(pos));
      pos += 4;

      // Offsets are relative to data start
      // But in some versions they're absolute
      var actualOffset = offset < dataStart ? dataStart + offset : offset;
      if (actualOffset + size > _data.Length) size = Math.Max(0, _data.Length - actualOffset);

      _entries.Add(new WrapsterEntry {
        Name = string.IsNullOrEmpty(name) ? $"file_{i}" : name,
        Size = size,
        Offset = actualOffset,
        DataLength = size,
      });
    }

    // If no entries were added, try treating as single file
    if (_entries.Count == 0 && totalSize > 0) {
      _entries.Add(new WrapsterEntry {
        Name = "unwrapped",
        Size = totalSize,
        Offset = sigOffset + 12,
        DataLength = Math.Min(totalSize, _data.Length - sigOffset - 12),
      });
    }
  }

  private void ParseV3(int sigOffset) {
    // v3 "wwapster": 8 bytes sig + 4 bytes total size + file entries
    var pos = sigOffset + 8;
    if (pos + 4 > _data.Length) return;

    var totalSize = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(pos));
    pos += 4;

    if (pos + 4 > _data.Length) return;
    var fileCount = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(pos));
    pos += 4;

    if (fileCount <= 0 || fileCount > 10000) {
      _entries.Add(new WrapsterEntry {
        Name = "unwrapped",
        Size = totalSize,
        Offset = sigOffset + 12,
        DataLength = Math.Min(totalSize, _data.Length - sigOffset - 12),
      });
      return;
    }

    var dataStart = pos + fileCount * 264;
    for (var i = 0; i < fileCount && pos + 264 <= _data.Length; i++) {
      var nameEnd = Array.IndexOf(_data, (byte)0, pos, 256);
      var nameLen = nameEnd >= 0 ? nameEnd - pos : 256;
      var name = Encoding.ASCII.GetString(_data, pos, nameLen);
      pos += 256;

      var size = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(pos));
      pos += 4;
      var offset = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(pos));
      pos += 4;

      var actualOffset = offset < dataStart ? dataStart + offset : offset;
      if (actualOffset + size > _data.Length) size = Math.Max(0, _data.Length - actualOffset);

      _entries.Add(new WrapsterEntry {
        Name = string.IsNullOrEmpty(name) ? $"file_{i}" : name,
        Size = size,
        Offset = actualOffset,
        DataLength = size,
      });
    }
  }

  public byte[] Extract(WrapsterEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset + entry.DataLength > _data.Length)
      throw new InvalidDataException("Wrapster: data extends beyond file.");
    return _data.AsSpan(entry.Offset, entry.DataLength).ToArray();
  }

  public void Dispose() { }
}
