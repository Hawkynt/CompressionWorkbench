#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Ext;

/// <summary>
/// Mount-grade direct writer for the classic ext2 layout.  The session is deliberately
/// narrower than <see cref="ExtModifier"/>: it only enables profiles whose metadata can
/// be kept consistent without JBD/JBD2, extents, htree mutation or metadata checksums.
/// All mutations are bounded random-access updates against the existing image.
/// </summary>
internal sealed class Ext2MountedFilesystemSession : IFilesystemSession {
  private const uint RootInode = 2;
  private const uint IncompatFileType = 0x0002;
  private const uint CompatHasJournal = 0x0004;
  private const uint RoCompatSparseSuper = 0x0001;
  private const uint RoCompatLargeFile = 0x0002;
  private const uint InodeFlagIndex = 0x00001000;
  private const uint InodeFlagExtents = 0x00080000;
  private const ushort ModeTypeMask = 0xF000;
  private const ushort ModeRegular = 0x8000;
  private const ushort ModeDirectory = 0x4000;
  private const ushort ModeSymlink = 0xA000;
  private const ushort DefaultFileMode = ModeRegular | 0x01A4; // 0644
  private const ushort DefaultDirectoryMode = ModeDirectory | 0x01ED; // 0755
  private const ushort DefaultSymlinkMode = ModeSymlink | 0x01FF; // 0777
  private const int DirectPointerCount = 12;
  private const int InodeBlockOffset = 40;
  private const int InodeBlockPointerCount = 15;
  private const int FastSymlinkCapacity = InodeBlockPointerCount * 4;

  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly object _gate = new();
  private readonly Ext2Geometry _geometry;
  private readonly Dictionary<uint, int> _openHandles = [];
  private readonly HashSet<uint> _pendingUnlinks = [];
  private bool _disposed;

  public Ext2MountedFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("ext2 mounted writes require a readable, writable, seekable image.", nameof(image));
    if (!profile.CanMountWritable)
      throw new NotSupportedException("This ext profile is not qualified for mounted writes.");

