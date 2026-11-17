using System.Security.Cryptography;
using System.Text;
using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// A writable, log-structured view of a read-only archive. Changes are persisted in a
/// separate, append-only sidecar; the original archive is never modified. No claim is
/// made that another archive reader can see the overlay before an explicit export.
/// </summary>
public sealed class ArchiveOverlayFilesystemSession : IFilesystemSession {
  private const int MaxRecordBytes = 1024 * 1024 + 4096;
  private const int WriteChunkBytes = 1024 * 1024;
  private static ReadOnlySpan<byte> Magic => "CWBOVL01"u8;
  private readonly object _gate = new();
  private readonly IFilesystemSession _source;
  private readonly FileStream _log;
  private readonly IDisposable[] _owners;
  private readonly Dictionary<FilesystemNodeId, Node> _nodes = [];
  private ulong _nextId = 1;
  private bool _disposed;

  private enum Operation : byte { File = 1, Directory = 2, Unlink = 3, Move = 4, Write = 5, Resize = 6 }
  private sealed record Patch(long Start, int Length, long LogOffset) {
    public long End => Start + Length;
  }

  private sealed class Node {
    public required FilesystemNodeId Id { get; init; }
    public required FilesystemNodeKind Kind { get; init; }
    public required string Name { get; set; }
    public required Node? Parent { get; set; }
    public FilesystemNodeId? SourceId { get; init; }
    public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Patch> Patches { get; set; } = [];
    public long Length { get; set; }
    public long SourceLimit { get; set; }
    public bool Unlinked { get; set; }
  }

