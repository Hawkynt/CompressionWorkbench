#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.D64;

/// <summary>
/// Root-only CBM DOS 2.6 namespace session over a 1541 sector device. File
/// handles are path-independent: rename keeps the node id stable and unlink
/// detaches a namespace entry without invalidating already-open handles.
/// </summary>
public sealed class D64FilesystemSession : IFilesystemSession {
  private const int SectorSize = 256;
  private const int DirectoryTrack = 18;
  private const int DirectoryStartSector = 1;
  private const int TotalTracks = 35;

  private static readonly int[] SectorsPerTrack = [
    0,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17,
  ];

  private readonly IRandomAccessBlockDevice _device;
  private readonly bool _ownsDevice;
  private readonly bool _readOnly;
  private readonly object _gate = new();
  private readonly Dictionary<FilesystemNodeId, NodeState> _states = [];
  private readonly Dictionary<string, FilesystemNodeId> _idsByName = new(StringComparer.OrdinalIgnoreCase);
  private byte[] _image;
  private ulong _nextNodeId = 2;
  private bool _disposed;

  public D64FilesystemSession(
      IRandomAccessBlockDevice device,
      FilesystemDriverProfile profile,
      bool readOnly,
      bool ownsDevice = true) {
    ArgumentNullException.ThrowIfNull(device);
    ArgumentNullException.ThrowIfNull(profile);
    if (device.Geometry.LogicalBlockSize != SectorSize || device.Geometry.BlockCount < D64BlockDevice.SectorCount)
      throw new InvalidDataException("CBM DOS requires a 683-sector, 256-byte 1541 block device.");
    if (!readOnly && !device.CanWrite)
      throw new ArgumentException("Writable CBM DOS session requires a writable block device.", nameof(device));
    if (!readOnly && !profile.CanMountWritable)
      throw new NotSupportedException("This exact CBM DOS media profile is not safe for writable mounting.");

    _device = device;
    _ownsDevice = ownsDevice;
    _readOnly = readOnly;
    Profile = profile;
    _image = ReadWholeImage(device);
    IndexInitialNamespace();
  }

