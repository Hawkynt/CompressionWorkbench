#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Fat;

/// <summary>
/// Mount-grade FAT12/16/32 mutation session. The namespace is indexed once, but
/// file contents and allocation metadata stay on the backing random-access
/// stream: no whole-volume byte[] snapshot is created.
/// </summary>
internal sealed class FatWritableFilesystemSession : IFilesystemSession {
  private const byte AttrReadOnly = 0x01;
  private const byte AttrVolume = 0x08;
  private const byte AttrDirectory = 0x10;
  private const byte AttrArchive = 0x20;
  private const byte AttrLongName = 0x0F;

  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly FatDriverGeometry _geometry;
  private readonly object _gate = new();
  private readonly Dictionary<FilesystemNodeId, NodeState> _nodes = [];
  private readonly Dictionary<FilesystemNodeId, Dictionary<string, FilesystemNodeId>> _children = [];
  private readonly int _rootEntryCount;
  private readonly int _fsInfoSector;
  private ulong _nextNodeId = 2;
  private bool _disposed;
  private bool _volumeDirty;
  private bool _hardError;

  public FatWritableFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(profile);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Writable FAT mounting requires a readable, writable, seekable backing stream.", nameof(image));
    if (!profile.CanMountWritable)
      throw new NotSupportedException("This FAT media profile is not safe for writable mounting.");

    _image = image;
    _leaveOpen = leaveOpen;
    Profile = profile;
    _geometry = FatDriverGeometry.Parse(image);

    Span<byte> boot = stackalloc byte[512];
    ReadAt(0, boot);
    _rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(boot[17..19]);
    _fsInfoSector = _geometry.FatType == 32
      ? BinaryPrimitives.ReadUInt16LittleEndian(boot[48..50])
      : 0;