  /// <summary>
  /// Returns a writable sidecar profile only for ordinary, safely enumerable archives.
  /// Does not upgrade the descriptor's WORM/CanModify capability.
  /// </summary>
  public static FilesystemDriverProfile? Probe(IFormatDescriptor descriptor, Stream archive, string? password = null) {
    ArgumentNullException.ThrowIfNull(descriptor);
    ArgumentNullException.ThrowIfNull(archive);
    if (descriptor is not (IArchiveFormatOperations and IArchiveCreatable) ||
        descriptor is IArchiveModifiable or IFilesystemDriverProvider or IRandomAccessBlockDeviceProvider ||
        descriptor.Category is not (FormatCategory.Archive or FormatCategory.CompoundTar) ||
        !string.IsNullOrEmpty(password) || !archive.CanRead || !archive.CanSeek)
      return null;

    var saved = archive.Position;
    try {
      archive.Position = 0;
      var entries = ((IArchiveFormatOperations)descriptor).List(archive, null);
      var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var entry in entries) {
        var path = entry.Name.Replace('\\', '/').Trim('/');
        if (entry.IsEncrypted || entry.OriginalSize < 0 || entry.IsSymlink || entry.LinkTarget is not null ||
            !SafePath(path) || !names.Add(path))
          return null;
      }

      return new FilesystemDriverProfile(
        descriptor.Id,
        "append-only sidecar archive overlay",
        FilesystemMountCapabilityResolver.CoreReadCapabilities |
        FilesystemMountCapabilityResolver.CoreWriteCapabilities |
        FilesystemDriverCapabilities.CreateDirectory |
        FilesystemDriverCapabilities.RemoveDirectory |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.LogStructured,
        CanMount: true,
        CanMountWritable: true,
        [
          "Writes persist in an adjacent .cwb-overlay journal, never inside the original archive.",
          "Other archive readers see the unchanged original until a separate, validated native export.",
          "The source must remain immutable; a changed source identity refuses overlay replay.",
          "Transactions, hard links, symlink creation and arbitrary metadata writes are unsupported.",
        ]);
    } catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or NotSupportedException or OverflowException) {
      return null;
    } finally {
      archive.Position = saved;
    }
  }

  /// <summary>Opens a local WORM archive as a persistent writable view.</summary>
  public static ArchiveOverlayFilesystemSession OpenPath(IFormatDescriptor descriptor, string archivePath, string? password = null) {
    ArgumentNullException.ThrowIfNull(descriptor);
    ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
    archivePath = Path.GetFullPath(archivePath);
    var mountLock = new FileStream(archivePath + ".cwbmount.lock", FileMode.OpenOrCreate,
      FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
    FileStream? source = null;
    IFilesystemSession? underlying = null;
    FileStream? journal = null;
    try {
      ArchiveAtomicPublication.Recover(archivePath);
      if (File.Exists(JournaledArchiveWriteback.JournalPath(archivePath)))
        throw new IOException("The original archive has an unfinished native undo journal.");
      source = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
      var profile = Probe(descriptor, source, password)
        ?? throw new NotSupportedException("The archive does not qualify for a writable sidecar overlay.");
      var identity = Fingerprint(archivePath, descriptor.Id, source);
      source.Position = 0;
      underlying = FilesystemDriverDerivation.Open(descriptor, source,
        new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true), password, preserveFileIdentity: true);
      journal = new FileStream(archivePath + ".cwb-overlay", FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.None);
      var session = new ArchiveOverlayFilesystemSession(underlying, journal, identity, profile, source, mountLock);
      underlying = null;
      journal = null;
      source = null;
      return session;
    } catch {
      journal?.Dispose();
      underlying?.Dispose();
      source?.Dispose();
      mountLock.Dispose();
      throw;
    }
  }

  private ArchiveOverlayFilesystemSession(
      IFilesystemSession source,
      FileStream journal,
      byte[] identity,
      FilesystemDriverProfile profile,
      params IDisposable[] owners) {
    _source = source;
    _log = journal;
    _owners = owners;
    Profile = profile;
    var root = new Node {
      Id = RootNodeId, Name = "", Parent = null,
      Kind = FilesystemNodeKind.Directory, SourceId = source.RootNodeId,
    };
    _nodes.Add(root.Id, root);
    CopyTree(root);
    Load(identity);
  }

  public FilesystemDriverProfile Profile { get; }
  public FilesystemNodeId RootNodeId { get; } = new(1, 1);

  private void CopyTree(Node parent) {
    foreach (var entry in _source.Enumerate(parent.SourceId!.Value).OrderBy(e => e.Name, StringComparer.Ordinal)) {
      ValidateName(entry.Name);
      if (parent.Children.ContainsKey(entry.Name))
        throw new InvalidDataException("Archive contains case-insensitively ambiguous names.");
      var info = _source.Stat(entry.NodeId);
      var node = new Node {
        Id = NewId(), Name = entry.Name, Parent = parent, Kind = entry.Kind,
        SourceId = entry.NodeId, Length = info.Size, SourceLimit = info.Size,
      };
      parent.Children.Add(node.Name, node);
      _nodes.Add(node.Id, node);
      if (node.Kind == FilesystemNodeKind.Directory)
        CopyTree(node);
    }
  }

  private static bool SafePath(string path)
    => path.Length != 0 && path.Split('/').All(part =>
      part.Length != 0 && SafeName(part));

  private static bool SafeName(string name)
    => name is not ("" or "." or "..") && name.Length <= 255 &&
       name.IndexOfAny(['/', '\\', ':', '\0']) < 0 &&
       !name.Any(char.IsControl);

  private static void ValidateName(string name) {
    if (!SafeName(name))
      throw new ArgumentException("Unsafe or unsupported archive filename.", nameof(name));
  }

  private FilesystemNodeId NewId() => new(++_nextId, 1);

  private static byte[] Fingerprint(string path, string formatId, FileStream source) {
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var info = new FileInfo(path);
    hash.AppendData(Encoding.UTF8.GetBytes(
      $"{Path.GetFullPath(path)}\0{formatId}\0{info.Length}\0{info.LastWriteTimeUtc.Ticks}"));
    var buffer = new byte[64 * 1024];
    source.Position = 0;
    var head = source.Read(buffer);
    hash.AppendData(buffer.AsSpan(0, head));
    if (source.Length > buffer.Length) {
      source.Position = Math.Max(0, source.Length - buffer.Length);
      var tail = source.Read(buffer);
      hash.AppendData(buffer.AsSpan(0, tail));
    }
    return hash.GetHashAndReset();
  }

  private void Load(byte[] identity) {
    if (_log.Length == 0) {
      _log.Write(Magic);
      _log.Write(identity);
      _log.Flush(true);
      return;
    }

    if (_log.Length < Magic.Length + identity.Length)
      throw new InvalidDataException("Incomplete archive overlay header.");
    _log.Position = 0;
    Span<byte> magic = stackalloc byte[8];
    _log.ReadExactly(magic);
    Span<byte> savedIdentity = stackalloc byte[32];
    _log.ReadExactly(savedIdentity);
    if (!magic.SequenceEqual(Magic) || !savedIdentity.SequenceEqual(identity))
      throw new InvalidDataException("Overlay version or original archive identity does not match.");

    while (_log.Position < _log.Length) {
      var recordStart = _log.Position;
      if (_log.Length - recordStart < 4) {
        _log.SetLength(recordStart);
        break;
      }
      using var reader = new BinaryReader(_log, Encoding.UTF8, leaveOpen: true);
      var length = reader.ReadInt32();
      if (length <= 0 || length > MaxRecordBytes)
        throw new InvalidDataException("Invalid archive overlay record length.");
      if (_log.Length - _log.Position < 32L + length) {
        _log.SetLength(recordStart); // interrupted final record only
        break;
      }
      var checksum = reader.ReadBytes(32);
      var bodyOffset = _log.Position;
      var body = reader.ReadBytes(length);
      if (!SHA256.HashData(body).AsSpan().SequenceEqual(checksum))
        throw new InvalidDataException("Archive overlay record checksum mismatch.");
      Replay(body, bodyOffset);
    }
  }

  private long Append(byte[] record) {
    if (record.Length > MaxRecordBytes)
      throw new ArgumentOutOfRangeException(nameof(record));
    var start = _log.Length;
    try {
      _log.Position = start;
      using var writer = new BinaryWriter(_log, Encoding.UTF8, leaveOpen: true);
      writer.Write(record.Length);
      writer.Write(SHA256.HashData(record));
      var bodyOffset = _log.Position;
      writer.Write(record);
      return bodyOffset;
    } catch {
      _log.SetLength(start);
      throw;
    }
  }

  private long Record(Operation operation, Action<BinaryWriter> fields) {
    using var buffer = new MemoryStream();
    using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true)) {
      writer.Write((byte)operation);
      fields(writer);
    }
    return Append(buffer.ToArray());
  }

  private void Replay(byte[] body, long bodyOffset) {
    using var buffer = new MemoryStream(body, writable: false);
    using var reader = new BinaryReader(buffer, Encoding.UTF8);
    var operation = (Operation)reader.ReadByte();
    switch (operation) {
      case Operation.File:
      case Operation.Directory: {
        var id = new FilesystemNodeId(reader.ReadUInt64(), 1);
        var parent = RequireDirectory(new(reader.ReadUInt64(), 1));
        var name = reader.ReadString();
        ValidateName(name);
        if (_nodes.ContainsKey(id) || parent.Children.ContainsKey(name) || id.Value <= _nextId)
          throw new InvalidDataException("Duplicate overlay node.");
        _nextId = id.Value;
        Create(parent, name, operation == Operation.Directory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile, id);
        break;
      }
      case Operation.Unlink: {
        var node = Require(new(reader.ReadUInt64(), 1));
        Unlink(node, node.Kind == FilesystemNodeKind.Directory);
        break;
      }
      case Operation.Move: {
        var node = Require(new(reader.ReadUInt64(), 1));
        var parent = RequireDirectory(new(reader.ReadUInt64(), 1));
        Move(node, parent, reader.ReadString(), reader.ReadBoolean());
        break;
      }
      case Operation.Write: {
        var node = RequireRegular(new(reader.ReadUInt64(), 1));
        var offset = reader.ReadInt64();
        var count = reader.ReadInt32();
        if (offset < 0 || count < 0 || count > WriteChunkBytes ||
            count > body.Length - buffer.Position || offset > long.MaxValue - count)
          throw new InvalidDataException("Invalid overlay data extent.");
        ApplyWrite(node, offset, count, bodyOffset + buffer.Position);
        buffer.Position += count;
        break;
      }
      case Operation.Resize: {
        var node = RequireRegular(new(reader.ReadUInt64(), 1));
        Resize(node, reader.ReadInt64());
        break;
      }
      default:
        throw new InvalidDataException("Unknown archive overlay operation.");
    }
    if (buffer.Position != buffer.Length)
      throw new InvalidDataException("Trailing bytes in archive overlay record.");
  }

  private Node Require(FilesystemNodeId id)
    => _nodes.TryGetValue(id, out var node) ? node : throw new FileNotFoundException("Unknown overlay node.");

  private Node RequireDirectory(FilesystemNodeId id) {
    var node = Require(id);
    if (node.Kind != FilesystemNodeKind.Directory || node.Unlinked)
      throw new DirectoryNotFoundException("Overlay directory not found.");
    return node;
  }

  private Node RequireRegular(FilesystemNodeId id) {
    var node = Require(id);
    if (node.Kind != FilesystemNodeKind.RegularFile)
      throw new UnauthorizedAccessException("Not a regular file.");
    return node;
  }

  private static Node? Child(Node parent, string name)
    => parent.Children.GetValueOrDefault(name);

  private Node Create(Node parent, string name, FilesystemNodeKind kind, FilesystemNodeId id) {
    var node = new Node { Id = id, Parent = parent, Name = name, Kind = kind };
    _nodes.Add(id, node);
    parent.Children.Add(name, node);
    return node;
  }

  private void Unlink(Node node, bool directory) {
    if (node.Parent is null || node.Unlinked || (node.Kind == FilesystemNodeKind.Directory) != directory)
      throw new IOException("Invalid overlay unlink.");
    if (directory && node.Children.Count != 0)
      throw new IOException("Directory is not empty.");
    node.Parent.Children.Remove(node.Name);
    node.Unlinked = true;
  }

  private void Move(Node node, Node parent, string newName, bool replace) {
    ValidateName(newName);
    if (node.Parent is null || node.Unlinked)
      throw new IOException("Cannot move an unlinked or root node.");
    for (var current = parent; current is not null; current = current.Parent)
      if (current == node)
        throw new IOException("Cannot move directory into itself.");
    var target = Child(parent, newName);
    if (target == node) {
      if (node.Name != newName) {
        node.Parent.Children.Remove(node.Name);
        node.Name = newName;
        parent.Children.Add(node.Name, node);
      }
      return;
    }
    if (target != null) {
      if (!replace || target.Kind != node.Kind || target.Kind == FilesystemNodeKind.Directory && target.Children.Count > 0)
        throw new IOException("Destination already exists or is incompatible.");
      Unlink(target, target.Kind == FilesystemNodeKind.Directory);
    }
    node.Parent.Children.Remove(node.Name);
    node.Parent = parent;
    node.Name = newName;
    parent.Children.Add(node.Name, node);
  }

  private static void Resize(Node node, long length) {
    if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
    if (length < node.Length) {
      node.SourceLimit = Math.Min(node.SourceLimit, length);
      node.Patches = node.Patches.Where(p => p.Start < length)
        .Select(p => p.End <= length ? p : p with { Length = checked((int)(length - p.Start)) }).ToList();
    }
    node.Length = length;
  }

  private static void ApplyWrite(Node node, long offset, int length, long logOffset) {
    var end = checked(offset + length);
    var updated = new List<Patch>();
    foreach (var patch in node.Patches) {
      if (patch.End <= offset || patch.Start >= end) {
        updated.Add(patch);
      } else {
        if (patch.Start < offset)
          updated.Add(patch with { Length = checked((int)(offset - patch.Start)) });
        if (patch.End > end)
          updated.Add(new Patch(end, checked((int)(patch.End - end)),
            patch.LogOffset + end - patch.Start));
      }
    }
    updated.Add(new Patch(offset, length, logOffset));
    updated.Sort(static (a, b) => a.Start.CompareTo(b.Start));
    node.Patches = updated;
    node.Length = Math.Max(node.Length, end);
  }

  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) {
    lock (_gate) {
      Check();
      var node = Require(nodeId);
      var info = node.SourceId is { } id ? _source.Stat(id) :
        new FilesystemNodeInfo(node.Id, node.Kind, node.Length, node.Length);
      return info with { NodeId = node.Id, Kind = node.Kind, Size = node.Length,
        AllocatedSize = node.SourceId is null ? node.Patches.Sum(p => (long)p.Length) : info.AllocatedSize };
    }
  }

  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      Check();
      return Child(RequireDirectory(parentDirectory), name)?.Id;
    }
  }

  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) {
    lock (_gate) {
      Check();
      return RequireDirectory(directory).Children.Values
        .OrderBy(n => n.Name, StringComparer.Ordinal)
        .Select(n => new FilesystemDirectoryEntry(n.Name, n.Id, n.Kind)).ToArray();
    }
  }

  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) {
    lock (_gate) {
      Check();
      return new OverlayHandle(this, RequireRegular(nodeId), access);
    }
  }

  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name)
    => CreateEntry(parentDirectory, name, FilesystemNodeKind.RegularFile, Operation.File);

  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name)
    => CreateEntry(parentDirectory, name, FilesystemNodeKind.Directory, Operation.Directory);

  private FilesystemNodeId CreateEntry(FilesystemNodeId parentId, string name, FilesystemNodeKind kind, Operation operation) {
    lock (_gate) {
      Check();
      ValidateName(name);
      var parent = RequireDirectory(parentId);
      if (Child(parent, name) is not null) throw new IOException("Name already exists.");
      var id = new FilesystemNodeId(_nextId + 1, 1);
      Record(operation, w => { w.Write(id.Value); w.Write(parent.Id.Value); w.Write(name); });
      _nextId = id.Value;
      return Create(parent, name, kind, id).Id;
    }
  }

  public void DeleteFile(FilesystemNodeId parentDirectory, string name) => Delete(parentDirectory, name, false);
  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) => Delete(parentDirectory, name, true);

  private void Delete(FilesystemNodeId parentDirectory, string name, bool directory) {
    lock (_gate) {
      Check();
      var node = Child(RequireDirectory(parentDirectory), name)
        ?? throw new FileNotFoundException("Archive overlay entry not found.", name);
      if ((node.Kind == FilesystemNodeKind.Directory) != directory ||
          directory && node.Children.Count != 0)
        throw new IOException("Entry kind does not match or directory is not empty.");
      Record(Operation.Unlink, w => w.Write(node.Id.Value));
      Unlink(node, directory);
    }
  }

  public void Rename(FilesystemNodeId oldParent, string oldName, FilesystemNodeId newParent, string newName, bool replace) {
    lock (_gate) {
      Check();
      var node = Child(RequireDirectory(oldParent), oldName)
        ?? throw new FileNotFoundException("Archive overlay entry not found.", oldName);
      var targetParent = RequireDirectory(newParent);
      ValidateName(newName);
      // Validate before publishing the operation to the journal.
      for (var p = targetParent; p is not null; p = p.Parent)
        if (p == node) throw new IOException("Cannot move a directory into itself.");
      var existing = Child(targetParent, newName);
      if (existing != null && existing != node &&
          (!replace || existing.Kind != node.Kind ||
           existing.Kind == FilesystemNodeKind.Directory && existing.Children.Count != 0))
        throw new IOException("Destination already exists or is incompatible.");
      if (existing == node && node.Name == newName) return;
      Record(Operation.Move, w => {
        w.Write(node.Id.Value); w.Write(targetParent.Id.Value); w.Write(newName); w.Write(replace);
      });
      Move(node, targetParent, newName, replace);
    }
  }

  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string name)
    => throw new NotSupportedException("Archive overlays do not support hard links.");
  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target)
    => throw new NotSupportedException("Archive overlays do not support new symbolic links.");

  public string ReadSymbolicLink(FilesystemNodeId nodeId) {
    lock (_gate) {
      Check();
      var node = Require(nodeId);
      if (node.Kind != FilesystemNodeKind.SymbolicLink || node.SourceId is not { } sourceId)
        throw new InvalidOperationException("Not a source symbolic link.");
      return _source.ReadSymbolicLink(sourceId);
    }
  }

  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch)
    => throw new NotSupportedException("Arbitrary metadata modifications are not supported by the overlay.");

  public void Flush() {
    lock (_gate) {
      Check();
      _log.Flush(true);
    }
  }

  public IFilesystemTransaction BeginTransaction()
    => throw new NotSupportedException("Multi-operation transactions are not advertised.");

  public void Dispose() {
    lock (_gate) {
      if (_disposed) return;
      _log.Flush(true);
      _disposed = true;
      try { _log.Dispose(); }
      finally {
        try { _source.Dispose(); }
        finally {
          foreach (var owner in _owners)
            owner.Dispose();
        }
      }
    }
  }

  private void Check() {
    if (_disposed) throw new ObjectDisposedException(nameof(ArchiveOverlayFilesystemSession));
  }

  private sealed class OverlayHandle(ArchiveOverlayFilesystemSession session, Node node, FileAccess access)
      : IFilesystemFileHandle {
    private IFilesystemFileHandle? _sourceHandle;
    private bool _closed;
    public FilesystemNodeId NodeId => node.Id;
    public long Length {
      get {
        lock (session._gate) {
          EnsureOpen();
          return node.Length;
        }
      }
    }

    public int Read(long offset, Span<byte> destination) {
      lock (session._gate) {
        EnsureOpen();
        if (access == FileAccess.Write) throw new NotSupportedException("Handle is write-only.");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset >= node.Length || destination.IsEmpty) return 0;
        var count = (int)Math.Min(destination.Length, node.Length - offset);
        var target = destination[..count];
        target.Clear();
        if (node.SourceId is { } sourceId && offset < node.SourceLimit) {
          _sourceHandle ??= session._source.OpenFile(sourceId, FileAccess.Read);
          var fromSource = (int)Math.Min(count, node.SourceLimit - offset);
          var filled = 0;
          while (filled < fromSource) {
            var got = _sourceHandle.Read(offset + filled, target.Slice(filled, fromSource - filled));
            if (got == 0) break;
            filled += got;
          }
        }
        foreach (var patch in node.Patches) {
          var start = Math.Max(offset, patch.Start);
          var end = Math.Min(offset + count, patch.End);
          if (start >= end) continue;
          session._log.Position = patch.LogOffset + start - patch.Start;
          session._log.ReadExactly(target.Slice((int)(start - offset), (int)(end - start)));
        }
        return count;
      }
    }

    public void Write(long offset, ReadOnlySpan<byte> source) {
      lock (session._gate) {
        EnsureOpen();
        if (access == FileAccess.Read) throw new NotSupportedException("Handle is read-only.");
        if (offset < 0 || offset > long.MaxValue - source.Length)
          throw new ArgumentOutOfRangeException(nameof(offset));
        while (!source.IsEmpty) {
          var count = Math.Min(source.Length, WriteChunkBytes);
          var chunk = source[..count];
          using var record = new MemoryStream(count + 32);
          using (var writer = new BinaryWriter(record, Encoding.UTF8, leaveOpen: true)) {
            writer.Write((byte)Operation.Write);
            writer.Write(node.Id.Value);
            writer.Write(offset);
            writer.Write(count);
            writer.Write(chunk);
          }
          var body = session.Append(record.ToArray());
          ApplyWrite(node, offset, count, body + 1 + 8 + 8 + 4);
          offset += count;
          source = source[count..];
        }
      }
    }

    public void SetLength(long length) {
      lock (session._gate) {
        EnsureOpen();
        if (access == FileAccess.Read) throw new NotSupportedException("Handle is read-only.");
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        session.Record(Operation.Resize, w => { w.Write(node.Id.Value); w.Write(length); });
        Resize(node, length);
      }
    }

    public void Flush() => session.Flush();

    public void Dispose() {
      lock (session._gate) {
        if (_closed) return;
        _closed = true;
        _sourceHandle?.Dispose();
      }
    }

    private void EnsureOpen() {
      session.Check();
      if (_closed) throw new ObjectDisposedException(nameof(OverlayHandle));
    }
  }
}
