using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Mounting;

/// <summary>
/// Temp-file-backed mutable archive namespace. One file per node avoids array-sized
/// allocations, including for sparse offsets beyond 2 GiB. Publication happens
/// only at Flush, after rebuilding and verifying a complete candidate archive.
/// </summary>
internal sealed class FileBackedArchiveFilesystemSession : IFilesystemSession {
  private sealed class Node {
    public required FilesystemNodeId Id;
    public required FilesystemNodeId Parent;
    public required string Name;
    public required FilesystemNodeKind Kind;
    public string? DataPath;
    public bool Linked = true;
  }

  private readonly object _gate = new();
  private readonly IArchiveFormatOperations _archive;
  private readonly IArchiveCreatable _writer;
  private readonly IArchiveModifiable? _nativeZipModifier;
  private readonly Dictionary<string, (string RawName, bool Directory, byte[]? ContentHash)> _published = new(StringComparer.Ordinal);
  private readonly Stream? _image;
  private readonly string? _imagePath;
  private readonly string _stagePath;
  private readonly FileStream? _mountLock;
  private readonly string? _password;
  private readonly Dictionary<FilesystemNodeId, Node> _nodes = [];
  private readonly Dictionary<FilesystemNodeId, Dictionary<string, Node>> _children = [];
  private readonly Dictionary<string, ArchiveEntryMetadata> _metadata = new(StringComparer.Ordinal);
  private readonly StringComparer _names;
  private readonly FilesystemNodeId _root = new(1, 1);
  private ulong _nextId = 1;
  private byte[]? _baseline;
  private bool _dirty;
  private bool _disposed;

