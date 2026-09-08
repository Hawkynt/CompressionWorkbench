#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.MinixFs;

/// <summary>
/// Reads MINIX v1/v2/v3 filesystem images. V1 uses 32-byte inodes with 16-bit
/// zone pointers; v2/v3 use 64-byte inodes with 32-bit zone pointers. All three
/// block-map depths defined by their respective on-disk inode layouts are read.
/// </summary>
public sealed class MinixFsReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<MinixFsEntry> _entries = [];

  public IReadOnlyList<MinixFsEntry> Entries => _entries;

  private uint _ninodes;
  private ushort _imapBlocks;
  private ushort _zmapBlocks;
  private int _blockSize;
  private int _inodeSize;
  private int _pointerSize;
  private int _nameLength;
  private int _directoryInodeSize;
  private int _indirectDepth;

  private enum MinixVersion { V1, V2, V3 }
  private MinixVersion _version;

  private const ushort MagicV1_14 = 0x137F;
  private const ushort MagicV1_30 = 0x138F;
  private const ushort MagicV2_14 = 0x2468;
  private const ushort MagicV2_30 = 0x2478;
  private const ushort MagicV3 = 0x4D5A;
  private const int SuperblockOffset = 1024;

  public MinixFsReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 32)
      throw new InvalidDataException("MinixFs: image too small for superblock.");

    var sb = _data.AsSpan(SuperblockOffset);
    var magic16 = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(16, 2));
    var magic24 = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(24, 2));

    if (magic24 == MagicV3) {
      _version = MinixVersion.V3;
      _ninodes = BinaryPrimitives.ReadUInt32LittleEndian(sb);
      _imapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6, 2));
      _zmapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(8, 2));
      _blockSize = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(28, 2));
      if (_blockSize == 0) _blockSize = 1024;
      _inodeSize = 64;
      _pointerSize = 4;
      _nameLength = 60;
      _directoryInodeSize = 4;
      _indirectDepth = 3;
    } else if (magic16 is MagicV2_14 or MagicV2_30) {
      _version = MinixVersion.V2;
      _ninodes = BinaryPrimitives.ReadUInt16LittleEndian(sb);
      _imapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(4, 2));
      _zmapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6, 2));
      _blockSize = 1024;
      _inodeSize = 64;
      _pointerSize = 4;
      _nameLength = magic16 == MagicV2_30 ? 30 : 14;
      _directoryInodeSize = 2;
      _indirectDepth = 3;
    } else if (magic16 is MagicV1_14 or MagicV1_30) {
      _version = MinixVersion.V1;
      _ninodes = BinaryPrimitives.ReadUInt16LittleEndian(sb);
      _imapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(4, 2));
      _zmapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6, 2));
      _blockSize = 1024;
      _inodeSize = 32;
      _pointerSize = 2;
      _nameLength = magic16 == MagicV1_30 ? 30 : 14;
      _directoryInodeSize = 2;
      _indirectDepth = 2;
    } else {
      throw new InvalidDataException(
        $"MinixFs: invalid magic. Got 0x{magic16:X4} at +16 and 0x{magic24:X4} at +24.");
    }

    if (_ninodes == 0 || _imapBlocks == 0 || _zmapBlocks == 0)
      throw new InvalidDataException("MinixFs: invalid zero geometry in superblock.");

    ReadDirectory(1, string.Empty, []);
  }

  private long InodeTableOffset()
    => 2L * _blockSize + (long)_imapBlocks * _blockSize + (long)_zmapBlocks * _blockSize;

  private byte[]? ReadInode(uint inodeNum) {
    if (inodeNum == 0 || inodeNum > _ninodes) return null;
    var offset = InodeTableOffset() + (long)(inodeNum - 1) * _inodeSize;
    if (offset < 0 || offset + _inodeSize > _data.Length) return null;
    return _data.AsSpan((int)offset, _inodeSize).ToArray();
  }

  private (ushort Mode, uint Size, uint[] Zones) ParseInode(byte[] inode) {
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    if (_version == MinixVersion.V1) {
      var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4, 4));
      var zones = new uint[9];
      for (var i = 0; i < zones.Length; ++i)
        zones[i] = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(14 + i * 2, 2));
      return (mode, size, zones);
    }

    var modernSize = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(8, 4));
    var modernZones = new uint[10];
    for (var i = 0; i < modernZones.Length; ++i)
      modernZones[i] = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(24 + i * 4, 4));
    return (mode, modernSize, modernZones);
  }

  private static bool IsDirectory(ushort mode) => (mode & 0xF000) == 0x4000;

  private byte[] ReadInodeData(uint inodeNum) {
    var inode = ReadInode(inodeNum);
    if (inode is null) return [];
    var (_, size, zones) = ParseInode(inode);
    return ReadZones(zones, size);
  }

  private byte[] ReadZones(uint[] zones, uint size) {
    if (size == 0) return [];
    using var output = new MemoryStream();
    var remaining = (long)size;

    for (var i = 0; i < 7 && remaining > 0; ++i) {
      if (zones[i] == 0) AppendHole(output, ref remaining, _blockSize);
      else AppendZone(output, zones[i], ref remaining);
    }

    var pointerCount = _blockSize / _pointerSize;
    for (var depth = 1; depth <= _indirectDepth && remaining > 0; ++depth) {
      var root = zones[7 + depth - 1];
      if (root != 0) {
        ReadIndirect(output, root, ref remaining, depth);
        continue;
      }
      var zonesCovered = 1L;
      for (var level = 0; level < depth; ++level) zonesCovered *= pointerCount;
      AppendHole(output, ref remaining, checked(zonesCovered * _blockSize));
    }

    return output.ToArray();
  }

  private static void AppendHole(Stream output, ref long remaining, long bytes) {
    var toWrite = Math.Min(remaining, bytes);
    if (toWrite <= 0) return;
    var zeros = new byte[(int)Math.Min(toWrite, 64 * 1024)];
    while (toWrite > 0) {
      var count = (int)Math.Min(zeros.Length, toWrite);
      output.Write(zeros, 0, count);
      toWrite -= count;
      remaining -= count;
    }
  }

  private void AppendZone(Stream output, uint zone, ref long remaining) {
    var offset = checked((long)zone * _blockSize);
    if (offset < 0 || offset + _blockSize > _data.Length)
      throw new InvalidDataException($"MinixFs: zone {zone} lies outside the image.");
    var count = (int)Math.Min(remaining, _blockSize);
    output.Write(_data, (int)offset, count);
    remaining -= count;
  }

  private void ReadIndirect(Stream output, uint indirectZone, ref long remaining, int depth) {
    var offset = checked((long)indirectZone * _blockSize);
    if (offset < 0 || offset + _blockSize > _data.Length)
      throw new InvalidDataException($"MinixFs: indirect zone {indirectZone} lies outside the image.");

    var pointersPerBlock = _blockSize / _pointerSize;
    for (var i = 0; i < pointersPerBlock && remaining > 0; ++i) {
      var pointerOffset = checked((int)offset + i * _pointerSize);
      var pointer = _pointerSize == 2
        ? BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(pointerOffset, 2))
        : BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pointerOffset, 4));
      if (pointer == 0) {
        var zonesCovered = 1L;
        for (var level = 1; level < depth; ++level) zonesCovered *= pointersPerBlock;
        AppendHole(output, ref remaining, checked(zonesCovered * _blockSize));
      } else if (depth == 1) {
        AppendZone(output, pointer, ref remaining);
      } else {
        ReadIndirect(output, pointer, ref remaining, depth - 1);
      }
    }
  }

  private void ReadDirectory(uint inodeNum, string path, HashSet<uint> visitedDirectories) {
    if (!visitedDirectories.Add(inodeNum)) return;
    var dirData = ReadInodeData(inodeNum);
    var entrySize = _directoryInodeSize + _nameLength;
    for (var offset = 0; offset + entrySize <= dirData.Length; offset += entrySize) {
      var inode = _directoryInodeSize == 2
        ? BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(offset, 2))
        : BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(offset, 4));
      if (inode == 0) continue;
      var name = ReadNullTermString(dirData, offset + _directoryInodeSize, _nameLength);
      if (name is "." or "..") continue;
      ProcessDirectoryEntry(inode, name, path, visitedDirectories);
    }
  }

  private void ProcessDirectoryEntry(uint inodeNumber, string name, string path, HashSet<uint> visitedDirectories) {
    var inode = ReadInode(inodeNumber);
    if (inode is null)
      throw new InvalidDataException($"MinixFs: directory entry '{name}' references invalid inode {inodeNumber}.");
    var (mode, size, _) = ParseInode(inode);
    var isDirectory = IsDirectory(mode);
    var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
    _entries.Add(new MinixFsEntry {
      Name = fullPath,
      Size = isDirectory ? 0 : checked((int)size),
      InodeNumber = checked((int)inodeNumber),
      IsDirectory = isDirectory,
    });
    if (isDirectory)
      ReadDirectory(inodeNumber, fullPath, visitedDirectories);
  }

  private static string ReadNullTermString(byte[] data, int offset, int maxLength) {
    var end = offset;
    var limit = Math.Min(offset + maxLength, data.Length);
    while (end < limit && data[end] != 0) ++end;
    return Encoding.Latin1.GetString(data, offset, end - offset);
  }

  public byte[] Extract(MinixFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var inode = ReadInode((uint)entry.InodeNumber);
    if (inode is null) return [];
    var (_, size, _) = ParseInode(inode);
    var data = ReadInodeData((uint)entry.InodeNumber);
    return data.Length > size ? data.AsSpan(0, checked((int)size)).ToArray() : data;
  }

  public void Dispose() { }
}