    _image = image;
    _leaveOpen = leaveOpen;
    _geometry = Ext2Geometry.Read(image);
    Profile = profile;
    var root = ReadInode(RootInode);
    RootNodeId = ToNodeId(root);
  }

  public FilesystemDriverProfile Profile { get; }
  public FilesystemNodeId RootNodeId { get; }

  public static IReadOnlyList<string> GetWritableLimitations(
      Stream image,
      ExtDriverSuperblock super,
      IReadOnlyList<ExtEntry> entries) {
    var limitations = new List<string>();
    if ((super.FeatureCompat & CompatHasJournal) != 0)
      limitations.Add("Journaled ext3/ext4 profiles remain read-only until JBD/JBD2 transaction publication and replay are implemented.");
    var unsupportedIncompat = super.FeatureIncompat & ~IncompatFileType;
    if (unsupportedIncompat != 0)
      limitations.Add($"Mounted ext2 writes reject incompat feature bits 0x{unsupportedIncompat:X8}; classic block maps + FILETYPE are the writable subset.");
    var unsupportedRoCompat = super.FeatureRoCompat & ~(RoCompatSparseSuper | RoCompatLargeFile);
    if (unsupportedRoCompat != 0)
      limitations.Add($"Mounted ext2 writes reject read-only-compatible feature bits 0x{unsupportedRoCompat:X8}; metadata checksum/btree variants are not mutated.");
    if ((super.State & 0x0001) == 0)
      limitations.Add("The ext2 volume is not marked clean; run e2fsck before opening it writable.");
    if (super.BlocksCount > uint.MaxValue || super.DescriptorSize != 32)
      limitations.Add("Mounted ext2 writes currently require the classic 32-bit, 32-byte group-descriptor layout.");
    if (super.InodeSize < 128 || super.InodeSize > super.BlockSize)
      limitations.Add($"Mounted ext2 writes require inode sizes from 128 through one filesystem block; found {super.InodeSize}.");

    if (limitations.Count != 0)
      return limitations;

    var original = image.Position;
    try {
      var inodeNumbers = new HashSet<uint> { RootInode };
      foreach (var entry in entries) inodeNumbers.Add(entry.Inode);
      foreach (var number in inodeNumbers) {
        var inode = super.ReadInode(image, number);
        if ((inode.Flags & (InodeFlagIndex | InodeFlagExtents)) != 0)
          limitations.Add($"Inode {number} uses indexed-directory or extent-tree state; writable mounting stays fail-closed for that volume.");
        if (inode.FileAclBlock != 0)
          limitations.Add($"Inode {number} owns an external xattr/ACL block; mounted ext2 mutation does not yet maintain shared xattr block lifetimes.");
      }
    } catch (Exception e) when (e is InvalidDataException or IOException or OverflowException or NotSupportedException) {
      limitations.Add("Writable-profile validation failed: " + FirstLine(e.Message));
    } finally {
      image.Position = original;
    }

    return limitations.Distinct(StringComparer.Ordinal).ToArray();
  }

  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) {
    lock (_gate) {
      EnsureNotDisposed();
      var inode = Resolve(nodeId);
      return ToNodeInfo(inode);
    }
  }

  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      EnsureNotDisposed();
      ValidateName(name);
      var directory = ResolveDirectory(parentDirectory);
      var entry = FindDirectoryEntry(directory, name);
      if (entry is null) return null;
      return ToNodeId(ReadInode(entry.Value.Inode));
    }
  }

  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) {
    lock (_gate) {
      EnsureNotDisposed();
      var inode = ResolveDirectory(directory);
      var result = new List<FilesystemDirectoryEntry>();
      foreach (var entry in ReadDirectoryEntries(inode)) {
        if (entry.Name is "." or "..") continue;
        var child = ReadInode(entry.Inode);
        result.Add(new FilesystemDirectoryEntry(entry.Name, ToNodeId(child), child.Kind));
      }
      return result;
    }
  }

  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) {
    lock (_gate) {
      EnsureNotDisposed();
      if ((access & ~(FileAccess.Read | FileAccess.Write)) != 0)
        throw new ArgumentOutOfRangeException(nameof(access));
      var inode = Resolve(nodeId);
      if (inode.Kind != FilesystemNodeKind.RegularFile)
        throw new InvalidOperationException($"ext2 inode {inode.Number} is {inode.Kind}, not a regular file.");
      _openHandles[inode.Number] = _openHandles.GetValueOrDefault(inode.Number) + 1;
      return new FileHandle(this, nodeId, access);
    }
  }

  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      EnsureNotDisposed();
      ValidateName(name);
      var parent = ResolveDirectory(parentDirectory);
      EnsureNameAvailable(parent, name);
      var inode = AllocateInode(FilesystemNodeKind.RegularFile, DefaultFileMode, parent.Number);
      try {
        InsertDirectoryEntry(parent, name, inode.Number, FileType(FilesystemNodeKind.RegularFile));
      } catch {
        FreeInode(inode);
        throw;
      }
      FlushCore();
      return ToNodeId(inode);
    }
  }

  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      EnsureNotDisposed();
      ValidateName(name);
      var parent = ResolveDirectory(parentDirectory);
      EnsureNameAvailable(parent, name);
      var inode = AllocateInode(FilesystemNodeKind.Directory, DefaultDirectoryMode, parent.Number);
      try {
        var block = EnsureLogicalDataBlock(inode, 0);
        var entries = new List<DirectoryEntry> {
          new(inode.Number, FileType(FilesystemNodeKind.Directory), "."),
          new(parent.Number, FileType(FilesystemNodeKind.Directory), ".."),
        };
        WriteDirectoryBlock(block, entries);
        inode.Size = _geometry.BlockSize;
        Touch(inode, access: false, modify: true, change: true);
        WriteInode(inode);
        InsertDirectoryEntry(parent, name, inode.Number, FileType(FilesystemNodeKind.Directory));
        parent.Links = checked((ushort)(parent.Links + 1));
        Touch(parent, access: false, modify: true, change: true);
        WriteInode(parent);
      } catch {
        FreeInode(inode);
        throw;
      }
      FlushCore();
      return ToNodeId(inode);
    }
  }

  public void DeleteFile(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      EnsureNotDisposed();
      ValidateName(name);
      var parent = ResolveDirectory(parentDirectory);
      var entry = FindDirectoryEntry(parent, name)
        ?? throw new FileNotFoundException($"ext2 entry '{name}' does not exist.");
      var inode = ReadInode(entry.Inode);
      if (inode.Kind == FilesystemNodeKind.Directory)
        throw new IOException("Use RemoveDirectory for directories.");
      RemoveDirectoryEntry(parent, name, expectedInode: inode.Number);
      if (inode.Links == 0) throw new InvalidDataException($"ext2 inode {inode.Number} has zero links before unlink.");
      --inode.Links;
      Touch(inode, access: false, modify: false, change: true);
      WriteInode(inode);
      if (inode.Links == 0) {
        if (_openHandles.GetValueOrDefault(inode.Number) > 0)
          _pendingUnlinks.Add(inode.Number);
        else
          FreeInode(inode);
      }
      FlushCore();
    }
  }

  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      EnsureNotDisposed();
      ValidateName(name);
      var parent = ResolveDirectory(parentDirectory);
      var entry = FindDirectoryEntry(parent, name)
        ?? throw new DirectoryNotFoundException($"ext2 directory '{name}' does not exist.");
      var inode = ReadInode(entry.Inode);
      if (inode.Kind != FilesystemNodeKind.Directory)
        throw new IOException("The named entry is not a directory.");
      if (inode.Number == RootInode)
        throw new IOException("The ext2 root directory cannot be removed.");
      if (ReadDirectoryEntries(inode).Any(e => e.Name is not "." and not ".."))
        throw new IOException("Directory is not empty.");

      RemoveDirectoryEntry(parent, name, expectedInode: inode.Number);
      if (parent.Links == 0) throw new InvalidDataException($"ext2 directory inode {parent.Number} has invalid zero link count.");
      --parent.Links;
      Touch(parent, access: false, modify: true, change: true);
      WriteInode(parent);
      inode.Links = 0;
      WriteInode(inode);
      FreeInode(inode);
      FlushCore();
    }
  }

  public void Rename(
      FilesystemNodeId oldParent,
      string oldName,
      FilesystemNodeId newParent,
      string newName,
      bool replace) {
    lock (_gate) {
      EnsureNotDisposed();
      ValidateName(oldName);
      ValidateName(newName);
      var sourceParent = ResolveDirectory(oldParent);
      var destinationParent = ResolveDirectory(newParent);
      if (sourceParent.Number == destinationParent.Number && string.Equals(oldName, newName, StringComparison.Ordinal))
        return;

      var sourceEntry = FindDirectoryEntry(sourceParent, oldName)
        ?? throw new FileNotFoundException($"ext2 entry '{oldName}' does not exist.");
      var source = ReadInode(sourceEntry.Inode);
      var destinationEntry = FindDirectoryEntry(destinationParent, newName);
      if (destinationEntry is not null) {
        if (!replace) throw new IOException($"ext2 entry '{newName}' already exists.");
        if (destinationEntry.Value.Inode == source.Number) {
          RemoveDirectoryEntry(sourceParent, oldName, source.Number);
          FlushCore();
          return;
        }
        var destination = ReadInode(destinationEntry.Value.Inode);
        if ((source.Kind == FilesystemNodeKind.Directory) != (destination.Kind == FilesystemNodeKind.Directory))
          throw new IOException("A directory cannot replace a non-directory, or vice versa.");
        if (destination.Kind == FilesystemNodeKind.Directory) {
          if (ReadDirectoryEntries(destination).Any(e => e.Name is not "." and not ".."))
            throw new IOException("Destination directory is not empty.");
          RemoveDirectory(destinationParent, newName);
          destinationParent = ReadInode(destinationParent.Number);
        } else {
          DeleteFile(destinationParent, newName);
          destinationParent = ReadInode(destinationParent.Number);
        }
      }

      if (source.Kind == FilesystemNodeKind.Directory && sourceParent.Number != destinationParent.Number) {
        EnsureDirectoryMoveDoesNotCycle(source.Number, destinationParent.Number);
      }

      InsertDirectoryEntry(destinationParent, newName, source.Number, FileType(source.Kind));
      try {
        RemoveDirectoryEntry(sourceParent, oldName, source.Number);
      } catch {
        RemoveDirectoryEntry(destinationParent, newName, source.Number);
        throw;
      }

      if (source.Kind == FilesystemNodeKind.Directory && sourceParent.Number != destinationParent.Number) {
        RewriteDotDot(source, destinationParent.Number);
        if (sourceParent.Links == 0)
          throw new InvalidDataException($"ext2 directory inode {sourceParent.Number} has invalid zero link count.");
        --sourceParent.Links;
        destinationParent.Links = checked((ushort)(destinationParent.Links + 1));
        Touch(sourceParent, access: false, modify: true, change: true);
        Touch(destinationParent, access: false, modify: true, change: true);
        WriteInode(sourceParent);
        WriteInode(destinationParent);
      }

      Touch(source, access: false, modify: false, change: true);
      WriteInode(source);
      FlushCore();
    }
  }

  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName) {
    lock (_gate) {
      EnsureNotDisposed();
      ValidateName(newName);
      var inode = Resolve(existingNode);
      if (inode.Kind == FilesystemNodeKind.Directory)
        throw new IOException("Hard links to directories are not supported.");
      var parent = ResolveDirectory(newParent);
      EnsureNameAvailable(parent, newName);
      if (inode.Links == ushort.MaxValue)
        throw new IOException("ext2 inode link-count limit reached.");
      InsertDirectoryEntry(parent, newName, inode.Number, FileType(inode.Kind));
      ++inode.Links;
      Touch(inode, access: false, modify: false, change: true);
      WriteInode(inode);
      FlushCore();
    }
  }

  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target) {
    lock (_gate) {
      EnsureNotDisposed();
      ValidateName(name);
      ArgumentNullException.ThrowIfNull(target);
      var targetBytes = Encoding.UTF8.GetBytes(target);
      var parent = ResolveDirectory(parentDirectory);
      EnsureNameAvailable(parent, name);
      var inode = AllocateInode(FilesystemNodeKind.SymbolicLink, DefaultSymlinkMode, parent.Number);
      try {
        if (targetBytes.Length <= FastSymlinkCapacity) {
          Array.Clear(inode.Bytes, InodeBlockOffset, FastSymlinkCapacity);
          targetBytes.CopyTo(inode.Bytes.AsSpan(InodeBlockOffset, FastSymlinkCapacity));
        } else {
          WriteFileBytes(inode, 0, targetBytes);
        }
        inode.Size = targetBytes.Length;
        Touch(inode, access: false, modify: true, change: true);
        WriteInode(inode);
        InsertDirectoryEntry(parent, name, inode.Number, FileType(FilesystemNodeKind.SymbolicLink));
      } catch {
        FreeInode(inode);
        throw;
      }
      FlushCore();
      return ToNodeId(inode);
    }
  }

  public string ReadSymbolicLink(FilesystemNodeId nodeId) {
    lock (_gate) {
      EnsureNotDisposed();
      var inode = Resolve(nodeId);
      if (inode.Kind != FilesystemNodeKind.SymbolicLink)
        throw new InvalidOperationException("Node is not a symbolic link.");
      if (inode.Size > int.MaxValue) throw new NotSupportedException("Symbolic link target is too large.");
      var bytes = new byte[(int)inode.Size];
      if (inode.Sectors == 0 && bytes.Length <= FastSymlinkCapacity)
        inode.Bytes.AsSpan(InodeBlockOffset, bytes.Length).CopyTo(bytes);
      else
        ReadFileBytes(inode, 0, bytes);
      return Encoding.UTF8.GetString(bytes);
    }
  }

  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch) {
    ArgumentNullException.ThrowIfNull(patch);
    lock (_gate) {
      EnsureNotDisposed();
      if (patch.Created is not null)
        throw new NotSupportedException("Classic ext2 inodes do not have a portable creation-time field.");
      var inode = Resolve(nodeId);
      if (patch.NativeAttributes is { } native) {
        var requestedMode = (ushort)native;
        if ((requestedMode & ModeTypeMask) != (inode.Mode & ModeTypeMask))
          throw new IOException("Changing an ext2 inode object type through metadata is not supported.");
        var requestedFlags = (uint)(native >> 32);
        if (requestedFlags != inode.Flags)
          throw new NotSupportedException("Mounted ext2 metadata updates preserve inode behavior flags; only permission bits are mutable.");
        inode.Mode = (ushort)((inode.Mode & ModeTypeMask) | (requestedMode & ~ModeTypeMask));
      }
      if (patch.Accessed is { } atime) inode.AccessTime = ToUnixSeconds(atime);
      if (patch.Modified is { } mtime) inode.ModifyTime = ToUnixSeconds(mtime);
      inode.ChangeTime = NowSeconds();
      WriteInode(inode);
      FlushCore();
    }
  }

  public void Flush() {
    lock (_gate) {
      EnsureNotDisposed();
      FlushCore();
    }
  }

  public IFilesystemTransaction BeginTransaction()
    => throw new NotSupportedException("Classic ext2 mounted writes use direct synchronous metadata updates and do not expose transactions.");

  public void Dispose() {
    lock (_gate) {
      if (_disposed) return;
      foreach (var inodeNumber in _pendingUnlinks.ToArray()) {
        if (_openHandles.GetValueOrDefault(inodeNumber) != 0) continue;
        var inode = ReadInode(inodeNumber);
        if (inode.Links == 0) FreeInode(inode);
        _pendingUnlinks.Remove(inodeNumber);
      }
      FlushCore();
      _disposed = true;
      if (!_leaveOpen) _image.Dispose();
    }
  }

  private FilesystemNodeInfo ToNodeInfo(Inode inode) {
    var created = inode.CreationTime;
    return new FilesystemNodeInfo(
      ToNodeId(inode),
      inode.Kind,
      inode.Kind == FilesystemNodeKind.Directory ? 0 : inode.Size,
      checked((long)inode.Sectors * 512L),
      inode.Links,
      ((ulong)inode.Flags << 32) | inode.Mode,
      Created: created == 0 ? null : FromUnixSeconds(created),
      Modified: inode.ModifyTime == 0 ? null : FromUnixSeconds(inode.ModifyTime),
      Accessed: inode.AccessTime == 0 ? null : FromUnixSeconds(inode.AccessTime),
      Changed: inode.ChangeTime == 0 ? null : FromUnixSeconds(inode.ChangeTime));
  }

  private Inode Resolve(FilesystemNodeId nodeId) {
    if (nodeId.Value is 0 or > uint.MaxValue)
      throw new FileNotFoundException("Invalid ext2 inode identity.");
    var inode = ReadInode((uint)nodeId.Value);
    if (inode.Mode == 0 || inode.Generation != (uint)nodeId.Generation)
      throw new FileNotFoundException($"ext2 inode identity {nodeId.Value}:{nodeId.Generation} is stale.");
    return inode;
  }

  private Inode ResolveDirectory(FilesystemNodeId nodeId) {
    var inode = Resolve(nodeId);
    if (inode.Kind != FilesystemNodeKind.Directory)
      throw new DirectoryNotFoundException($"ext2 inode {inode.Number} is not a directory.");
    return inode;
  }

  private void EnsureNameAvailable(Inode directory, string name) {
    if (FindDirectoryEntry(directory, name) is not null)
      throw new IOException($"ext2 entry '{name}' already exists.");
  }

  private DirectoryEntry? FindDirectoryEntry(Inode directory, string name)
    => ReadDirectoryEntries(directory).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal)) is { Inode: not 0 } entry
      ? entry
      : null;

  private List<DirectoryEntry> ReadDirectoryEntries(Inode directory) {
    if (directory.Kind != FilesystemNodeKind.Directory)
      throw new InvalidDataException($"ext2 inode {directory.Number} is not a directory.");
    if ((directory.Flags & InodeFlagIndex) != 0)
      throw new NotSupportedException("Indexed ext2 directories are not mutable through the mounted writer.");
    if (directory.Size < 0 || directory.Size % _geometry.BlockSize != 0)
      throw new InvalidDataException($"ext2 directory inode {directory.Number} has non-block-aligned size {directory.Size}.");

    var result = new List<DirectoryEntry>();
    var blocks = checked((int)(directory.Size / _geometry.BlockSize));
    for (var logical = 0; logical < blocks; ++logical) {
      var physical = GetLogicalDataBlock(directory, logical);
      if (physical == 0)
        throw new InvalidDataException($"ext2 directory inode {directory.Number} has a sparse data block.");
      var bytes = ReadBlock(physical);
      foreach (var entry in ParseDirectoryBlock(bytes)) result.Add(entry);
    }
    return result;
  }

  private IEnumerable<DirectoryEntry> ParseDirectoryBlock(byte[] block) {
    var offset = 0;
    while (offset < block.Length) {
      if (offset > block.Length - 8)
        throw new InvalidDataException("ext2 directory entry header crosses the block boundary.");
      var inode = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(offset, 4));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(offset + 4, 2));
      if (recLen < 8 || (recLen & 3) != 0 || offset + recLen > block.Length)
        throw new InvalidDataException($"ext2 directory record at {offset} has invalid rec_len {recLen}.");
      var nameLen = block[offset + 6];
      var fileType = _geometry.HasFileType ? block[offset + 7] : (byte)0;
      if (nameLen > recLen - 8)
        throw new InvalidDataException($"ext2 directory record at {offset} has invalid name length {nameLen}.");
      if (inode != 0) {
        var name = Encoding.UTF8.GetString(block, offset + 8, nameLen);
        yield return new DirectoryEntry(inode, fileType, name);
      }
      offset += recLen;
    }
  }

  private void InsertDirectoryEntry(Inode directory, string name, uint inodeNumber, byte fileType) {
    var encoded = Encoding.UTF8.GetBytes(name);
    var required = DirectoryRecordSize(encoded.Length);
    var blocks = checked((int)(directory.Size / _geometry.BlockSize));
    for (var logical = 0; logical < blocks; ++logical) {
      var physical = GetLogicalDataBlock(directory, logical);
      var entries = ParseDirectoryBlock(ReadBlock(physical)).ToList();
      var used = entries.Sum(e => DirectoryRecordSize(Encoding.UTF8.GetByteCount(e.Name)));
      if (used + required > _geometry.BlockSize) continue;
      entries.Add(new DirectoryEntry(inodeNumber, fileType, name));
      WriteDirectoryBlock(physical, entries);
      Touch(directory, access: false, modify: true, change: true);
      WriteInode(directory);
      return;
    }

    var newLogical = blocks;
    var newBlock = EnsureLogicalDataBlock(directory, newLogical);
    WriteDirectoryBlock(newBlock, [new DirectoryEntry(inodeNumber, fileType, name)]);
    directory.Size = checked(directory.Size + _geometry.BlockSize);
    Touch(directory, access: false, modify: true, change: true);
    WriteInode(directory);
  }

  private void RemoveDirectoryEntry(Inode directory, string name, uint expectedInode) {
    var blocks = checked((int)(directory.Size / _geometry.BlockSize));
    for (var logical = 0; logical < blocks; ++logical) {
      var physical = GetLogicalDataBlock(directory, logical);
      var entries = ParseDirectoryBlock(ReadBlock(physical)).ToList();
      var index = entries.FindIndex(e => e.Inode == expectedInode && string.Equals(e.Name, name, StringComparison.Ordinal));
      if (index < 0) continue;
      entries.RemoveAt(index);
      WriteDirectoryBlock(physical, entries);
      TrimTrailingEmptyDirectoryBlocks(directory);
      Touch(directory, access: false, modify: true, change: true);
      WriteInode(directory);
      return;
    }
    throw new FileNotFoundException($"ext2 directory entry '{name}' disappeared during mutation.");
  }

  private void TrimTrailingEmptyDirectoryBlocks(Inode directory) {
    var blocks = checked((int)(directory.Size / _geometry.BlockSize));
    while (blocks > 1) {
      var physical = GetLogicalDataBlock(directory, blocks - 1);
      if (physical == 0) {
        --blocks;
        directory.Size -= _geometry.BlockSize;
        continue;
      }
      if (ParseDirectoryBlock(ReadBlock(physical)).Any()) break;
      --blocks;
      directory.Size -= _geometry.BlockSize;
      TrimInodeToBlockCount(directory, (ulong)blocks);
    }
  }

  private void WriteDirectoryBlock(uint blockNumber, IReadOnlyList<DirectoryEntry> entries) {
    var block = new byte[_geometry.BlockSize];
    if (entries.Count == 0) {
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4, 2), checked((ushort)_geometry.BlockSize));
      WriteBlock(blockNumber, block);
      return;
    }

    var offset = 0;
    for (var i = 0; i < entries.Count; ++i) {
      var entry = entries[i];
      var name = Encoding.UTF8.GetBytes(entry.Name);
      var minimum = DirectoryRecordSize(name.Length);
      var recLen = i == entries.Count - 1 ? _geometry.BlockSize - offset : minimum;
      if (recLen < minimum || recLen > ushort.MaxValue)
        throw new IOException("ext2 directory block has no room for the requested entry.");
      BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(offset, 4), entry.Inode);
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(offset + 4, 2), (ushort)recLen);
      block[offset + 6] = checked((byte)name.Length);
      block[offset + 7] = _geometry.HasFileType ? entry.FileType : (byte)0;
      name.CopyTo(block.AsSpan(offset + 8));
      offset += recLen;
    }
    WriteBlock(blockNumber, block);
  }

  private void RewriteDotDot(Inode directory, uint parentInode) {
    var first = GetLogicalDataBlock(directory, 0);
    if (first == 0) throw new InvalidDataException("ext2 directory has no first data block.");
    var entries = ParseDirectoryBlock(ReadBlock(first)).ToList();
    var index = entries.FindIndex(e => e.Name == "..");
    if (index < 0) throw new InvalidDataException("ext2 directory has no '..' entry.");
    entries[index] = entries[index] with { Inode = parentInode };
    WriteDirectoryBlock(first, entries);
    Touch(directory, access: false, modify: false, change: true);
    WriteInode(directory);
  }

  private void EnsureDirectoryMoveDoesNotCycle(uint movingDirectory, uint newParent) {
    var current = newParent;
    var visited = new HashSet<uint>();
    while (true) {
      if (current == movingDirectory)
        throw new IOException("Cannot move an ext2 directory into its own subtree.");
      if (current == RootInode) return;
      if (!visited.Add(current)) throw new InvalidDataException("ext2 '..' chain contains a cycle.");
      var inode = ReadInode(current);
      if (inode.Kind != FilesystemNodeKind.Directory)
        throw new InvalidDataException($"ext2 '..' chain reached non-directory inode {current}.");
      current = FindDirectoryEntry(inode, "..")?.Inode
        ?? throw new InvalidDataException($"ext2 directory inode {current} has no '..' entry.");
    }
  }

  private Inode AllocateInode(FilesystemNodeKind kind, ushort mode, uint parent) {
    for (uint group = 0; group < _geometry.GroupCount; ++group) {
      var descriptor = ReadGroupDescriptor(group);
      if (descriptor.FreeInodes == 0) continue;
      var bitmap = ReadBlock(descriptor.InodeBitmap);
      var first = group * _geometry.InodesPerGroup + 1;
      var count = Math.Min(_geometry.InodesPerGroup, _geometry.InodesCount - first + 1);
      for (uint local = 0; local < count; ++local) {
        var number = first + local;
        if (number < _geometry.FirstUserInode || TestBit(bitmap, local)) continue;

        var previous = ReadInodeBytes(number);
        var generation = BinaryPrimitives.ReadUInt32LittleEndian(previous.AsSpan(100, 4));
        generation = generation == uint.MaxValue ? 1u : generation + 1u;
        if (generation == 0) generation = 1;

        SetBit(bitmap, local, true);
        WriteBlock(descriptor.InodeBitmap, bitmap);
        descriptor.FreeInodes = checked((ushort)(descriptor.FreeInodes - 1));
        if (kind == FilesystemNodeKind.Directory)
          descriptor.UsedDirectories = checked((ushort)(descriptor.UsedDirectories + 1));
        WriteGroupDescriptor(group, descriptor);
        AdjustSuperFreeInodes(-1);

        var bytes = new byte[_geometry.InodeSize];
        var inode = new Inode(number, bytes) {
          Mode = mode,
          Links = kind == FilesystemNodeKind.Directory ? (ushort)2 : (ushort)1,
          Generation = generation,
          AccessTime = NowSeconds(),
          ChangeTime = NowSeconds(),
          ModifyTime = NowSeconds(),
        };
        WriteInode(inode);
        return inode;
      }
    }
    throw new IOException("ext2 has no free inode available.");
  }

  private void FreeInode(Inode inode) {
    if (inode.Number < _geometry.FirstUserInode && inode.Number != RootInode)
      throw new IOException($"Refusing to free reserved ext2 inode {inode.Number}.");
    FreeAllDataBlocks(inode);
    inode.Links = 0;
    inode.DeleteTime = NowSeconds();
    WriteInode(inode);

    var group = (inode.Number - 1) / _geometry.InodesPerGroup;
    var local = (inode.Number - 1) % _geometry.InodesPerGroup;
    var descriptor = ReadGroupDescriptor(group);
    var bitmap = ReadBlock(descriptor.InodeBitmap);
    if (!TestBit(bitmap, local))
      throw new InvalidDataException($"ext2 inode {inode.Number} is already free.");
    SetBit(bitmap, local, false);
    WriteBlock(descriptor.InodeBitmap, bitmap);
    descriptor.FreeInodes = checked((ushort)(descriptor.FreeInodes + 1));
    if (inode.Kind == FilesystemNodeKind.Directory) {
      if (descriptor.UsedDirectories == 0)
        throw new InvalidDataException($"ext2 group {group} has zero used-directory count while freeing inode {inode.Number}.");
      --descriptor.UsedDirectories;
    }
    WriteGroupDescriptor(group, descriptor);
    AdjustSuperFreeInodes(+1);
    _pendingUnlinks.Remove(inode.Number);
  }

  private uint AllocateBlock(uint preferredGroup) {
    for (uint pass = 0; pass < _geometry.GroupCount; ++pass) {
      var group = (preferredGroup + pass) % _geometry.GroupCount;
      var descriptor = ReadGroupDescriptor(group);
      if (descriptor.FreeBlocks == 0) continue;
      var bitmap = ReadBlock(descriptor.BlockBitmap);
      var groupStart = _geometry.FirstDataBlock + group * _geometry.BlocksPerGroup;
      if (groupStart >= _geometry.BlocksCount) continue;
      var count = Math.Min(_geometry.BlocksPerGroup, _geometry.BlocksCount - groupStart);
      for (uint local = 0; local < count; ++local) {
        if (TestBit(bitmap, local)) continue;
        var block = groupStart + local;
        SetBit(bitmap, local, true);
        WriteBlock(descriptor.BlockBitmap, bitmap);
        descriptor.FreeBlocks = checked((ushort)(descriptor.FreeBlocks - 1));
        WriteGroupDescriptor(group, descriptor);
        AdjustSuperFreeBlocks(-1);
        WriteBlock(block, new byte[_geometry.BlockSize]);
        return block;
      }
    }
    throw new IOException("ext2 has no free data block available.");
  }

  private void FreeBlock(uint blockNumber) {
    if (blockNumber < _geometry.FirstDataBlock || blockNumber >= _geometry.BlocksCount)
      throw new InvalidDataException($"ext2 block {blockNumber} is outside the allocatable range.");
    var group = (blockNumber - _geometry.FirstDataBlock) / _geometry.BlocksPerGroup;
    var local = (blockNumber - _geometry.FirstDataBlock) % _geometry.BlocksPerGroup;
    var descriptor = ReadGroupDescriptor(group);
    var bitmap = ReadBlock(descriptor.BlockBitmap);
    if (!TestBit(bitmap, local))
      throw new InvalidDataException($"ext2 block {blockNumber} is already free.");
    SetBit(bitmap, local, false);
    WriteBlock(descriptor.BlockBitmap, bitmap);
    descriptor.FreeBlocks = checked((ushort)(descriptor.FreeBlocks + 1));
    WriteGroupDescriptor(group, descriptor);
    AdjustSuperFreeBlocks(+1);
  }

  private uint GetLogicalDataBlock(Inode inode, ulong logicalBlock) {
    if (logicalBlock < DirectPointerCount)
      return inode.GetBlockPointer((int)logicalBlock);
    logicalBlock -= DirectPointerCount;
    var perBlock = (ulong)_geometry.PointersPerBlock;
    if (logicalBlock < perBlock)
      return ReadIndirectDataPointer(inode.GetBlockPointer(12), 1, logicalBlock);
    logicalBlock -= perBlock;
    var doubleCapacity = checked(perBlock * perBlock);
    if (logicalBlock < doubleCapacity)
      return ReadIndirectDataPointer(inode.GetBlockPointer(13), 2, logicalBlock);
    logicalBlock -= doubleCapacity;
    var tripleCapacity = checked(doubleCapacity * perBlock);
    if (logicalBlock < tripleCapacity)
      return ReadIndirectDataPointer(inode.GetBlockPointer(14), 3, logicalBlock);
    throw new IOException("ext2 logical block exceeds the classic triple-indirect address space.");
  }

  private uint ReadIndirectDataPointer(uint root, int depth, ulong index) {
    if (root == 0) return 0;
    var perBlock = (ulong)_geometry.PointersPerBlock;
    var current = root;
    for (var level = depth; level > 0; --level) {
      var divisor = Pow(perBlock, level - 1);
      var slot = checked((int)(index / divisor));
      index %= divisor;
      var block = ReadBlock(current);
      current = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(slot * 4, 4));
      if (current == 0) return 0;
    }
    return current;
  }

  private uint EnsureLogicalDataBlock(Inode inode, ulong logicalBlock) {
    var preferredGroup = (inode.Number - 1) / _geometry.InodesPerGroup;
    if (logicalBlock < DirectPointerCount) {
      var slot = (int)logicalBlock;
      var existing = inode.GetBlockPointer(slot);
      if (existing != 0) return existing;
      var allocated = AllocateBlock(preferredGroup);
      inode.SetBlockPointer(slot, allocated);
      AddSectors(inode, +_geometry.SectorsPerBlock);
      WriteInode(inode);
      return allocated;
    }

    logicalBlock -= DirectPointerCount;
    var perBlock = (ulong)_geometry.PointersPerBlock;
    var rootSlot = 12;
    var depth = 1;
    var capacity = perBlock;
    if (logicalBlock >= capacity) {
      logicalBlock -= capacity;
      rootSlot = 13;
      depth = 2;
      capacity = checked(perBlock * perBlock);
      if (logicalBlock >= capacity) {
        logicalBlock -= capacity;
        rootSlot = 14;
        depth = 3;
        capacity = checked(capacity * perBlock);
        if (logicalBlock >= capacity)
          throw new IOException("ext2 logical block exceeds the classic triple-indirect address space.");
      }
    }

    var current = inode.GetBlockPointer(rootSlot);
    if (current == 0) {
      current = AllocateBlock(preferredGroup);
      inode.SetBlockPointer(rootSlot, current);
      AddSectors(inode, +_geometry.SectorsPerBlock);
      WriteInode(inode);
    }

    for (var level = depth; level > 0; --level) {
      var divisor = Pow(perBlock, level - 1);
      var slot = checked((int)(logicalBlock / divisor));
      logicalBlock %= divisor;
      var pointerBlock = ReadBlock(current);
      var next = BinaryPrimitives.ReadUInt32LittleEndian(pointerBlock.AsSpan(slot * 4, 4));
      if (next == 0) {
        next = AllocateBlock(preferredGroup);
        BinaryPrimitives.WriteUInt32LittleEndian(pointerBlock.AsSpan(slot * 4, 4), next);
        WriteBlock(current, pointerBlock);
        AddSectors(inode, +_geometry.SectorsPerBlock);
        WriteInode(inode);
      }
      current = next;
    }
    return current;
  }

  private void TrimInodeToBlockCount(Inode inode, ulong keepBlocks) {
    for (var i = 0; i < DirectPointerCount; ++i) {
      if ((ulong)i < keepBlocks) continue;
      var block = inode.GetBlockPointer(i);
      if (block == 0) continue;
      FreeBlock(block);
      inode.SetBlockPointer(i, 0);
      AddSectors(inode, -_geometry.SectorsPerBlock);
    }

    var perBlock = (ulong)_geometry.PointersPerBlock;
    TrimIndirectRoot(inode, 12, depth: 1, baseLogical: DirectPointerCount, keepBlocks);
    TrimIndirectRoot(inode, 13, depth: 2, baseLogical: DirectPointerCount + perBlock, keepBlocks);
    TrimIndirectRoot(inode, 14, depth: 3, baseLogical: DirectPointerCount + perBlock + perBlock * perBlock, keepBlocks);
    WriteInode(inode);
  }

  private void TrimIndirectRoot(Inode inode, int rootSlot, int depth, ulong baseLogical, ulong keepBlocks) {
    var root = inode.GetBlockPointer(rootSlot);
    if (root == 0) return;
    if (TrimIndirectBlock(inode, root, depth, baseLogical, keepBlocks)) {
      FreeBlock(root);
      inode.SetBlockPointer(rootSlot, 0);
      AddSectors(inode, -_geometry.SectorsPerBlock);
    }
  }

  private bool TrimIndirectBlock(Inode inode, uint blockNumber, int depth, ulong baseLogical, ulong keepBlocks) {
    var pointers = ReadBlock(blockNumber);
    var perBlock = (ulong)_geometry.PointersPerBlock;
    var childCapacity = Pow(perBlock, depth - 1);
    var any = false;
    for (var slot = 0; slot < _geometry.PointersPerBlock; ++slot) {
      var offset = slot * 4;
      var child = BinaryPrimitives.ReadUInt32LittleEndian(pointers.AsSpan(offset, 4));
      if (child == 0) continue;
      var childBase = checked(baseLogical + (ulong)slot * childCapacity);
      if (childBase >= keepBlocks) {
        if (depth == 1) {
          FreeBlock(child);
          AddSectors(inode, -_geometry.SectorsPerBlock);
        } else {
          FreeIndirectTree(inode, child, depth - 1);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(pointers.AsSpan(offset, 4), 0);
        continue;
      }
      if (depth > 1 && childBase + childCapacity > keepBlocks && TrimIndirectBlock(inode, child, depth - 1, childBase, keepBlocks)) {
        FreeBlock(child);
        AddSectors(inode, -_geometry.SectorsPerBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(pointers.AsSpan(offset, 4), 0);
        continue;
      }
      any = true;
    }
    WriteBlock(blockNumber, pointers);
    return !any && !ContainsNonZeroPointer(pointers);
  }

  private void FreeIndirectTree(Inode inode, uint blockNumber, int depth) {
    var pointers = ReadBlock(blockNumber);
    for (var slot = 0; slot < _geometry.PointersPerBlock; ++slot) {
      var child = BinaryPrimitives.ReadUInt32LittleEndian(pointers.AsSpan(slot * 4, 4));
      if (child == 0) continue;
      if (depth == 1) {
        FreeBlock(child);
        AddSectors(inode, -_geometry.SectorsPerBlock);
      } else {
        FreeIndirectTree(inode, child, depth - 1);
      }
    }
    FreeBlock(blockNumber);
    AddSectors(inode, -_geometry.SectorsPerBlock);
  }

  private void FreeAllDataBlocks(Inode inode) {
    for (var i = 0; i < DirectPointerCount; ++i) {
      var block = inode.GetBlockPointer(i);
      if (block == 0) continue;
      FreeBlock(block);
      inode.SetBlockPointer(i, 0);
      AddSectors(inode, -_geometry.SectorsPerBlock);
    }
    for (var slot = 12; slot <= 14; ++slot) {
      var root = inode.GetBlockPointer(slot);
      if (root == 0) continue;
      FreeIndirectTree(inode, root, slot - 11);
      inode.SetBlockPointer(slot, 0);
    }
    inode.Size = 0;
    WriteInode(inode);
  }

  private int ReadFileBytes(Inode inode, long offset, Span<byte> destination) {
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (offset >= inode.Size || destination.IsEmpty) return 0;
    var remaining = (int)Math.Min(destination.Length, inode.Size - offset);
    var copied = 0;
    while (copied < remaining) {
      var absolute = offset + copied;
      var logical = (ulong)(absolute / _geometry.BlockSize);
      var inBlock = (int)(absolute % _geometry.BlockSize);
      var count = Math.Min(remaining - copied, _geometry.BlockSize - inBlock);
      var physical = GetLogicalDataBlock(inode, logical);
      if (physical == 0)
        destination.Slice(copied, count).Clear();
      else
        ReadBlock(physical).AsSpan(inBlock, count).CopyTo(destination.Slice(copied, count));
      copied += count;
    }
    return copied;
  }

  private void WriteFileBytes(Inode inode, long offset, ReadOnlySpan<byte> source) {
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    var end = checked(offset + source.Length);
    var copied = 0;
    while (copied < source.Length) {
      var absolute = offset + copied;
      var logical = (ulong)(absolute / _geometry.BlockSize);
      var inBlock = (int)(absolute % _geometry.BlockSize);
      var count = Math.Min(source.Length - copied, _geometry.BlockSize - inBlock);
      var physical = EnsureLogicalDataBlock(inode, logical);
      if (inBlock == 0 && count == _geometry.BlockSize) {
        WriteBlock(physical, source.Slice(copied, count));
      } else {
        var block = ReadBlock(physical);
        source.Slice(copied, count).CopyTo(block.AsSpan(inBlock, count));
        WriteBlock(physical, block);
      }
      copied += count;
    }
    if (end > inode.Size) inode.Size = end;
    Touch(inode, access: false, modify: true, change: true);
    WriteInode(inode);
  }

  private void SetFileLength(Inode inode, long length) {
    if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
    if (length == inode.Size) return;
    if (length < inode.Size) {
      var keepBlocks = length == 0 ? 0UL : checked((ulong)((length + _geometry.BlockSize - 1) / _geometry.BlockSize));
      if (length != 0 && length % _geometry.BlockSize != 0) {
        var logical = checked((ulong)(length / _geometry.BlockSize));
        var physical = GetLogicalDataBlock(inode, logical);
        if (physical != 0) {
          var block = ReadBlock(physical);
          block.AsSpan((int)(length % _geometry.BlockSize)).Clear();
          WriteBlock(physical, block);
        }
      }
      TrimInodeToBlockCount(inode, keepBlocks);
    }
    inode.Size = length;
    Touch(inode, access: false, modify: true, change: true);
    WriteInode(inode);
  }

  private Inode ReadInode(uint number) => new(number, ReadInodeBytes(number));

  private byte[] ReadInodeBytes(uint number) {
    if (number == 0 || number > _geometry.InodesCount)
      throw new FileNotFoundException($"ext2 inode {number} is outside the volume.");
    var group = (number - 1) / _geometry.InodesPerGroup;
    var local = (number - 1) % _geometry.InodesPerGroup;
    var descriptor = ReadGroupDescriptor(group);
    var offset = checked((long)descriptor.InodeTable * _geometry.BlockSize + (long)local * _geometry.InodeSize);
    var bytes = new byte[_geometry.InodeSize];
    ReadExactly(offset, bytes);
    return bytes;
  }

  private void WriteInode(Inode inode) {
    var group = (inode.Number - 1) / _geometry.InodesPerGroup;
    var local = (inode.Number - 1) % _geometry.InodesPerGroup;
    var descriptor = ReadGroupDescriptor(group);
    var offset = checked((long)descriptor.InodeTable * _geometry.BlockSize + (long)local * _geometry.InodeSize);
    WriteExactly(offset, inode.Bytes);
  }

  private GroupDescriptor ReadGroupDescriptor(uint group) {
    if (group >= _geometry.GroupCount) throw new ArgumentOutOfRangeException(nameof(group));
    Span<byte> bytes = stackalloc byte[32];
    ReadExactly(_geometry.BgdtOffset + (long)group * 32, bytes);
    return new GroupDescriptor(
      BinaryPrimitives.ReadUInt32LittleEndian(bytes[0..4]),
      BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]),
      BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]),
      BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..14]),
      BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..16]),
      BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..18]));
  }

  private void WriteGroupDescriptor(uint group, GroupDescriptor descriptor) {
    Span<byte> bytes = stackalloc byte[32];
    ReadExactly(_geometry.BgdtOffset + (long)group * 32, bytes);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes[0..4], descriptor.BlockBitmap);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..8], descriptor.InodeBitmap);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..12], descriptor.InodeTable);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes[12..14], descriptor.FreeBlocks);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes[14..16], descriptor.FreeInodes);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes[16..18], descriptor.UsedDirectories);
    WriteExactly(_geometry.BgdtOffset + (long)group * 32, bytes);
  }

  private void AdjustSuperFreeBlocks(int delta) => AdjustSuperCounter(12, delta, "free block");
  private void AdjustSuperFreeInodes(int delta) => AdjustSuperCounter(16, delta, "free inode");

  private void AdjustSuperCounter(int offset, int delta, string name) {
    Span<byte> bytes = stackalloc byte[4];
    ReadExactly(1024 + offset, bytes);
    var current = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    var next = delta < 0
      ? checked(current - (uint)-delta)
      : checked(current + (uint)delta);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, next);
    WriteExactly(1024 + offset, bytes);
  }

  private byte[] ReadBlock(uint blockNumber) {
    if (blockNumber >= _geometry.BlocksCount)
      throw new InvalidDataException($"ext2 block {blockNumber} lies outside the image.");
    var bytes = new byte[_geometry.BlockSize];
    ReadExactly(checked((long)blockNumber * _geometry.BlockSize), bytes);
    return bytes;
  }

  private void WriteBlock(uint blockNumber, ReadOnlySpan<byte> bytes) {
    if (blockNumber >= _geometry.BlocksCount)
      throw new InvalidDataException($"ext2 block {blockNumber} lies outside the image.");
    if (bytes.Length != _geometry.BlockSize)
      throw new ArgumentException($"ext2 block writes must be exactly {_geometry.BlockSize} bytes.", nameof(bytes));
    WriteExactly(checked((long)blockNumber * _geometry.BlockSize), bytes);
  }

  private void ReadExactly(long offset, Span<byte> destination) {
    _image.Position = offset;
    _image.ReadExactly(destination);
  }

  private void WriteExactly(long offset, ReadOnlySpan<byte> source) {
    _image.Position = offset;
    _image.Write(source);
  }

  private void FlushCore() => _image.Flush();

  private void CloseHandle(uint inodeNumber) {
    lock (_gate) {
      if (!_openHandles.TryGetValue(inodeNumber, out var count) || count <= 0) return;
      if (count == 1) _openHandles.Remove(inodeNumber);
      else _openHandles[inodeNumber] = count - 1;
      if (_pendingUnlinks.Contains(inodeNumber) && _openHandles.GetValueOrDefault(inodeNumber) == 0) {
        var inode = ReadInode(inodeNumber);
        if (inode.Links == 0) FreeInode(inode);
        _pendingUnlinks.Remove(inodeNumber);
        FlushCore();
      }
    }
  }

  private static FilesystemNodeId ToNodeId(Inode inode) => new(inode.Number, inode.Generation);

  private static byte FileType(FilesystemNodeKind kind) => kind switch {
    FilesystemNodeKind.RegularFile => 1,
    FilesystemNodeKind.Directory => 2,
    FilesystemNodeKind.CharacterDevice => 3,
    FilesystemNodeKind.BlockDevice => 4,
    FilesystemNodeKind.Fifo => 5,
    FilesystemNodeKind.Socket => 6,
    FilesystemNodeKind.SymbolicLink => 7,
    _ => 0,
  };

  private static int DirectoryRecordSize(int nameBytes) => (8 + nameBytes + 3) & ~3;

  private static void ValidateName(string name) {
    ArgumentNullException.ThrowIfNull(name);
    if (name.Length == 0 || name is "." or "..") throw new ArgumentException("Invalid ext2 directory-entry name.", nameof(name));
    if (name.Contains('/') || name.Contains('\\') || name.Contains('\0'))
      throw new ArgumentException("ext2 directory-entry names cannot contain separators or NUL.", nameof(name));
    if (Encoding.UTF8.GetByteCount(name) > byte.MaxValue)
      throw new ArgumentException("ext2 directory-entry names are limited to 255 bytes.", nameof(name));
  }

  private static uint NowSeconds() => ToUnixSeconds(DateTimeOffset.UtcNow);

  private static uint ToUnixSeconds(DateTimeOffset value) {
    var seconds = value.ToUnixTimeSeconds();
    if (seconds < 0 || seconds > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
    return (uint)seconds;
  }

  private static DateTimeOffset? FromUnixSeconds(uint seconds) {
    try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
    catch (ArgumentOutOfRangeException) { return null; }
  }

  private static void Touch(Inode inode, bool access, bool modify, bool change) {
    var now = NowSeconds();
    if (access) inode.AccessTime = now;
    if (modify) inode.ModifyTime = now;
    if (change) inode.ChangeTime = now;
  }

  private static ulong Pow(ulong value, int exponent) {
    var result = 1UL;
    for (var i = 0; i < exponent; ++i) result = checked(result * value);
    return result;
  }

  private static bool TestBit(byte[] bitmap, uint bit)
    => (bitmap[checked((int)(bit >> 3))] & (1 << (int)(bit & 7))) != 0;

  private static void SetBit(byte[] bitmap, uint bit, bool value) {
    var index = checked((int)(bit >> 3));
    var mask = (byte)(1 << (int)(bit & 7));
    if (value) bitmap[index] |= mask;
    else bitmap[index] &= (byte)~mask;
  }

  private static bool ContainsNonZeroPointer(byte[] block) {
    for (var offset = 0; offset < block.Length; offset += 4)
      if (BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(offset, 4)) != 0) return true;
    return false;
  }

  private static void AddSectors(Inode inode, int delta) {
    var next = delta < 0
      ? checked(inode.Sectors - (uint)-delta)
      : checked(inode.Sectors + (uint)delta);
    inode.Sectors = next;
  }

  private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }

  private readonly record struct DirectoryEntry(uint Inode, byte FileType, string Name);

  private sealed class FileHandle(
      Ext2MountedFilesystemSession session,
      FilesystemNodeId nodeId,
      FileAccess access) : IFilesystemFileHandle {
    private bool _disposed;

    public FilesystemNodeId NodeId => nodeId;

    public long Length {
      get {
        lock (session._gate) {
          ObjectDisposedException.ThrowIf(_disposed, this);
          return session.Resolve(nodeId).Size;
        }
      }
    }

    public int Read(long offset, Span<byte> destination) {
      lock (session._gate) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((access & FileAccess.Read) == 0) throw new UnauthorizedAccessException("File handle was not opened for reading.");
        var inode = session.Resolve(nodeId);
        return session.ReadFileBytes(inode, offset, destination);
      }
    }

    public void Write(long offset, ReadOnlySpan<byte> source) {
      lock (session._gate) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((access & FileAccess.Write) == 0) throw new UnauthorizedAccessException("File handle was not opened for writing.");
        var inode = session.Resolve(nodeId);
        session.WriteFileBytes(inode, offset, source);
      }
    }

    public void SetLength(long length) {
      lock (session._gate) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((access & FileAccess.Write) == 0) throw new UnauthorizedAccessException("File handle was not opened for writing.");
        var inode = session.Resolve(nodeId);
        session.SetFileLength(inode, length);
      }
    }

    public void Flush() {
      lock (session._gate) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        session.FlushCore();
      }
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;
      session.CloseHandle((uint)nodeId.Value);
    }
  }

  private sealed class Inode(uint number, byte[] bytes) {
    public uint Number { get; } = number;
    public byte[] Bytes { get; } = bytes;

    public ushort Mode {
      get => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(0, 2));
      set => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(0, 2), value);
    }

    public FilesystemNodeKind Kind => (Mode & ModeTypeMask) switch {
      0x4000 => FilesystemNodeKind.Directory,
      0x8000 => FilesystemNodeKind.RegularFile,
      0xA000 => FilesystemNodeKind.SymbolicLink,
      0x2000 => FilesystemNodeKind.CharacterDevice,
      0x6000 => FilesystemNodeKind.BlockDevice,
      0x1000 => FilesystemNodeKind.Fifo,
      0xC000 => FilesystemNodeKind.Socket,
      _ => FilesystemNodeKind.Unknown,
    };

    public long Size {
      get {
        var lo = BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(4, 4));
        var hi = Kind == FilesystemNodeKind.RegularFile && Bytes.Length >= 112
          ? BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(108, 4))
          : 0u;
        return checked((long)(((ulong)hi << 32) | lo));
      }
      set {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(4, 4), (uint)value);
        if (Kind == FilesystemNodeKind.RegularFile && Bytes.Length >= 112)
          BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(108, 4), (uint)((ulong)value >> 32));
        else if ((ulong)value > uint.MaxValue)
          throw new IOException("This ext2 inode type cannot represent a size above 4 GiB.");
      }
    }

    public uint AccessTime {
      get => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(8, 4));
      set => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(8, 4), value);
    }

    public uint ChangeTime {
      get => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(12, 4));
      set => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(12, 4), value);
    }

    public uint ModifyTime {
      get => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(16, 4));
      set => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(16, 4), value);
    }

    public uint DeleteTime {
      get => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(20, 4));
      set => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(20, 4), value);
    }

    public ushort Links {
      get => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(26, 2));
      set => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(26, 2), value);
    }

    public uint Sectors {
      get => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(28, 4));
      set => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(28, 4), value);
    }

    public uint Flags {
      get => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(32, 4));
      set => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(32, 4), value);
    }

    public uint Generation {
      get => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(100, 4));
      set => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(100, 4), value);
    }

    public uint FileAclBlock => Bytes.Length >= 108
      ? BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(104, 4))
      : 0;

    public uint CreationTime => Bytes.Length >= 148
      ? BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(144, 4))
      : 0;

    public uint GetBlockPointer(int index)
      => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(InodeBlockOffset + index * 4, 4));

    public void SetBlockPointer(int index, uint value)
      => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(InodeBlockOffset + index * 4, 4), value);
  }

  private sealed record GroupDescriptor(
    uint BlockBitmap,
    uint InodeBitmap,
    uint InodeTable,
    ushort FreeBlocks,
    ushort FreeInodes,
    ushort UsedDirectories) {
    public ushort FreeBlocks { get; set; } = FreeBlocks;
    public ushort FreeInodes { get; set; } = FreeInodes;
    public ushort UsedDirectories { get; set; } = UsedDirectories;
  }

  private sealed record Ext2Geometry(
    uint InodesCount,
    uint BlocksCount,
    uint FirstDataBlock,
    uint BlocksPerGroup,
    uint InodesPerGroup,
    uint FirstUserInode,
    int BlockSize,
    int InodeSize,
    uint FeatureIncompat,
    long BgdtOffset,
    uint GroupCount) {
    public bool HasFileType => (FeatureIncompat & IncompatFileType) != 0;
    public int PointersPerBlock => BlockSize / 4;
    public int SectorsPerBlock => BlockSize / 512;

    public static Ext2Geometry Read(Stream image) {
      var original = image.Position;
      try {
        Span<byte> sb = stackalloc byte[1024];
        image.Position = 1024;
        image.ReadExactly(sb);
        if (BinaryPrimitives.ReadUInt16LittleEndian(sb[56..58]) != 0xEF53)
          throw new InvalidDataException("ext2 superblock magic is invalid.");
        var inodes = BinaryPrimitives.ReadUInt32LittleEndian(sb[0..4]);
        var blocks = BinaryPrimitives.ReadUInt32LittleEndian(sb[4..8]);
        var firstData = BinaryPrimitives.ReadUInt32LittleEndian(sb[20..24]);
        var shift = BinaryPrimitives.ReadUInt32LittleEndian(sb[24..28]);
        if (shift > 6) throw new NotSupportedException("ext2 block size is unsupported.");
        var blockSize = checked(1024 << (int)shift);
        var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb[32..36]);
        var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb[40..44]);
        var rev = BinaryPrimitives.ReadUInt32LittleEndian(sb[76..80]);
        var firstUser = rev == 0 ? 11u : BinaryPrimitives.ReadUInt32LittleEndian(sb[84..88]);
        if (firstUser == 0) firstUser = 11;
        var inodeSize = rev == 0 ? 128 : BinaryPrimitives.ReadUInt16LittleEndian(sb[88..90]);
        if (inodeSize == 0) inodeSize = 128;
        var incompat = BinaryPrimitives.ReadUInt32LittleEndian(sb[96..100]);
        if (blocks <= firstData || blocksPerGroup == 0 || inodesPerGroup == 0)
          throw new InvalidDataException("ext2 group geometry is invalid.");
        var groups = checked((blocks - firstData + blocksPerGroup - 1) / blocksPerGroup);
        var bgdt = checked((long)(firstData + 1) * blockSize);
        return new Ext2Geometry(inodes, blocks, firstData, blocksPerGroup, inodesPerGroup,
          firstUser, blockSize, inodeSize, incompat, bgdt, groups);
      } finally {
        image.Position = original;
      }
    }
  }
}