    var root = new NodeState(
      RootNodeId,
      default,
      string.Empty,
      isDirectory: true,
      _geometry.FatType == 32 ? _geometry.RootCluster : 0,
      0,
      AttrDirectory,
      []);
    _nodes.Add(root.Id, root);
    _children.Add(root.Id, new Dictionary<string, FilesystemNodeId>(StringComparer.OrdinalIgnoreCase));
    IndexDirectory(root, []);
  }

  public FilesystemDriverProfile Profile { get; }
  public FilesystemNodeId RootNodeId { get; } = new(1, 1);

  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) {
    lock (_gate) {
      ThrowIfDisposed();
      var state = RequireState(nodeId);
      var allocated = state.FirstCluster < 2
        ? 0L
        : checked((long)GetChain(state).Count * _geometry.ClusterSize);
      return new FilesystemNodeInfo(
        state.Id,
        state.IsDirectory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile,
        state.IsDirectory ? 0 : state.Size,
        allocated,
        state.Linked ? 1U : 0U,
        state.Attributes,
        Modified: state.Modified);
    }
  }

  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) {
    ArgumentNullException.ThrowIfNull(name);
    lock (_gate) {
      ThrowIfDisposed();
      var parent = RequireDirectory(parentDirectory);
      return _children[parent.Id].TryGetValue(name, out var id) ? id : null;
    }
  }

  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) {
    lock (_gate) {
      ThrowIfDisposed();
      var parent = RequireDirectory(directory);
      return _children[parent.Id]
        .Select(pair => {
          var child = RequireState(pair.Value);
          return new FilesystemDirectoryEntry(
            child.Name,
            child.Id,
            child.IsDirectory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile);
        })
        .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }
  }

  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) {
    lock (_gate) {
      ThrowIfDisposed();
      var state = RequireState(nodeId);
      if (state.IsDirectory)
        throw new UnauthorizedAccessException("A FAT directory cannot be opened as a regular file.");
      if (!state.Linked && state.OpenHandles == 0)
        throw new FileNotFoundException($"FAT node {nodeId.Value}:{nodeId.Generation} is no longer linked.");
      ++state.OpenHandles;
      return new FileHandle(this, state, access);
    }
  }

  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      ThrowIfDisposed();
      return Mutate(() => {
        var parent = RequireDirectory(parentDirectory);
        ValidateNewName(parent, name);
        var slots = ReserveSlots(parent, name, excluded: null, out var encoded);
        PatchShortEntry(encoded, 0, 0, AttrArchive);
        WriteSlotBlob(slots, encoded);
        FlushBacking();
        return AddState(parent, name, isDirectory: false, 0, 0, AttrArchive, slots).Id;
      });
    }
  }

  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      ThrowIfDisposed();
      return Mutate(() => {
        var parent = RequireDirectory(parentDirectory);
        ValidateNewName(parent, name);
        var slots = ReserveSlots(parent, name, excluded: null, out var encoded);
        var cluster = AllocateClusters(1)[0];

        // Initialize detached storage first, then reserve it in the FAT, and only
        // then publish the directory entry. A crash cannot expose uninitialized data.
        InitializeDirectoryCluster(cluster, parent);
        FlushBacking();
        WriteFatEntryAll(cluster, EndOfChain());
        AdjustFsInfo(-1, cluster + 1);
        FlushBacking();

        PatchShortEntry(encoded, cluster, 0, AttrDirectory);
        WriteSlotBlob(slots, encoded);
        FlushBacking();

        var state = AddState(parent, name, isDirectory: true, cluster, 0, AttrDirectory, slots);
        state.Chain = [cluster];
        _children.Add(state.Id, new Dictionary<string, FilesystemNodeId>(StringComparer.OrdinalIgnoreCase));
        return state.Id;
      });
    }
  }

  public void DeleteFile(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      ThrowIfDisposed();
      Mutate(() => {
        var parent = RequireDirectory(parentDirectory);
        var state = RequireChild(parent, name);
        if (state.IsDirectory) throw new UnauthorizedAccessException($"'{name}' is a directory.");
        Unlink(state);
      });
    }
  }

  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      ThrowIfDisposed();
      Mutate(() => {
        var parent = RequireDirectory(parentDirectory);
        var state = RequireChild(parent, name);
        if (!state.IsDirectory) throw new UnauthorizedAccessException($"'{name}' is not a directory.");
        if (_children[state.Id].Count != 0) throw new IOException($"FAT directory '{name}' is not empty.");
        Unlink(state);
        _children.Remove(state.Id);
      });
    }
  }

  public void Rename(
      FilesystemNodeId oldParent,
      string oldName,
      FilesystemNodeId newParent,
      string newName,
      bool replace) {
    lock (_gate) {
      ThrowIfDisposed();
      Mutate(() => {
        var sourceParent = RequireDirectory(oldParent);
        var targetParent = RequireDirectory(newParent);
        var source = RequireChild(sourceParent, oldName);
        ValidateName(newName);

        if (source.IsDirectory && (targetParent.Id == source.Id || IsDescendantOf(targetParent, source)))
          throw new IOException("A FAT directory cannot be moved into itself or one of its descendants.");

        var target = _children[targetParent.Id].TryGetValue(newName, out var targetId)
          ? RequireState(targetId)
          : null;
        if (target != null && target.Id == source.Id) {
          if (string.Equals(source.Name, newName, StringComparison.Ordinal)) return;
          target = null;
        }
        if (target != null && !replace) throw new IOException($"FAT entry '{newName}' already exists.");
        if (target != null && target.IsDirectory != source.IsDirectory)
          throw new IOException("FAT rename replacement requires source and target to have the same node kind.");
        if (target is { IsDirectory: true } && _children[target.Id].Count != 0)
          throw new IOException($"FAT target directory '{newName}' is not empty.");

        // Publish the new name first. FAT has no journal; the safe failure mode is
        // two aliases, never a source that disappears before its replacement exists.
        var slots = ReserveSlots(targetParent, newName, source, out var encoded);
        PatchShortEntry(encoded, source.FirstCluster, source.Size, source.Attributes);
        WriteSlotBlob(slots, encoded);
        FlushBacking();

        var oldSlots = source.SlotOffsets.ToArray();
        var oldParentId = source.ParentId;
        var oldNameCanonical = source.Name;

        // For a cross-directory directory move, make '..' agree with the already
        // published new alias before removing the old alias.
        if (source.IsDirectory && oldParentId != targetParent.Id)
          RewriteDotDot(source, targetParent);

        source.ParentId = targetParent.Id;
        source.Name = newName;
        source.SlotOffsets = slots.ToList();
        source.Modified = DateTimeOffset.Now;
        _children[sourceParent.Id].Remove(oldNameCanonical);
        _children[targetParent.Id][newName] = source.Id;

        var reused = slots.ToHashSet();
        MarkSlotsDeleted(oldSlots.Where(offset => !reused.Contains(offset)));
        if (target != null) {
          MarkSlotsDeleted(target.SlotOffsets);
          target.Linked = false;
          _children[targetParent.Id].Remove(target.Name);
          _children[targetParent.Id][newName] = source.Id;
          if (target.IsDirectory) _children.Remove(target.Id);
          FinalizeUnlinkedIfPossible(target);
        }
        FlushBacking();
      });
    }
  }

  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName)
    => throw new NotSupportedException("FAT has no hard-link representation.");

  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target)
    => throw new NotSupportedException("FAT has no symbolic-link representation.");

  public string ReadSymbolicLink(FilesystemNodeId nodeId)
    => throw new NotSupportedException("FAT has no symbolic-link representation.");

  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch) {
    ArgumentNullException.ThrowIfNull(patch);
    lock (_gate) {
      ThrowIfDisposed();
      Mutate(() => {
        var state = RequireState(nodeId);
        if (!state.Linked) throw new FileNotFoundException("Cannot update metadata for an unlinked FAT node.");
        if (state.Id == RootNodeId)
          throw new NotSupportedException("Generic FAT root metadata updates are not represented by a normal directory entry.");

        var shortOffset = state.SlotOffsets[^1];
        Span<byte> entry = stackalloc byte[32];
        ReadAt(shortOffset, entry);

        if (patch.NativeAttributes is { } raw) {
          var attrs = checked((byte)(raw & 0x3F));
          if ((attrs & AttrVolume) != 0 || (attrs & AttrLongName) == AttrLongName)
            throw new ArgumentOutOfRangeException(nameof(patch), "Volume-label/LFN attribute combinations are not valid regular FAT nodes.");
          attrs = state.IsDirectory ? (byte)(attrs | AttrDirectory) : (byte)(attrs & ~AttrDirectory);
          entry[11] = attrs;
          state.Attributes = attrs;
        }
        if (patch.Created is { } created) {
          EncodeDateTime(created, out var date, out var time, out var tenths);
          entry[13] = tenths;
          BinaryPrimitives.WriteUInt16LittleEndian(entry[14..16], time);
          BinaryPrimitives.WriteUInt16LittleEndian(entry[16..18], date);
        }
        if (patch.Accessed is { } accessed) {
          EncodeDate(accessed, out var date);
          BinaryPrimitives.WriteUInt16LittleEndian(entry[18..20], date);
        }
        if (patch.Modified is { } modified) {
          EncodeDateTime(modified, out var date, out var time, out _);
          BinaryPrimitives.WriteUInt16LittleEndian(entry[22..24], time);
          BinaryPrimitives.WriteUInt16LittleEndian(entry[24..26], date);
          state.Modified = modified;
        }

        WriteAt(shortOffset, entry);
        FlushBacking();
      });
    }
  }

  public void Flush() {
    lock (_gate) {
      ThrowIfDisposed();
      FlushBacking();
    }
  }

  public IFilesystemTransaction BeginTransaction()
    => throw new NotSupportedException("Ordinary FAT has ordered direct writes but no crash-atomic transaction primitive.");

  public void Dispose() {
    lock (_gate) {
      if (_disposed) return;
      foreach (var state in _nodes.Values.Where(static state => !state.Linked).ToArray()) {
        state.OpenHandles = 0;
        FinalizeUnlinkedIfPossible(state);
      }
      FlushBacking();
      if (_volumeDirty && !_hardError) {
        SetCleanFlag(clean: true, hardErrorFree: true);
        FlushBacking();
      }
      _disposed = true;
      if (!_leaveOpen) _image.Dispose();
    }
  }

  private NodeState AddState(
      NodeState parent,
      string name,
      bool isDirectory,
      int firstCluster,
      uint size,
      byte attributes,
      IReadOnlyList<long> slots) {
    var id = new FilesystemNodeId(_nextNodeId++, 1);
    var state = new NodeState(id, parent.Id, name, isDirectory, firstCluster, size, attributes, slots.ToList()) {
      Modified = DateTimeOffset.Now,
    };
    _nodes.Add(id, state);
    _children[parent.Id].Add(name, id);
    return state;
  }

  private void IndexDirectory(NodeState directory, HashSet<int> ancestorClusters) {
    var slots = GetDirectorySlots(directory);
    var lfn = new SortedDictionary<int, string>();
    var lfnOffsets = new List<long>();
    Span<byte> entry = stackalloc byte[32];

    foreach (var slot in slots) {
      ReadAt(slot, entry);
      var first = entry[0];
      if (first == 0x00) break;
      if (first == 0xE5) {
        lfn.Clear();
        lfnOffsets.Clear();
        continue;
      }

      var attr = entry[11];
      if ((attr & 0x3F) == AttrLongName) {
        lfn[first & 0x3F] = DecodeLfnFragment(entry);
        lfnOffsets.Add(slot);
        continue;
      }
      if ((attr & AttrVolume) != 0) {
        lfn.Clear();
        lfnOffsets.Clear();
        continue;
      }

      var shortName = DecodeShortName(entry);
      var name = lfn.Count == 0
        ? shortName
        : string.Concat(lfn.OrderBy(static pair => pair.Key).Select(static pair => pair.Value)).TrimEnd('\0', '\xFFFF');
      var allSlots = lfnOffsets.Append(slot).ToList();
      lfn.Clear();
      lfnOffsets.Clear();
      if (name is "." or "..") continue;

      var firstCluster = BinaryPrimitives.ReadUInt16LittleEndian(entry[26..28]);
      if (_geometry.FatType == 32)
        firstCluster |= BinaryPrimitives.ReadUInt16LittleEndian(entry[20..22]) << 16;
      var isDirectory = (attr & AttrDirectory) != 0;
      var size = isDirectory ? 0U : BinaryPrimitives.ReadUInt32LittleEndian(entry[28..32]);
      var id = new FilesystemNodeId(_nextNodeId++, 1);
      var state = new NodeState(id, directory.Id, name, isDirectory, firstCluster, size, attr, allSlots) {
        Modified = DecodeModified(entry),
      };
      if (_children[directory.Id].ContainsKey(name))
        throw new InvalidDataException($"FAT directory contains duplicate decoded name '{name}'.");
      _nodes.Add(id, state);
      _children[directory.Id].Add(name, id);

      if (!isDirectory) continue;
      _children.Add(id, new Dictionary<string, FilesystemNodeId>(StringComparer.OrdinalIgnoreCase));
      if (firstCluster < 2) continue;
      if (!ancestorClusters.Add(firstCluster))
        throw new InvalidDataException($"FAT directory recursion loop begins at cluster {firstCluster}.");
      IndexDirectory(state, ancestorClusters);
      ancestorClusters.Remove(firstCluster);
    }
  }

  private IReadOnlyList<long> GetDirectorySlots(NodeState directory) {
    if (directory.Id == RootNodeId && _geometry.FatType != 32) {
      var rootStart = checked(((long)_geometry.ReservedSectors + (long)_geometry.FatCount * _geometry.FatSize) * _geometry.BytesPerSector);
      return Enumerable.Range(0, _rootEntryCount).Select(i => checked(rootStart + i * 32L)).ToArray();
    }

    if (directory.FirstCluster < 2)
      throw new InvalidDataException($"FAT directory '{directory.Name}' has no cluster chain.");
    var chain = GetChain(directory);
    var perCluster = _geometry.ClusterSize / 32;
    var result = new long[checked(chain.Count * perCluster)];
    for (var c = 0; c < chain.Count; ++c) {
      var start = _geometry.ClusterOffset(chain[c]);
      for (var slot = 0; slot < perCluster; ++slot)
        result[c * perCluster + slot] = checked(start + slot * 32L);
    }
    return result;
  }

  private IReadOnlyList<long> ReserveSlots(NodeState directory, string name, NodeState? excluded, out byte[] encoded) {
    var shortNames = CollectShortNames(directory, excluded);
    encoded = FatWriter.BuildDirentSlots(name, shortNames, DateTime.Now, enableLfn: true, attr: AttrArchive);
    var needed = encoded.Length / 32;

    while (true) {
      var slots = GetDirectorySlots(directory);
      var runStart = FindFreeRun(slots, needed, excluded?.SlotOffsets);
      if (runStart >= 0) return slots.Skip(runStart).Take(needed).ToArray();
      if (directory.Id == RootNodeId && _geometry.FatType != 32)
        throw new IOException($"FAT fixed root directory has no run of {needed} free slot(s).");
      GrowDirectory(directory);
    }
  }

  private int FindFreeRun(IReadOnlyList<long> slots, int needed, IReadOnlyList<long>? additionallyFree) {
    var reusable = additionallyFree?.ToHashSet();
    var consecutive = 0;
    Span<byte> first = stackalloc byte[1];
    for (var i = 0; i < slots.Count; ++i) {
      ReadAt(slots[i], first);
      var free = first[0] is 0x00 or 0xE5 || reusable?.Contains(slots[i]) == true;
      consecutive = free ? consecutive + 1 : 0;
      if (consecutive == needed) return i - needed + 1;
    }
    return -1;
  }

  private HashSet<string> CollectShortNames(NodeState directory, NodeState? excluded) {
    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    Span<byte> entry = stackalloc byte[32];
    foreach (var childId in _children[directory.Id].Values) {
      var child = RequireState(childId);
      if (child == excluded || child.SlotOffsets.Count == 0) continue;
      ReadAt(child.SlotOffsets[^1], entry);
      result.Add(DecodeShortName(entry));
    }
    return result;
  }

  private void GrowDirectory(NodeState directory) {
    var chain = GetChain(directory).ToList();
    if (chain.Count == 0) throw new InvalidDataException("Cannot grow a FAT directory without a first cluster.");
    var newCluster = AllocateClusters(1)[0];

    ZeroCluster(newCluster);
    FlushBacking();
    WriteFatEntryAll(newCluster, EndOfChain());
    AdjustFsInfo(-1, newCluster + 1);
    FlushBacking();
    WriteFatEntryAll(chain[^1], newCluster);
    FlushBacking();

    chain.Add(newCluster);
    directory.Chain = chain;
  }

  private void InitializeDirectoryCluster(int cluster, NodeState parent) {
    ZeroCluster(cluster);
    var clusterOffset = _geometry.ClusterOffset(cluster);
    Span<byte> dot = stackalloc byte[32];
    Span<byte> dotDot = stackalloc byte[32];
    BuildDotEntry(dot, ".", cluster);
    var parentCluster = parent.Id == RootNodeId && _geometry.FatType != 32 ? 0 : parent.FirstCluster;
    BuildDotEntry(dotDot, "..", parentCluster);
    WriteAt(clusterOffset, dot);
    WriteAt(clusterOffset + 32, dotDot);
  }

  private void RewriteDotDot(NodeState directory, NodeState newParent) {
    if (directory.FirstCluster < 2) return;
    var offset = _geometry.ClusterOffset(directory.FirstCluster) + 32;
    Span<byte> entry = stackalloc byte[32];
    ReadAt(offset, entry);
    var parentCluster = newParent.Id == RootNodeId && _geometry.FatType != 32 ? 0 : newParent.FirstCluster;
    BinaryPrimitives.WriteUInt16LittleEndian(entry[20..22], (ushort)(parentCluster >> 16));
    BinaryPrimitives.WriteUInt16LittleEndian(entry[26..28], (ushort)parentCluster);
    WriteAt(offset, entry);
    FlushBacking();
  }

  private void BuildDotEntry(Span<byte> entry, string name, int firstCluster) {
    entry.Clear();
    entry[..11].Fill((byte)' ');
    Encoding.ASCII.GetBytes(name, entry);
    entry[11] = AttrDirectory;
    EncodeDateTime(DateTimeOffset.Now, out var date, out var time, out var tenths);
    entry[13] = tenths;
    BinaryPrimitives.WriteUInt16LittleEndian(entry[14..16], time);
    BinaryPrimitives.WriteUInt16LittleEndian(entry[16..18], date);
    BinaryPrimitives.WriteUInt16LittleEndian(entry[18..20], date);
    BinaryPrimitives.WriteUInt16LittleEndian(entry[20..22], (ushort)(firstCluster >> 16));
    BinaryPrimitives.WriteUInt16LittleEndian(entry[22..24], time);
    BinaryPrimitives.WriteUInt16LittleEndian(entry[24..26], date);
    BinaryPrimitives.WriteUInt16LittleEndian(entry[26..28], (ushort)firstCluster);
  }

  private bool IsDescendantOf(NodeState candidate, NodeState ancestor) {
    for (var current = candidate; current.Id != RootNodeId; current = RequireState(current.ParentId))
      if (current.Id == ancestor.Id) return true;
    return false;
  }

  private void Unlink(NodeState state) {
    if (!state.Linked) throw new FileNotFoundException(state.Name);
    MarkSlotsDeleted(state.SlotOffsets);
    FlushBacking();
    _children[state.ParentId].Remove(state.Name);
    state.Linked = false;
    FinalizeUnlinkedIfPossible(state);
  }

  private void FinalizeUnlinkedIfPossible(NodeState state) {
    if (state.Linked || state.OpenHandles != 0) return;
    var chain = GetChain(state).ToArray();
    if (chain.Length == 0) return;
    FreeClusters(chain);
    state.FirstCluster = 0;
    state.Chain = [];
  }

  private void HandleClosed(NodeState state) {
    lock (_gate) {
      if (state.OpenHandles > 0) --state.OpenHandles;
      if (!_disposed) FinalizeUnlinkedIfPossible(state);
    }
  }

  private int ReadFile(NodeState state, long offset, Span<byte> destination) {
    lock (_gate) {
      ThrowIfDisposed();
      if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
      if (destination.Length == 0 || offset >= state.Size) return 0;
      var remaining = checked((int)Math.Min(destination.Length, state.Size - offset));
      var chain = GetChain(state);
      var copied = 0;
      var logical = offset;
      while (remaining > 0) {
        var chainIndex = checked((int)(logical / _geometry.ClusterSize));
        if ((uint)chainIndex >= (uint)chain.Count)
          throw new InvalidDataException($"FAT chain for '{state.Name}' ends before logical offset {logical}.");
        var within = checked((int)(logical % _geometry.ClusterSize));
        var take = Math.Min(remaining, _geometry.ClusterSize - within);
        ReadAt(_geometry.ClusterOffset(chain[chainIndex]) + within, destination.Slice(copied, take));
        logical += take;
        copied += take;
        remaining -= take;
      }
      return copied;
    }
  }

  private void WriteFile(NodeState state, long offset, ReadOnlySpan<byte> source) {
    lock (_gate) {
      ThrowIfDisposed();
      if ((state.Attributes & AttrReadOnly) != 0) throw new UnauthorizedAccessException($"FAT entry '{state.Name}' is read-only.");
      if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
      if (source.Length == 0) return;
      var end = checked(offset + source.Length);
      if (end > uint.MaxValue) throw new IOException("FAT file size is limited to 4 GiB - 1 byte.");

      Mutate(() => {
        var oldSize = (long)state.Size;
        EnsureCapacity(state, end);
        if (offset > oldSize) ZeroLogicalRange(state, oldSize, offset - oldSize);
        WriteLogicalRange(state, offset, source);
        if (end <= oldSize) return;
        FlushBacking();
        state.Size = (uint)end;
        PublishShortEntry(state);
        FlushBacking();
      });
    }
  }

  private void SetFileLength(NodeState state, long length) {
    lock (_gate) {
      ThrowIfDisposed();
      if ((state.Attributes & AttrReadOnly) != 0) throw new UnauthorizedAccessException($"FAT entry '{state.Name}' is read-only.");
      if (length < 0 || length > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(length));
      if (length == state.Size) return;

      Mutate(() => {
        var oldSize = (long)state.Size;
        if (length > oldSize) {
          EnsureCapacity(state, length);
          ZeroLogicalRange(state, oldSize, length - oldSize);
          FlushBacking();
          state.Size = (uint)length;
          PublishShortEntry(state);
          FlushBacking();
          return;
        }

        // Capture ownership before changing FirstCluster. Publish the smaller
        // logical size before releasing storage, so a crash cannot expose freed data.
        var chain = GetChain(state).ToList();
        var keep = length == 0 ? 0 : checked((int)((length + _geometry.ClusterSize - 1) / _geometry.ClusterSize));
        state.Size = (uint)length;
        if (length == 0) state.FirstCluster = 0;
        PublishShortEntry(state);
        FlushBacking();

        if (keep < chain.Count) {
          if (keep > 0) {
            WriteFatEntryAll(chain[keep - 1], EndOfChain());
            FlushBacking();
          }
          var released = chain.Skip(keep).ToArray();
          FreeClusters(released);
          chain.RemoveRange(keep, chain.Count - keep);
        }
        state.Chain = chain;
        FlushBacking();
      });
    }
  }

  private void EnsureCapacity(NodeState state, long length) {
    var required = length == 0 ? 0 : checked((int)((length + _geometry.ClusterSize - 1) / _geometry.ClusterSize));
    var chain = GetChain(state).ToList();
    if (chain.Count >= required) return;

    var added = AllocateClusters(required - chain.Count);
    foreach (var cluster in added) ZeroCluster(cluster);
    FlushBacking();

    for (var i = 0; i < added.Count - 1; ++i) WriteFatEntryAll(added[i], added[i + 1]);
    WriteFatEntryAll(added[^1], EndOfChain());
    AdjustFsInfo(-added.Count, added[^1] + 1);
    FlushBacking();

    if (chain.Count != 0) {
      WriteFatEntryAll(chain[^1], added[0]);
      FlushBacking();
    } else {
      state.FirstCluster = added[0];
    }
    chain.AddRange(added);
    state.Chain = chain;
  }

  private List<int> AllocateClusters(int count) {
    if (count <= 0) return [];
    var result = new List<int>(count);
    for (var cluster = 2; cluster < _geometry.TotalDataClusters + 2 && result.Count < count; ++cluster) {
      if (ReadFatEntryVerified(cluster) != 0) continue;
      result.Add(cluster);
    }
    if (result.Count != count)
      throw new IOException($"FAT volume has only {result.Count} free cluster(s); {count} required.");
    return result;
  }

  private void FreeClusters(IReadOnlyList<int> clusters) {
    foreach (var cluster in clusters) WriteFatEntryAll(cluster, 0);
    AdjustFsInfo(clusters.Count, clusters.Count == 0 ? 2 : clusters.Min());
    FlushBacking();
  }

  private IReadOnlyList<int> GetChain(NodeState state) {
    if (state.Chain != null) return state.Chain;
    state.Chain = state.FirstCluster < 2
      ? []
      : _geometry.ReadChain(_image, state.FirstCluster, state.Name).ToList();
    return state.Chain;
  }

  private void WriteLogicalRange(NodeState state, long offset, ReadOnlySpan<byte> source) {
    var chain = GetChain(state);
    var consumed = 0;
    while (consumed < source.Length) {
      var logical = offset + consumed;
      var chainIndex = checked((int)(logical / _geometry.ClusterSize));
      var within = checked((int)(logical % _geometry.ClusterSize));
      var take = Math.Min(source.Length - consumed, _geometry.ClusterSize - within);
      WriteAt(_geometry.ClusterOffset(chain[chainIndex]) + within, source.Slice(consumed, take));
      consumed += take;
    }
  }

  private void ZeroLogicalRange(NodeState state, long offset, long count) {
    if (count <= 0) return;
    var zero = new byte[Math.Min(_geometry.ClusterSize, 64 * 1024)];
    var remaining = count;
    var logical = offset;
    while (remaining > 0) {
      var take = checked((int)Math.Min(remaining, zero.Length));
      WriteLogicalRange(state, logical, zero.AsSpan(0, take));
      logical += take;
      remaining -= take;
    }
  }

  private void PublishShortEntry(NodeState state) {
    if (!state.Linked || state.SlotOffsets.Count == 0) return;
    var shortOffset = state.SlotOffsets[^1];
    Span<byte> entry = stackalloc byte[32];
    ReadAt(shortOffset, entry);
    BinaryPrimitives.WriteUInt16LittleEndian(entry[20..22], (ushort)(state.FirstCluster >> 16));
    BinaryPrimitives.WriteUInt16LittleEndian(entry[26..28], (ushort)state.FirstCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[28..32], state.IsDirectory ? 0U : state.Size);
    EncodeDateTime(DateTimeOffset.Now, out var date, out var time, out _);
    BinaryPrimitives.WriteUInt16LittleEndian(entry[22..24], time);
    BinaryPrimitives.WriteUInt16LittleEndian(entry[24..26], date);
    WriteAt(shortOffset, entry);
    state.Modified = DateTimeOffset.Now;
  }

  private static void PatchShortEntry(byte[] slots, int firstCluster, uint size, byte attributes) {
    var shortOffset = slots.Length - 32;
    slots[shortOffset + 11] = attributes;
    BinaryPrimitives.WriteUInt16LittleEndian(slots.AsSpan(shortOffset + 20, 2), (ushort)(firstCluster >> 16));
    BinaryPrimitives.WriteUInt16LittleEndian(slots.AsSpan(shortOffset + 26, 2), (ushort)firstCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(slots.AsSpan(shortOffset + 28, 4), size);
  }

  private void WriteSlotBlob(IReadOnlyList<long> slots, ReadOnlySpan<byte> encoded) {
    if (slots.Count * 32 != encoded.Length)
      throw new InvalidOperationException("FAT directory slot reservation does not match encoded entry size.");
    for (var i = 0; i < slots.Count; ++i) WriteAt(slots[i], encoded.Slice(i * 32, 32));
  }

  private void MarkSlotsDeleted(IEnumerable<long> offsets) {
    Span<byte> entry = stackalloc byte[32];
    foreach (var offset in offsets) {
      entry.Clear();
      entry[0] = 0xE5;
      WriteAt(offset, entry);
    }
  }

  private void ZeroCluster(int cluster) {
    var remaining = _geometry.ClusterSize;
    var offset = _geometry.ClusterOffset(cluster);
    Span<byte> zero = stackalloc byte[4096];
    while (remaining > 0) {
      var take = Math.Min(remaining, zero.Length);
      WriteAt(offset, zero[..take]);
      offset += take;
      remaining -= take;
    }
  }

  private int ReadFatEntryVerified(int cluster) {
    var value = ReadFatEntry(0, cluster);
    for (var copy = 1; copy < _geometry.FatCount; ++copy) {
      var mirror = ReadFatEntry(copy, cluster);
      if (mirror != value)
        throw new InvalidDataException($"FAT copies disagree at cluster {cluster}: primary=0x{value:X}, copy {copy}=0x{mirror:X}.");
    }
    return value;
  }

  private int ReadFatEntry(int copy, int cluster) {
    var fatStart = checked(((long)_geometry.ReservedSectors + (long)copy * _geometry.FatSize) * _geometry.BytesPerSector);
    Span<byte> bytes = stackalloc byte[4];
    var offset = _geometry.FatType switch {
      12 => fatStart + cluster + cluster / 2,
      16 => fatStart + cluster * 2L,
      _ => fatStart + cluster * 4L,
    };
    var count = _geometry.FatType == 32 ? 4 : 2;
    ReadAt(offset, bytes[..count]);
    if (_geometry.FatType == 12) {
      var raw = BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]);
      return (cluster & 1) == 0 ? raw & 0x0FFF : raw >> 4;
    }
    if (_geometry.FatType == 16) return BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]);
    return (int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes) & 0x0FFFFFFF);
  }

  private void WriteFatEntryAll(int cluster, int value) {
    for (var copy = 0; copy < _geometry.FatCount; ++copy) WriteFatEntry(copy, cluster, value);
  }

  private void WriteFatEntry(int copy, int cluster, int value) {
    var fatStart = checked(((long)_geometry.ReservedSectors + (long)copy * _geometry.FatSize) * _geometry.BytesPerSector);
    var offset = _geometry.FatType switch {
      12 => fatStart + cluster + cluster / 2,
      16 => fatStart + cluster * 2L,
      _ => fatStart + cluster * 4L,
    };

    if (_geometry.FatType == 12) {
      Span<byte> bytes = stackalloc byte[2];
      ReadAt(offset, bytes);
      var raw = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
      raw = (ushort)((cluster & 1) == 0
        ? (raw & 0xF000) | (value & 0x0FFF)
        : (raw & 0x000F) | ((value & 0x0FFF) << 4));
      BinaryPrimitives.WriteUInt16LittleEndian(bytes, raw);
      WriteAt(offset, bytes);
      return;
    }
    if (_geometry.FatType == 16) {
      Span<byte> bytes = stackalloc byte[2];
      BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
      WriteAt(offset, bytes);
      return;
    }

    Span<byte> fat32 = stackalloc byte[4];
    ReadAt(offset, fat32);
    var reserved = BinaryPrimitives.ReadUInt32LittleEndian(fat32) & 0xF0000000U;
    BinaryPrimitives.WriteUInt32LittleEndian(fat32, reserved | ((uint)value & 0x0FFFFFFFU));
    WriteAt(offset, fat32);
  }

  private int EndOfChain() => _geometry.FatType switch {
    12 => 0x0FFF,
    16 => 0xFFFF,
    _ => 0x0FFFFFFF,
  };

  private void AdjustFsInfo(int freeDelta, int nextHint) {
    if (_geometry.FatType != 32 || _fsInfoSector <= 0 || _fsInfoSector >= _geometry.ReservedSectors) return;
    var offset = checked((long)_fsInfoSector * _geometry.BytesPerSector);
    Span<byte> fsInfo = stackalloc byte[512];
    ReadAt(offset, fsInfo);
    if (BinaryPrimitives.ReadUInt32LittleEndian(fsInfo[..4]) != 0x41615252
        || BinaryPrimitives.ReadUInt32LittleEndian(fsInfo[484..488]) != 0x61417272
        || BinaryPrimitives.ReadUInt32LittleEndian(fsInfo[508..512]) != 0xAA550000)
      return;

    var free = BinaryPrimitives.ReadUInt32LittleEndian(fsInfo[488..492]);
    if (free != uint.MaxValue) {
      var updated = Math.Clamp((long)free + freeDelta, 0L, _geometry.TotalDataClusters);
      BinaryPrimitives.WriteUInt32LittleEndian(fsInfo[488..492], (uint)updated);
    }
    var hint = nextHint >= 2 && nextHint < _geometry.TotalDataClusters + 2 ? (uint)nextHint : 2U;
    BinaryPrimitives.WriteUInt32LittleEndian(fsInfo[492..496], hint);
    WriteAt(offset, fsInfo);
  }

  private void MarkVolumeDirty() {
    if (_volumeDirty) return;
    if (_geometry.FatType is 16 or 32) SetCleanFlag(clean: false, hardErrorFree: true);
    FlushBacking();
    _volumeDirty = true;
  }

  private void MarkHardError() {
    _hardError = true;
    try {
      if (_geometry.FatType is 16 or 32) {
        SetCleanFlag(clean: false, hardErrorFree: false);
        FlushBacking();
      }
    } catch {
      // Preserve the original I/O exception; the medium is already known bad.
    }
  }

  private void SetCleanFlag(bool clean, bool hardErrorFree) {
    var value = ReadFatEntryVerified(1);
    if (_geometry.FatType == 16) {
      value = clean ? value | 0x8000 : value & ~0x8000;
      value = hardErrorFree ? value | 0x4000 : value & ~0x4000;
    } else if (_geometry.FatType == 32) {
      value = clean ? value | 0x08000000 : value & ~0x08000000;
      value = hardErrorFree ? value | 0x04000000 : value & ~0x04000000;
    } else {
      return;
    }
    WriteFatEntryAll(1, value);
  }

  private T Mutate<T>(Func<T> action) {
    MarkVolumeDirty();
    try {
      return action();
    } catch (IOException) {
      MarkHardError();
      throw;
    }
  }

  private void Mutate(Action action) => Mutate(() => {
    action();
    return true;
  });

  private void ReadAt(long offset, Span<byte> destination) {
    if (offset < 0 || offset > _geometry.DataLength - destination.Length)
      throw new InvalidDataException($"FAT access [{offset}, {offset + destination.Length}) lies outside the declared volume.");
    _image.Position = offset;
    _image.ReadExactly(destination);
  }

  private void WriteAt(long offset, ReadOnlySpan<byte> source) {
    if (offset < 0 || offset > _geometry.DataLength - source.Length)
      throw new InvalidDataException($"FAT write [{offset}, {offset + source.Length}) lies outside the declared volume.");
    _image.Position = offset;
    _image.Write(source);
  }

  private void FlushBacking() => _image.Flush();

  private NodeState RequireState(FilesystemNodeId id) {
    if (!_nodes.TryGetValue(id, out var state))
      throw new FileNotFoundException($"FAT node {id.Value}:{id.Generation} is unknown.");
    return state;
  }

  private NodeState RequireDirectory(FilesystemNodeId id) {
    var state = RequireState(id);
    if (!state.Linked && id != RootNodeId) throw new DirectoryNotFoundException(state.Name);
    if (!state.IsDirectory) throw new DirectoryNotFoundException($"FAT node '{state.Name}' is not a directory.");
    return state;
  }

  private NodeState RequireChild(NodeState parent, string name) {
    if (!_children[parent.Id].TryGetValue(name, out var id)) throw new FileNotFoundException(name);
    return RequireState(id);
  }

  private void ValidateNewName(NodeState parent, string name) {
    ValidateName(name);
    if (_children[parent.Id].ContainsKey(name)) throw new IOException($"FAT entry '{name}' already exists.");
  }

  private static void ValidateName(string name) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    if (name is "." or "..") throw new ArgumentException("FAT dot entries are reserved.", nameof(name));
    if (name.IndexOfAny(['/', '\\', '\0']) >= 0)
      throw new ArgumentException("FAT entry names cannot contain path separators or NUL.", nameof(name));
    if (name.Length > 255) throw new ArgumentException("VFAT long names are limited to 255 UTF-16 code units.", nameof(name));
  }

  private static string DecodeLfnFragment(ReadOnlySpan<byte> entry) {
    Span<char> chars = stackalloc char[13];
    var written = 0;
    DecodeLfnRange(entry[1..11], chars, ref written);
    DecodeLfnRange(entry[14..26], chars, ref written);
    DecodeLfnRange(entry[28..32], chars, ref written);
    return new string(chars[..written]);
  }

  private static void DecodeLfnRange(ReadOnlySpan<byte> bytes, Span<char> chars, ref int written) {
    for (var i = 0; i < bytes.Length; i += 2) {
      var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(i, 2));
      if (value is 0 or 0xFFFF) break;
      chars[written++] = (char)value;
    }
  }

  private static string DecodeShortName(ReadOnlySpan<byte> entry) {
    var baseName = Encoding.ASCII.GetString(entry[..8]).TrimEnd(' ');
    var extension = Encoding.ASCII.GetString(entry[8..11]).TrimEnd(' ');
    if ((entry[12] & 0x08) != 0) baseName = baseName.ToLowerInvariant();
    if ((entry[12] & 0x10) != 0) extension = extension.ToLowerInvariant();
    return extension.Length == 0 ? baseName : $"{baseName}.{extension}";
  }

  private static DateTimeOffset? DecodeModified(ReadOnlySpan<byte> entry) {
    var date = BinaryPrimitives.ReadUInt16LittleEndian(entry[24..26]);
    var time = BinaryPrimitives.ReadUInt16LittleEndian(entry[22..24]);
    if (date == 0) return null;
    try {
      var value = new DateTime(
        1980 + (date >> 9),
        (date >> 5) & 0x0F,
        date & 0x1F,
        time >> 11,
        (time >> 5) & 0x3F,
        (time & 0x1F) * 2,
        DateTimeKind.Local);
      return new DateTimeOffset(value);
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }

  private static void EncodeDateTime(DateTimeOffset value, out ushort date, out ushort time, out byte tenths) {
    var local = value.LocalDateTime;
    var year = Math.Clamp(local.Year, 1980, 2107);
    date = (ushort)(((year - 1980) << 9) | (local.Month << 5) | local.Day);
    time = (ushort)((local.Hour << 11) | (local.Minute << 5) | (local.Second / 2));
    tenths = (byte)Math.Clamp((local.Second & 1) * 100 + local.Millisecond / 10, 0, 199);
  }

  private static void EncodeDate(DateTimeOffset value, out ushort date) {
    var local = value.LocalDateTime;
    var year = Math.Clamp(local.Year, 1980, 2107);
    date = (ushort)(((year - 1980) << 9) | (local.Month << 5) | local.Day);
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

  private sealed class NodeState(
      FilesystemNodeId id,
      FilesystemNodeId parentId,
      string name,
      bool isDirectory,
      int firstCluster,
      uint size,
      byte attributes,
      List<long> slotOffsets) {
    public FilesystemNodeId Id { get; } = id;
    public FilesystemNodeId ParentId { get; set; } = parentId;
    public string Name { get; set; } = name;
    public bool IsDirectory { get; } = isDirectory;
    public int FirstCluster { get; set; } = firstCluster;
    public uint Size { get; set; } = size;
    public byte Attributes { get; set; } = attributes;
    public List<long> SlotOffsets { get; set; } = slotOffsets;
    public List<int>? Chain { get; set; }
    public bool Linked { get; set; } = true;
    public int OpenHandles { get; set; }
    public DateTimeOffset? Modified { get; set; }
  }

  private sealed class FileHandle : IFilesystemFileHandle {
    private readonly FatWritableFilesystemSession _session;
    private readonly NodeState _state;
    private readonly FileAccess _access;
    private bool _disposed;

    public FileHandle(FatWritableFilesystemSession session, NodeState state, FileAccess access) {
      _session = session;
      _state = state;
      _access = access;
    }

    public FilesystemNodeId NodeId => _state.Id;

    public long Length {
      get {
        ThrowIfDisposed();
        lock (_session._gate) return _state.Size;
      }
    }

    public int Read(long offset, Span<byte> destination) {
      ThrowIfDisposed();
      if (_access == FileAccess.Write) throw new NotSupportedException("This FAT handle was opened write-only.");
      return _session.ReadFile(_state, offset, destination);
    }

    public void Write(long offset, ReadOnlySpan<byte> source) {
      ThrowIfDisposed();
      if (_access == FileAccess.Read) throw new NotSupportedException("This FAT handle was opened read-only.");
      _session.WriteFile(_state, offset, source);
    }

    public void SetLength(long length) {
      ThrowIfDisposed();
      if (_access == FileAccess.Read) throw new NotSupportedException("This FAT handle was opened read-only.");
      _session.SetFileLength(_state, length);
    }

    public void Flush() {
      ThrowIfDisposed();
      _session.Flush();
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;
      _session.HandleClosed(_state);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
  }
}