  public FileBackedArchiveFilesystemSession(
      IFormatDescriptor descriptor,
      FilesystemDriverProfile profile,
      Stream? image,
      string? imagePath,
      string? password,
      FileStream? existingMountLock = null) {
    ArgumentNullException.ThrowIfNull(descriptor);
    ArgumentNullException.ThrowIfNull(profile);
    if (descriptor is not IArchiveFormatOperations archive || descriptor is not IArchiveCreatable writer)
      throw new ArgumentException("Writable archive projection requires archive reading and creation.", nameof(descriptor));
    if (!profile.CanMountWritable)
      throw new ArgumentException("A writable archive profile is required.", nameof(profile));
    if ((image is null) == (imagePath is null))
      throw new ArgumentException("Supply exactly one archive stream or pathname.");
    if (image is not null && (!image.CanRead || !image.CanWrite || !image.CanSeek))
      throw new ArgumentException("Stream-backed writable archives require read/write/seek.", nameof(image));

    Profile = profile;
    _archive = archive;
    _writer = writer;
    // Qualified native mutation: ZIP's ZipModifier streams changes into its
    // existing central-directory layout. The operation runs on a disposable
    // same-directory candidate, never on the live source.
    _nativeZipModifier = descriptor.Id.Equals("Zip", StringComparison.OrdinalIgnoreCase)
      ? descriptor as IArchiveModifiable : null;
    _image = image;
    _imagePath = imagePath is null ? null : Path.GetFullPath(imagePath);
    _password = password;
    _names = (profile.Capabilities & FilesystemDriverCapabilities.CaseSensitiveNames) != 0
      ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    _stagePath = Path.Combine(Path.GetTempPath(), "cwb-archive-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_stagePath);
    try {
      if (_imagePath is not null) {
        _mountLock = existingMountLock ?? new FileStream(_imagePath + ".cwbmount.lock", FileMode.OpenOrCreate,
          FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        ArchiveAtomicPublication.Recover(_imagePath);
        _baseline = ArchiveAtomicPublication.HashFile(_imagePath);
      }

      var root = new Node { Id = _root, Parent = _root, Name = "", Kind = FilesystemNodeKind.Directory };
      _nodes.Add(_root, root);
      _children.Add(_root, NewChildren());

      using var input = OpenSource();
      input.Position = 0;
      var listing = _archive.List(input, password);
      foreach (var entry in listing.OrderBy(static x => Depth(x.Name)).ThenBy(static x => x.Name, StringComparer.Ordinal))
        AddEntry(input, entry);
      foreach (var entry in listing) {
        var name = entry.Name.Replace('\\', '/').Trim('/');
        var node = FindPath(name);
        _published.Add(name, (entry.Name, entry.IsDirectory,
          node.DataPath is null ? null : ArchiveAtomicPublication.HashFile(node.DataPath)));
      }
    } catch {
      _mountLock?.Dispose();
      Directory.Delete(_stagePath, recursive: true);
      throw;
    }
  }

  public FilesystemDriverProfile Profile { get; }
  public FilesystemNodeId RootNodeId => _root;

  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) {
    lock (_gate) {
      RequireActive();
      var node = RequireNode(nodeId);
      var length = node.DataPath is null ? 0 : new FileInfo(node.DataPath).Length;
      return new(nodeId, node.Kind, length, length, LinkCount: node.Linked ? 1U : 0U);
    }
  }

  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      RequireActive();
      var parent = RequireDirectory(parentDirectory);
      return _children[parent.Id].TryGetValue(name, out var child) && child.Linked ? child.Id : null;
    }
  }

  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) {
    lock (_gate) {
      RequireActive();
      var parent = RequireDirectory(directory);
      return _children[parent.Id].Values.Where(static n => n.Linked)
        .OrderBy(static n => n.Name, StringComparer.Ordinal)
        .Select(static n => new FilesystemDirectoryEntry(n.Name, n.Id, n.Kind)).ToArray();
    }
  }

  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) {
    lock (_gate) {
      RequireActive();
      var node = RequireNode(nodeId);
      if (node.Kind != FilesystemNodeKind.RegularFile)
        throw new UnauthorizedAccessException("Only regular files can be opened.");
      if ((access & FileAccess.Read) != 0) Require(FilesystemDriverCapabilities.ReadData);
      if ((access & FileAccess.Write) != 0) Require(FilesystemDriverCapabilities.WriteData);
      return new Handle(this, node, access);
    }
  }

  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      RequireActive();
      Require(FilesystemDriverCapabilities.CreateFile);
      var node = Insert(parentDirectory, name, FilesystemNodeKind.RegularFile);
      node.DataPath = NewDataPath();
      using (File.Create(node.DataPath)) { }
      _dirty = true;
      return node.Id;
    }
  }

  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      RequireActive();
      Require(FilesystemDriverCapabilities.CreateDirectory);
      var node = Insert(parentDirectory, name, FilesystemNodeKind.Directory);
      _children.Add(node.Id, NewChildren());
      _dirty = true;
      return node.Id;
    }
  }

  public void DeleteFile(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      RequireActive();
      Require(FilesystemDriverCapabilities.DeleteFile);
      var node = Child(parentDirectory, name);
      if (node.Kind != FilesystemNodeKind.RegularFile)
        throw new UnauthorizedAccessException("Not a regular file.");
      Unlink(node);
    }
  }

  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      RequireActive();
      Require(FilesystemDriverCapabilities.RemoveDirectory);
      var node = Child(parentDirectory, name);
      if (node.Kind != FilesystemNodeKind.Directory)
        throw new DirectoryNotFoundException(name);
      if (_children[node.Id].Count != 0)
        throw new IOException("Directory is not empty.");
      Unlink(node);
    }
  }

  public void Rename(FilesystemNodeId oldParent, string oldName,
      FilesystemNodeId newParent, string newName, bool replace) {
    lock (_gate) {
      RequireActive();
      Require(FilesystemDriverCapabilities.Rename);
      CheckComponent(newName);
      var oldDirectory = RequireDirectory(oldParent);
      var targetDirectory = RequireDirectory(newParent);
      var node = Child(oldParent, oldName);
      if (node.Kind == FilesystemNodeKind.Directory) {
        for (var p = targetDirectory; p.Id != _root; p = RequireNode(p.Parent))
          if (p.Id == node.Id) throw new IOException("Cannot move a directory into itself.");
        if (targetDirectory.Id == node.Id) throw new IOException("Cannot move a directory into itself.");
      }

      if (_children[targetDirectory.Id].TryGetValue(newName, out var existing)) {
        if (existing.Id == node.Id) return;
        if (!replace || existing.Kind != node.Kind ||
            (existing.Kind == FilesystemNodeKind.Directory && _children[existing.Id].Count > 0))
          throw new IOException("Rename destination already exists.");
        Unlink(existing);
      }
      _children[oldDirectory.Id].Remove(node.Name);
      node.Parent = targetDirectory.Id;
      node.Name = newName;
      _children[targetDirectory.Id].Add(newName, node);
      _dirty = true;
    }
  }

  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName)
    => throw new NotSupportedException("Archive rebuilds do not preserve hard links.");
  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target)
    => throw new NotSupportedException("Archive rebuilds do not preserve symbolic links.");
  public string ReadSymbolicLink(FilesystemNodeId nodeId)
    => throw new NotSupportedException("Archive rebuilds do not expose symbolic links.");
  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch)
    => throw new NotSupportedException("Archive rebuilds do not preserve arbitrary metadata.");

  public void Flush() {
    lock (_gate) {
      RequireActive();
      if (!_dirty) {
        if (_image is not null) WholeImageRebuildCommitter.FlushDurably(_image);
        return;
      }

      var entries = LinkedEntries();
      void Rebuild(Stream output) {
        var inputs = entries.Select(n => new StreamingArchiveInput(
          PathOf(n), n.DataPath is null ? 0 : new FileInfo(n.DataPath).Length,
          n.Kind == FilesystemNodeKind.Directory,
          n.DataPath is null ? () => Stream.Null : () => File.OpenRead(n.DataPath),
          _metadata.GetValueOrDefault(PathOf(n))));
        _writer.CreateFromStreams(output, inputs, new FormatCreateOptions { Password = _password });
      }

      // Use native ZIP Add/Remove on a private candidate when possible. Unlike
      // rebuilding from scratch this preserves untouched compressed payloads,
      // directory records and the original archive comment. All results still
      // undergo the same byte-for-byte verification before publication.
      var current = entries.ToDictionary(PathOf, StringComparer.Ordinal);
      var deltaRemove = _published
        .Where(old => !current.TryGetValue(old.Key, out var now) ||
          (now.Kind == FilesystemNodeKind.Directory) != old.Value.Directory)
        .Select(old => old.Value.RawName).ToArray();
      var deltaAdd = entries.Where(now =>
        !_published.TryGetValue(PathOf(now), out var prior) ||
        prior.Directory != (now.Kind == FilesystemNodeKind.Directory) ||
        (now.DataPath is not null &&
          !ArchiveAtomicPublication.HashFile(now.DataPath).AsSpan().SequenceEqual(prior.ContentHash)))
        .ToArray();

      void ApplyNativeDelta(Stream output) {
        using (var source = OpenSource()) {
          source.Position = 0;
          source.CopyTo(output);
        }
        output.Position = 0;
        if (deltaRemove.Length > 0)
          _nativeZipModifier!.Remove(output, deltaRemove);
        if (deltaAdd.Length > 0)
          _nativeZipModifier!.Add(output, deltaAdd.Select(n =>
            new ArchiveInputInfo(n.DataPath ?? PathOf(n), PathOf(n), n.DataPath is null,
              Metadata: _metadata.GetValueOrDefault(PathOf(n)))).ToArray());
      }

      void Validate(Stream candidate) {
        candidate.Position = 0;
        var listed = _archive.List(candidate, _password);
        if (listed.Count != entries.Length)
          throw new InvalidDataException("Candidate archive has an unexpected entry count.");
        var expected = entries.ToDictionary(PathOf, StringComparer.Ordinal);
        foreach (var entry in listed) {
          var name = entry.Name.Replace('\\', '/').Trim('/');
          if (!expected.TryGetValue(name, out var node) ||
              entry.IsDirectory != (node.Kind == FilesystemNodeKind.Directory))
            throw new InvalidDataException("Candidate archive namespace differs from staged content.");
          if (node.DataPath is null) continue;
          var length = new FileInfo(node.DataPath).Length;
          if (entry.OriginalSize != length)
            throw new InvalidDataException("Candidate archive entry length differs from staged content.");
          candidate.Position = 0;
          using var extracted = _archive.OpenEntrySeekable(candidate, entry.Name, _password);
          using var staged = File.OpenRead(node.DataPath);
          if (!EqualContent(extracted, staged))
            throw new InvalidDataException("Candidate archive entry data differs from staged content.");
        }
      }

      Action<Stream> writer = _nativeZipModifier is not null ? ApplyNativeDelta : Rebuild;
      if (_imagePath is not null)
        _baseline = ArchiveAtomicPublication.Publish(_imagePath, _baseline!, writer, Validate);
      else
        WholeImageRebuildCommitter.Replace(_image!, writer, Validate);

      _published.Clear();
      foreach (var item in entries) {
        var name = PathOf(item);
        _published.Add(name, (item.Kind == FilesystemNodeKind.Directory ? name + "/" : name,
          item.Kind == FilesystemNodeKind.Directory,
          item.DataPath is null ? null : ArchiveAtomicPublication.HashFile(item.DataPath)));
      }
      _dirty = false;
    }
  }

  public IFilesystemTransaction BeginTransaction()
    => throw new NotSupportedException("Archive rebuilds publish at Flush.");

  public void Dispose() {
    Exception? failed = null;
    lock (_gate) {
      if (_disposed) return;
      try {
        Flush();
      } catch (Exception error) {
        // Never discard staged data when publication fails. Keeping the
        // directory allows manual recovery without losing pending edits.
        failed = error;
      } finally {
        _disposed = true;
      }
    }
    try {
      _mountLock?.Dispose();
    } finally {
      if (failed is null) Directory.Delete(_stagePath, recursive: true);
    }
    if (failed is not null)
      System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed).Throw();
  }

  private void AddEntry(Stream archiveStream, ArchiveEntryInfo entry) {
    if (entry.IsSymlink || entry.LinkTarget is not null || entry.IsEncrypted)
      throw new NotSupportedException("Cannot safely stage encrypted or linked entries.");
    var path = entry.Name.Replace('\\', '/').Trim('/');
    var parts = path.Split('/');
    var parent = _root;
    foreach (var part in parts[..^1]) {
      var found = _children[parent].GetValueOrDefault(part);
      if (found is null) {
        found = Insert(parent, part, FilesystemNodeKind.Directory);
        _children.Add(found.Id, NewChildren());
      } else if (found.Kind != FilesystemNodeKind.Directory)
        throw new InvalidDataException("Archive path crosses a regular file.");
      parent = found.Id;
    }
    var leaf = parts[^1];
    Node node;
    if (entry.IsDirectory && _children[parent].TryGetValue(leaf, out var existing) &&
        existing.Kind == FilesystemNodeKind.Directory)
      node = existing;
    else {
      node = Insert(parent, leaf, entry.IsDirectory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile);
      if (entry.IsDirectory) _children.Add(node.Id, NewChildren());
    }

    if (entry.LastModified is { } modified)
      _metadata[path] = new(LastWriteTimeUtc: new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero));
    if (entry.IsDirectory) return;

    node.DataPath = NewDataPath();
    archiveStream.Position = 0;
    using var extracted = _archive.OpenEntrySeekable(archiveStream, entry.Name, _password);
    using var staged = new FileStream(node.DataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024);
    extracted.CopyTo(staged);
    if (staged.Length != entry.OriginalSize)
      throw new InvalidDataException($"Archive entry '{entry.Name}' extracted a different byte count.");
  }

  private Stream OpenSource()
    => _imagePath is not null ? File.OpenRead(_imagePath) : new NonOwningStream(_image!);

  private Node Insert(FilesystemNodeId parentId, string name, FilesystemNodeKind kind) {
    CheckComponent(name);
    var parent = RequireDirectory(parentId);
    if (_children[parent.Id].ContainsKey(name))
      throw new IOException("Filesystem entry already exists.");
    var node = new Node { Id = new(++_nextId, 1), Parent = parent.Id, Name = name, Kind = kind };
    _nodes.Add(node.Id, node);
    _children[parent.Id].Add(name, node);
    return node;
  }

  private void Unlink(Node node) {
    _children[node.Parent].Remove(node.Name);
    node.Linked = false;
    _dirty = true;
  }

  private Node FindPath(string path) {
    var node = RequireNode(_root);
    foreach (var component in path.Split('/')) {
      if (!_children[node.Id].TryGetValue(component, out var child))
        throw new FileNotFoundException("Archive entry missing from staged namespace.", path);
      node = child;
    }
    return node;
  }

  private Node Child(FilesystemNodeId parent, string name) {
    var directory = RequireDirectory(parent);
    return _children[directory.Id].TryGetValue(name, out var node)
      ? node : throw new FileNotFoundException("Archive entry does not exist.", name);
  }

  private Node RequireNode(FilesystemNodeId id)
    => _nodes.TryGetValue(id, out var node) ? node : throw new FileNotFoundException("Missing node.");
  private Node RequireDirectory(FilesystemNodeId id) {
    var node = RequireNode(id);
    return node.Kind == FilesystemNodeKind.Directory ? node : throw new DirectoryNotFoundException();
  }

  private Node[] LinkedEntries()
    => _nodes.Values.Where(n => n.Linked && n.Id != _root)
      .OrderBy(PathOf, StringComparer.Ordinal).ToArray();

  private string PathOf(Node node) {
    var parts = new List<string>();
    for (var p = node; p.Id != _root; p = RequireNode(p.Parent)) parts.Add(p.Name);
    parts.Reverse();
    return string.Join('/', parts);
  }

  private string NewDataPath() => Path.Combine(_stagePath, Guid.NewGuid().ToString("N") + ".data");
  private Dictionary<string, Node> NewChildren() => new(_names);
  private void Require(FilesystemDriverCapabilities flag) {
    if ((Profile.Capabilities & flag) == 0) throw new NotSupportedException("The mounted archive lacks the requested capability.");
  }
  private void RequireActive() => ObjectDisposedException.ThrowIf(_disposed, this);
  private static int Depth(string path) => path.Count(static ch => ch == '/' || ch == '\\');
  private static void CheckComponent(string name) {
    if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
        name.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
      throw new ArgumentException("Unsafe archive pathname.", nameof(name));
  }

  private static bool EqualContent(Stream left, Stream right) {
    var a = new byte[128 * 1024];
    var b = new byte[a.Length];
    while (true) {
      var n = left.Read(a);
      if (n == 0) return right.ReadByte() == -1;
      right.ReadExactly(b.AsSpan(0, n));
      if (!a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n))) return false;
    }
  }

  private sealed class Handle(FileBackedArchiveFilesystemSession owner, Node node, FileAccess access) : IFilesystemFileHandle {
    private bool _closed;
    public FilesystemNodeId NodeId => node.Id;
    public long Length {
      get { lock (owner._gate) { Check(); return new FileInfo(node.DataPath!).Length; } }
    }
    public int Read(long offset, Span<byte> destination) {
      lock (owner._gate) {
        Check();
        if ((access & FileAccess.Read) == 0) throw new UnauthorizedAccessException("Handle has no read access.");
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        using var f = File.OpenRead(node.DataPath!);
        if (offset >= f.Length) return 0;
        f.Position = offset;
        return f.Read(destination);
      }
    }
    public void Write(long offset, ReadOnlySpan<byte> data) {
      lock (owner._gate) {
        Check();
        if ((access & FileAccess.Write) == 0) throw new UnauthorizedAccessException("Handle has no write access.");
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        _ = checked(offset + data.Length);
        using var f = new FileStream(node.DataPath!, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        f.Position = offset;
        f.Write(data);
        if (!data.IsEmpty) owner._dirty = true;
      }
    }
    public void SetLength(long length) {
      lock (owner._gate) {
        Check();
        if ((access & FileAccess.Write) == 0) throw new UnauthorizedAccessException("Handle has no write access.");
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        using var f = new FileStream(node.DataPath!, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        if (f.Length == length) return;
        f.SetLength(length);
        owner._dirty = true;
      }
    }
    public void Flush() { Check(); owner.Flush(); }
    public void Dispose() => _closed = true;
    private void Check() {
      ObjectDisposedException.ThrowIf(_closed, this);
      owner.RequireActive();
    }
  }

  // The source stream's lifetime is managed by the caller, not by a listing pass.
  private sealed class NonOwningStream(Stream source) : Stream {
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => source.CanSeek;
    public override bool CanWrite => source.CanWrite;
    public override long Length => source.Length;
    public override long Position { get => source.Position; set => source.Position = value; }
    public override void Flush() => source.Flush();
    public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => source.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
    public override void SetLength(long value) => source.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => source.Write(buffer, offset, count);
  }
}

/// <summary>
/// Same-directory copy/flush/journal/rename publication with verified recovery.
/// A pending journal identifies whether the live file is the old or new version.
/// No P/Invoke is used. Directory fsync is unavailable through the managed BCL,
/// so strict power-loss durability cannot be guaranteed on all filesystems.
/// </summary>
internal static class ArchiveAtomicPublication {
  public static byte[] HashFile(string path) {
    using var file = File.OpenRead(path);
    return SHA256.HashData(file);
  }

  public static byte[] Publish(string path, byte[] expectedHash,
      Action<Stream> build, Action<Stream> validate) {
    var directory = Path.GetDirectoryName(path)!;
    var tag = Guid.NewGuid().ToString("N");
    var candidate = Path.Combine(directory, "." + Path.GetFileName(path) + "." + tag + ".candidate");
    var backup = Path.Combine(directory, "." + Path.GetFileName(path) + "." + tag + ".backup");
    var journal = path + ".cwbmount.journal";

    if (File.Exists(journal))
      throw new IOException("Previous archive publication requires recovery.");
    if (!HashFile(path).AsSpan().SequenceEqual(expectedHash))
      throw new IOException("Archive changed outside its mount; refusing to overwrite external changes.");

    var prepared = false;
    try {
      using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.ReadWrite,
        FileShare.None, 128 * 1024, FileOptions.WriteThrough)) {
        build(output);
        output.Flush(flushToDisk: true);
        output.Position = 0;
        validate(output);
        output.Flush(flushToDisk: true);
      }

      var candidateHash = HashFile(candidate);
      using (var source = File.OpenRead(path))
      using (var saved = new FileStream(backup, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 128 * 1024, FileOptions.WriteThrough)) {
        source.CopyTo(saved);
        saved.Flush(flushToDisk: true);
      }
      if (!HashFile(backup).AsSpan().SequenceEqual(expectedHash))
        throw new IOException("Archive backup verification failed.");

      using (var mark = new FileStream(journal, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 4096, FileOptions.WriteThrough)) {
        var record = string.Join("\n", "CWB-ARCHIVE-1", Convert.ToHexString(expectedHash),
          Convert.ToHexString(candidateHash), Path.GetFileName(backup), Path.GetFileName(candidate));
        mark.Write(Encoding.UTF8.GetBytes(record));
        mark.Flush(flushToDisk: true);
      }
      prepared = true;

      if (!HashFile(path).AsSpan().SequenceEqual(expectedHash))
        throw new IOException("Archive changed before atomic publication.");

      // Source and candidate must be on the same filesystem. An existing open
      // handle to 'path' is not held by FileBackedArchiveFilesystemSession.
      File.Move(candidate, path, overwrite: true);

      // The archive has been committed. Cleanup errors must not cause the
      // caller to replay a successful publication; a retained journal is
      // idempotently cleaned on the next open after matching the new hash.
      try {
        File.Delete(backup);
        File.Delete(journal);
      } catch (IOException) {
      } catch (UnauthorizedAccessException) {
      }
      return candidateHash;
    } finally {
      if (!prepared) {
        if (File.Exists(candidate)) File.Delete(candidate);
        if (File.Exists(backup)) File.Delete(backup);
      }
    }
  }

  public static void Recover(string path) {
    var journal = path + ".cwbmount.journal";
    if (!File.Exists(journal)) return;
    var parts = File.ReadAllLines(journal);
    if (parts.Length != 5 || parts[0] != "CWB-ARCHIVE-1")
      throw new InvalidDataException("Malformed pending archive publication journal.");
    var oldHash = Convert.FromHexString(parts[1]);
    var newHash = Convert.FromHexString(parts[2]);
    if (parts[3] != Path.GetFileName(parts[3]) || parts[4] != Path.GetFileName(parts[4]))
      throw new InvalidDataException("Publication journal has unsafe paths.");

    var backup = Path.Combine(Path.GetDirectoryName(path)!, parts[3]);
    var candidate = Path.Combine(Path.GetDirectoryName(path)!, parts[4]);
    var actual = File.Exists(path) ? HashFile(path) : Array.Empty<byte>();
    if (!actual.AsSpan().SequenceEqual(oldHash) && !actual.AsSpan().SequenceEqual(newHash)) {
      if (!File.Exists(backup) || !HashFile(backup).AsSpan().SequenceEqual(oldHash))
        throw new IOException("Archive differs from both journal states and has no verified backup.");
      File.Move(backup, path, overwrite: true);
    }
    if (File.Exists(candidate)) File.Delete(candidate);
    if (File.Exists(backup)) File.Delete(backup);
    File.Delete(journal);
  }
}