  public FilesystemDriverProfile Profile { get; }
  public FilesystemNodeId RootNodeId { get; } = new(1, 1);

  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) {
    lock (_gate) {
      ThrowIfDisposed();
      if (nodeId == RootNodeId)
        return new FilesystemNodeInfo(RootNodeId, FilesystemNodeKind.Directory, 0, 0, 1);
      if (!_states.TryGetValue(nodeId, out var state))
        throw new FileNotFoundException($"CBM DOS node {nodeId.Value}:{nodeId.Generation} is unknown.");
      if (!state.Linked)
        return new FilesystemNodeInfo(nodeId, FilesystemNodeKind.RegularFile,
          state.Data?.LongLength ?? 0, 0, 0);
      var record = FindRecord(state.Name);
      return new FilesystemNodeInfo(nodeId, FilesystemNodeKind.RegularFile,
        state.Data?.LongLength ?? record.Size, record.SectorCount * (long)SectorSize,
        1, record.FileType);
    }
  }

  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) {
    ArgumentNullException.ThrowIfNull(name);
    lock (_gate) {
      ThrowIfDisposed();
      RequireRoot(parentDirectory);
      return _idsByName.TryGetValue(name, out var nodeId) ? nodeId : null;
    }
  }

  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) {
    lock (_gate) {
      ThrowIfDisposed();
      RequireRoot(directory);
      return ScanDirectory(_image)
        .Select(record => new FilesystemDirectoryEntry(
          record.Name, _idsByName[record.Name], FilesystemNodeKind.RegularFile))
        .ToArray();
    }
  }

  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) {
    lock (_gate) {
      ThrowIfDisposed();
      if (nodeId == RootNodeId) throw new UnauthorizedAccessException("The CBM DOS root directory is not a regular file.");
      if (access != FileAccess.Read && _readOnly)
        throw new NotSupportedException("The CBM DOS session was opened read-only.");
      var state = RequireLinkedState(nodeId);
      state.Data ??= ReadFileData(_image, FindRecord(state.Name));
      return new FileHandle(this, state, access);
    }
  }

  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      ThrowIfDisposed();
      EnsureWritable();
      RequireRoot(parentDirectory);
      ValidateName(name);
      if (_idsByName.ContainsKey(name)) throw new IOException($"CBM DOS entry '{name}' already exists.");

      ApplyStreamMutation(stream => D64Modifier.AddFile(stream, name, []));
      var nodeId = new FilesystemNodeId(_nextNodeId++, 1);
      var canonicalName = FindRecord(name).Name;
      var state = new NodeState(nodeId, canonicalName) { Data = [] };
      _states[nodeId] = state;
      _idsByName[canonicalName] = nodeId;
      return nodeId;
    }
  }

  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name)
    => throw new NotSupportedException("1541 CBM DOS 2.6 has a flat root directory and cannot create subdirectories.");

  public void DeleteFile(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      ThrowIfDisposed();
      EnsureWritable();
      RequireRoot(parentDirectory);
      if (!_idsByName.TryGetValue(name, out var nodeId)) throw new FileNotFoundException(name);
      var state = _states[nodeId];
      ApplyStreamMutation(stream => {
        if (!D64Modifier.RemoveFile(stream, state.Name, wipeData: true))
          throw new FileNotFoundException(state.Name);
      });
      _idsByName.Remove(state.Name);
      state.Linked = false;
      state.Dirty = false;
    }
  }

  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name)
    => throw new NotSupportedException("1541 CBM DOS 2.6 has no subdirectories.");

  public void Rename(
      FilesystemNodeId oldParent,
      string oldName,
      FilesystemNodeId newParent,
      string newName,
      bool replace) {
    lock (_gate) {
      ThrowIfDisposed();
      EnsureWritable();
      RequireRoot(oldParent);
      RequireRoot(newParent);
      ValidateName(newName);
      if (!_idsByName.TryGetValue(oldName, out var sourceId)) throw new FileNotFoundException(oldName);

      if (_idsByName.TryGetValue(newName, out var targetId) && targetId != sourceId) {
        if (!replace) throw new IOException($"CBM DOS entry '{newName}' already exists.");
        var target = _states[targetId];
        ApplyStreamMutation(stream => {
          if (!D64Modifier.RemoveFile(stream, target.Name, wipeData: true))
            throw new IOException($"Unable to replace existing CBM DOS entry '{newName}'.");
        });
        _idsByName.Remove(target.Name);
        target.Linked = false;
        target.Dirty = false;
      }

      var source = _states[sourceId];
      var record = FindRecord(source.Name);
      var renamed = _image.ToArray();
      WriteDirectoryName(renamed, record.DirectoryEntryOffset, newName);
      CommitChangedBlocks(renamed);
      _idsByName.Remove(source.Name);
      source.Name = NormalizeName(newName);
      _idsByName[source.Name] = sourceId;
    }
  }

  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName)
    => throw new NotSupportedException("1541 CBM DOS has no hard links.");

  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target)
    => throw new NotSupportedException("1541 CBM DOS has no symbolic links.");

  public string ReadSymbolicLink(FilesystemNodeId nodeId)
    => throw new NotSupportedException("1541 CBM DOS has no symbolic links.");

  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch)
    => throw new NotSupportedException("The generic metadata patch has no lossless CBM DOS 2.6 mapping.");

  public void Flush() {
    NodeState[] dirty;
    lock (_gate) {
      ThrowIfDisposed();
      dirty = _states.Values.Where(state => state.Linked && state.Dirty).ToArray();
    }
    foreach (var state in dirty) FlushState(state);
    _device.Flush();
  }

  public IFilesystemTransaction BeginTransaction()
    => throw new NotSupportedException("CBM DOS 2.6 has no crash-atomic journal/transaction primitive.");

  public void Dispose() {
    if (_disposed) return;
    if (!_readOnly) Flush();
    _disposed = true;
    if (_ownsDevice) _device.Dispose();
  }

  private static byte[] ReadWholeImage(IRandomAccessBlockDevice device) {
    var data = new byte[D64BlockDevice.DataLength];
    var blocks = device.ReadBlocks(0, data);
    if (blocks != D64BlockDevice.SectorCount)
      throw new EndOfStreamException($"CBM DOS block device returned {blocks} sectors; expected {D64BlockDevice.SectorCount}.");
    return data;
  }

  private void IndexInitialNamespace() {
    foreach (var record in ScanDirectory(_image)) {
      if (_idsByName.ContainsKey(record.Name))
        throw new InvalidDataException($"CBM DOS directory contains duplicate name '{record.Name}'; mount would be ambiguous.");
      var nodeId = new FilesystemNodeId(_nextNodeId++, 1);
      _idsByName[record.Name] = nodeId;
      _states[nodeId] = new NodeState(nodeId, record.Name);
    }
  }

  private void FlushState(NodeState state) {
    lock (_gate) {
      ThrowIfDisposed();
      if (!state.Linked || !state.Dirty || state.Data == null) return;
      var oldName = state.Name;
      ApplyStreamMutation(stream => {
        if (!D64Modifier.RemoveFile(stream, oldName, wipeData: false))
          throw new IOException($"CBM DOS entry '{oldName}' disappeared while flushing an open handle.");
        D64Modifier.AddFile(stream, oldName, state.Data);
      });
      state.Name = FindRecord(oldName).Name;
      state.Dirty = false;
      _idsByName.Remove(oldName);
      _idsByName[state.Name] = state.NodeId;
    }
  }

  private void ApplyStreamMutation(Action<Stream> mutate) {
    var candidate = _image.ToArray();
    using var stream = new MemoryStream(candidate, writable: true);
    mutate(stream);
    CommitChangedBlocks(candidate);
  }

  private void CommitChangedBlocks(byte[] candidate) {
    if (candidate.Length != D64BlockDevice.DataLength)
      throw new InvalidDataException("CBM DOS mutation changed the fixed D64 data geometry.");
    for (var block = 0; block < D64BlockDevice.SectorCount; ++block) {
      var offset = block * SectorSize;
      var next = candidate.AsSpan(offset, SectorSize);
      if (_image.AsSpan(offset, SectorSize).SequenceEqual(next)) continue;
      _device.WriteBlocks(block, next);
    }
    _image = candidate;
  }

  private NodeState RequireLinkedState(FilesystemNodeId nodeId) {
    if (!_states.TryGetValue(nodeId, out var state) || !state.Linked)
      throw new FileNotFoundException($"CBM DOS node {nodeId.Value}:{nodeId.Generation} is not linked in this session.");
    return state;
  }

  private DirectoryRecord FindRecord(string name)
    => ScanDirectory(_image).FirstOrDefault(record => string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
       ?? throw new FileNotFoundException(name);

  private static IReadOnlyList<DirectoryRecord> ScanDirectory(byte[] image) {
    var result = new List<DirectoryRecord>();
    var track = DirectoryTrack;
    var sector = DirectoryStartSector;
    var visited = new HashSet<(int, int)>();
    while (track != 0) {
      if (!visited.Add((track, sector))) throw new InvalidDataException("CBM DOS directory chain contains a loop.");
      var offset = SectorOffset(track, sector);
      if (offset < 0 || offset + SectorSize > image.Length)
        throw new InvalidDataException("CBM DOS directory points outside the 1541 geometry.");
      var nextTrack = image[offset];
      var nextSector = image[offset + 1];
      for (var slot = 0; slot < 8; ++slot) {
        var entryOffset = offset + slot * 32;
        var fileType = image[entryOffset + 2];
        if ((fileType & 0x07) == 0) continue;
        var startTrack = image[entryOffset + 3];
        var startSector = image[entryOffset + 4];
        var name = DecodeName(image.AsSpan(entryOffset + 5, 16));
        var chain = WalkChain(image, startTrack, startSector);
        result.Add(new DirectoryRecord(
          name,
          fileType,
          startTrack,
          startSector,
          BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(entryOffset + 30, 2)),
          CalculateSize(image, chain),
          entryOffset));
      }
      if (nextTrack == 0) break;
      track = nextTrack;
      sector = nextSector;
    }
    return result;
  }

  private static List<(int Track, int Sector)> WalkChain(byte[] image, int track, int sector) {
    var chain = new List<(int, int)>();
    var visited = new HashSet<(int, int)>();
    while (track != 0) {
      if (!visited.Add((track, sector))) throw new InvalidDataException("CBM DOS file sector chain contains a loop.");
      var offset = SectorOffset(track, sector);
      if (offset < 0 || offset + SectorSize > image.Length)
        throw new InvalidDataException("CBM DOS file sector chain points outside the 1541 geometry.");
      chain.Add((track, sector));
      var nextTrack = image[offset];
      var nextSector = image[offset + 1];
      track = nextTrack;
      sector = nextSector;
    }
    return chain;
  }

  private static long CalculateSize(byte[] image, IReadOnlyList<(int Track, int Sector)> chain) {
    if (chain.Count == 0) return 0;
    var last = chain[^1];
    var offset = SectorOffset(last.Track, last.Sector);
    var used = image[offset + 1] > 1 ? image[offset + 1] - 1 : 254;
    return (chain.Count - 1L) * 254 + used;
  }

  private static byte[] ReadFileData(byte[] image, DirectoryRecord record) {
    var chain = WalkChain(image, record.StartTrack, record.StartSector);
    if (chain.Count == 0) return [];
    using var output = new MemoryStream((int)Math.Min(record.Size, int.MaxValue));
    for (var i = 0; i < chain.Count; ++i) {
      var item = chain[i];
      var offset = SectorOffset(item.Track, item.Sector);
      var count = i == chain.Count - 1
        ? (image[offset + 1] > 1 ? image[offset + 1] - 1 : 254)
        : 254;
      output.Write(image, offset + 2, count);
    }
    return output.ToArray();
  }

  private static int SectorOffset(int track, int sector) {
    if (track < 1 || track > TotalTracks || sector < 0 || sector >= SectorsPerTrack[track]) return -1;
    var offset = 0;
    for (var t = 1; t < track; ++t) offset += SectorsPerTrack[t] * SectorSize;
    return offset + sector * SectorSize;
  }

  private static string DecodeName(ReadOnlySpan<byte> bytes) {
    var end = bytes.IndexOf((byte)0xA0);
    if (end < 0) end = bytes.Length;
    return Encoding.ASCII.GetString(bytes[..end]);
  }

  private static string NormalizeName(string name) => name.ToUpperInvariant();

  private static void WriteDirectoryName(byte[] image, int entryOffset, string name) {
    var encoded = Encoding.ASCII.GetBytes(NormalizeName(name));
    image.AsSpan(entryOffset + 5, 16).Fill(0xA0);
    encoded.AsSpan(0, Math.Min(16, encoded.Length)).CopyTo(image.AsSpan(entryOffset + 5, 16));
  }

  private static void ValidateName(string name) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    if (name.Length > 16) throw new ArgumentException("CBM DOS names are limited to 16 characters.", nameof(name));
    if (name.IndexOfAny(['/', '\\', '\0']) >= 0)
      throw new ArgumentException("CBM DOS file names cannot contain path separators or NUL.", nameof(name));
  }

  private void RequireRoot(FilesystemNodeId nodeId) {
    if (nodeId != RootNodeId) throw new DirectoryNotFoundException("CBM DOS 2.6 has only the root directory.");
  }

  private void EnsureWritable() {
    if (_readOnly) throw new NotSupportedException("The CBM DOS session was opened read-only.");
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

  private sealed class NodeState(FilesystemNodeId nodeId, string name) {
    public FilesystemNodeId NodeId { get; } = nodeId;
    public string Name { get; set; } = name;
    public byte[]? Data { get; set; }
    public bool Dirty { get; set; }
    public bool Linked { get; set; } = true;
  }

  private sealed record DirectoryRecord(
    string Name,
    byte FileType,
    byte StartTrack,
    byte StartSector,
    ushort SectorCount,
    long Size,
    int DirectoryEntryOffset
  );

  private sealed class FileHandle(D64FilesystemSession session, NodeState state, FileAccess access) : IFilesystemFileHandle {
    private bool _disposed;

    public FilesystemNodeId NodeId => state.NodeId;
    public long Length {
      get {
        lock (session._gate) {
          ThrowIfDisposed();
          return state.Data?.LongLength ?? 0;
        }
      }
    }

    public int Read(long offset, Span<byte> destination) {
      lock (session._gate) {
        ThrowIfDisposed();
        if (access == FileAccess.Write) throw new NotSupportedException("This handle was opened write-only.");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var data = state.Data ?? [];
        if (offset >= data.LongLength) return 0;
        var count = (int)Math.Min(destination.Length, data.LongLength - offset);
        data.AsSpan((int)offset, count).CopyTo(destination);
        return count;
      }
    }

    public void Write(long offset, ReadOnlySpan<byte> source) {
      lock (session._gate) {
        ThrowIfDisposed();
        if (access == FileAccess.Read) throw new NotSupportedException("This handle was opened read-only.");
        session.EnsureWritable();
        if (offset < 0 || offset > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(offset));
        var end = checked(offset + source.Length);
        if (end > int.MaxValue) throw new IOException("CBM DOS file is too large for the 1541 image.");
        var data = state.Data ?? [];
        if (end > data.LongLength) Array.Resize(ref data, (int)end);
        source.CopyTo(data.AsSpan((int)offset));
        state.Data = data;
        state.Dirty = true;
      }
    }

    public void SetLength(long length) {
      lock (session._gate) {
        ThrowIfDisposed();
        if (access == FileAccess.Read) throw new NotSupportedException("This handle was opened read-only.");
        session.EnsureWritable();
        if (length < 0 || length > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(length));
        var data = state.Data ?? [];
        Array.Resize(ref data, (int)length);
        state.Data = data;
        state.Dirty = true;
      }
    }

    public void Flush() {
      ThrowIfDisposed();
      if (access != FileAccess.Read) session.FlushState(state);
      session._device.Flush();
    }

    public void Dispose() {
      if (_disposed) return;
      if (access != FileAccess.Read && !session._disposed) Flush();
      _disposed = true;
    }

    private void ThrowIfDisposed() {
      ObjectDisposedException.ThrowIf(_disposed, this);
      session.ThrowIfDisposed();
    }
  }
}
