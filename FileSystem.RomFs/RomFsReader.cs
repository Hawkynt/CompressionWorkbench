#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.RomFs;

/// <summary>
/// Reads Linux ROMFS filesystem images (romfs v1).
/// Magic: "-rom1fs-" at offset 0. All multi-byte integers are big-endian.
/// </summary>
public sealed class RomFsReader {
  private static readonly byte[] Magic = "-rom1fs-"u8.ToArray();

  private readonly byte[] _data;

  /// <summary>All entries (files and directories) discovered in the image.</summary>
  public IReadOnlyList<RomFsEntry> Entries { get; }

  /// <summary>Volume name read from the superblock.</summary>
  public string VolumeName { get; }

  /// <summary>
  /// Parses a ROMFS image from <paramref name="stream"/>.
  /// Throws <see cref="InvalidDataException"/> on bad magic or truncated data.
  /// </summary>
  public RomFsReader(Stream stream) {
    if (stream.Length < 16)
      throw new InvalidDataException("Stream too small to be a ROMFS image.");

    _data = new byte[stream.Length];
    stream.Position = 0;
    var bytesRead = stream.Read(_data, 0, _data.Length);
    if (bytesRead < 16)
      throw new InvalidDataException("Stream too small to be a ROMFS image.");

    // Validate magic "-rom1fs-"
    for (var i = 0; i < Magic.Length; i++) {
      if (_data[i] != Magic[i])
        throw new InvalidDataException("Not a ROMFS image: invalid magic bytes.");
    }

    // Superblock layout:
    //   [0..7]  magic "-rom1fs-"
    //   [8..11] uint32 BE fullSize
    //   [12..15] uint32 BE checksum
    //   [16..]  volume name (null-terminated, padded to 16-byte boundary)
    var (volumeName, firstFileOffset) = ReadSuperblock();
    VolumeName = volumeName;

    var entries = new List<RomFsEntry>();
    TraverseDirectory(firstFileOffset, "", entries);
    Entries = entries;
  }

  private (string VolumeName, long FirstFileOffset) ReadSuperblock() {
    // Volume name starts at offset 16, null-terminated, padded to 16-byte alignment
    var nameStart = 16;
    var nameEnd = nameStart;
    while (nameEnd < _data.Length && _data[nameEnd] != 0)
      nameEnd++;
    var volumeName = Encoding.ASCII.GetString(_data, nameStart, nameEnd - nameStart);

    // The name field (including null terminator) is padded to a 16-byte boundary
    // measured from offset 16 (the start of the name field).
    var nameFieldLen = nameEnd - nameStart + 1; // include null terminator
    var paddedNameLen = Align16(nameFieldLen);
    var firstFileOffset = nameStart + paddedNameLen;

    return (volumeName, firstFileOffset);
  }

  private void TraverseDirectory(long dirOffset, string parentPath, List<RomFsEntry> entries) {
    // dirOffset is the offset of the first file header in this directory.
    // For the root, specInfo of the "." entry points to firstChild.
    // We follow the chain via nextAndType's upper-28-bit next pointer.

    var offset = dirOffset;
    while (offset != 0 && offset < _data.Length) {
      if (offset + 16 > _data.Length) break;

      var nextAndType = ReadUInt32BE(offset);
      var specInfo   = ReadUInt32BE(offset + 4);
      var size       = (int)ReadUInt32BE(offset + 8);
      // checksum at offset+12 (not validated here)

      var next = (long)(nextAndType & 0xFFFFFFF0u); // upper 28 bits (lower nibble zeroed)
      var type = (int)(nextAndType & 0x0F);          // lower 4 bits

      // Name starts at offset+16, null-terminated, padded to 16-byte boundary from entry start
      var nameOffset = offset + 16;
      var nameEnd = nameOffset;
      while (nameEnd < _data.Length && _data[nameEnd] != 0)
        nameEnd++;
      var name = Encoding.ASCII.GetString(_data, (int)nameOffset, (int)(nameEnd - nameOffset));

      // Data follows the name field (padded to 16-byte boundary from entry start)
      var nameFieldLen = nameEnd - nameOffset + 1; // include null terminator
      var paddedNameLen = Align16((int)nameFieldLen);
      var dataOffset = nameOffset + paddedNameLen;

      // Skip "." and ".." entries
      if (name != "." && name != "..") {
        var fullPath = string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;

        if (type == 1) {
          // Directory: specInfo = offset of first child file header
          entries.Add(new RomFsEntry {
            Name = fullPath,
            Size = 0,
            DataOffset = dataOffset,
            IsDirectory = true
          });
          if (specInfo != 0 && specInfo < _data.Length)
            TraverseDirectory(specInfo, fullPath, entries);
        } else if (type == 2) {
          // Regular file
          entries.Add(new RomFsEntry {
            Name = fullPath,
            Size = size,
            DataOffset = dataOffset,
            IsDirectory = false
          });
        }
        // Types 0,3-7 (hardlinks, symlinks, devices, sockets, fifos) are skipped
      }

      if (next == 0) break;
      offset = next;
    }
  }

  /// <summary>Extracts the data for a regular file entry.</summary>
  public byte[] Extract(RomFsEntry entry) {
    if (entry.IsDirectory) return [];
    if (entry.DataOffset < 0 || entry.DataOffset + entry.Size > _data.Length)
      throw new InvalidDataException($"Entry '{entry.Name}' data is out of bounds.");
    var result = new byte[entry.Size];
    _data.AsSpan((int)entry.DataOffset, entry.Size).CopyTo(result);
    return result;
  }

  private uint ReadUInt32BE(long offset) =>
    BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan((int)offset, 4));

  // Pad length to next multiple of 16 (or same value if already aligned)
  private static int Align16(int len) => (len + 15) & ~15;
}
