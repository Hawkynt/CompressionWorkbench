using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// Read-only synthetic root that combines several independently parsed
/// partition filesystems. Each child keeps its own native node identities; the
/// composite assigns a stable session-local id so equal inode numbers in two
/// partitions can never alias.
/// </summary>
internal sealed class PartitionedFilesystemSession : IFilesystemSession {
  internal sealed record PartitionMount(string Name, IFilesystemSession Session);
  private sealed record NodeReference(int PartitionIndex, FilesystemNodeId InnerNodeId);

  private sealed class RemappedFileHandle(
      FilesystemNodeId nodeId,
      IFilesystemFileHandle inner) : IFilesystemFileHandle {
    private readonly IFilesystemFileHandle _inner = inner;
    public FilesystemNodeId NodeId { get; } = nodeId;
    public long Length => this._inner.Length;
    public int Read(long offset, Span<byte> destination) => this._inner.Read(offset, destination);
    public void Write(long offset, ReadOnlySpan<byte> source) => this._inner.Write(offset, source);
    public void SetLength(long length) => this._inner.SetLength(length);
    public void Flush() => this._inner.Flush();
    public void Dispose() => this._inner.Dispose();
  }

  private readonly PartitionMount[] _partitions;
  private readonly IDisposable _owner;
  private readonly object _gate = new();
  private readonly Dictionary<(int Partition, FilesystemNodeId Inner), FilesystemNodeId> _outward = [];
  private readonly Dictionary<FilesystemNodeId, NodeReference> _inward = [];
  private ulong _nextNodeId = 2;
  private bool _disposed;

  public PartitionedFilesystemSession(
      string outerFormatId,
      string scheme,
      IEnumerable<PartitionMount> partitions,
      IDisposable owner,
      IEnumerable<string>? limitations = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(outerFormatId);
    ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
    ArgumentNullException.ThrowIfNull(partitions);
    this._owner = owner ?? throw new ArgumentNullException(nameof(owner));
    this._partitions = partitions.ToArray();
    if (this._partitions.Length == 0)
      throw new ArgumentException("A partitioned mount requires at least one child filesystem.", nameof(partitions));
    if (this._partitions.Any(static partition => !partition.Session.Profile.CanMount))
      throw new ArgumentException("Every child partition must expose a mountable filesystem profile.", nameof(partitions));

    var optional = FilesystemDriverCapabilities.CasePreservingNames;
    if (this._partitions.All(static partition =>
          (partition.Session.Profile.Capabilities & FilesystemDriverCapabilities.SymbolicLinks) != 0))
      optional |= FilesystemDriverCapabilities.SymbolicLinks;

    var profileLimitations = new List<string> {
      $"Synthetic {scheme} partition root; each top-level directory is an independently parsed filesystem.",
      "The composite root is read-only even when a child filesystem has mount-grade writes; select a single partition for writable mounting.",
    };
    if (limitations is not null)
      profileLimitations.AddRange(limitations);
    profileLimitations.AddRange(this._partitions.SelectMany(static partition => partition.Session.Profile.Limitations));

    Profile = new FilesystemDriverProfile(
      outerFormatId,
      $"{scheme} partitioned disk ({this._partitions.Length} filesystems)",
      FilesystemMountCapabilityResolver.CoreReadCapabilities | optional,
      FilesystemMutationModel.None,
      CanMount: true,
      CanMountWritable: false,
      profileLimitations.Distinct(StringComparer.Ordinal).ToArray());

    for (var i = 0; i < this._partitions.Length; ++i)
      _ = this.Map(i, this._partitions[i].Session.RootNodeId);
  }

  public FilesystemDriverProfile Profile { get; }
  public FilesystemNodeId RootNodeId { get; } = new(1, 1);

  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) {
    this.ThrowIfDisposed();
    if (nodeId == this.RootNodeId)
      return new(nodeId, FilesystemNodeKind.Directory, 0, 0);

    var reference = this.Require(nodeId);
    var inner = this._partitions[reference.PartitionIndex].Session.Stat(reference.InnerNodeId);
    return inner with { NodeId = nodeId };
  }

  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) {
    ArgumentNullException.ThrowIfNull(name);
    this.ThrowIfDisposed();

    if (parentDirectory == this.RootNodeId) {
      var exact = Array.FindIndex(this._partitions,
        partition => string.Equals(partition.Name, name, StringComparison.Ordinal));
      if (exact >= 0)
        return this.Map(exact, this._partitions[exact].Session.RootNodeId);

      var folded = Enumerable.Range(0, this._partitions.Length)
        .Where(index => string.Equals(this._partitions[index].Name, name, StringComparison.OrdinalIgnoreCase))
        .ToArray();
      return folded.Length == 1
        ? this.Map(folded[0], this._partitions[folded[0]].Session.RootNodeId)
        : null;
    }

    var reference = this.Require(parentDirectory);
    var child = this._partitions[reference.PartitionIndex].Session.Lookup(reference.InnerNodeId, name);
    return child is null ? null : this.Map(reference.PartitionIndex, child.Value);
  }

  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) {
    this.ThrowIfDisposed();
    if (directory == this.RootNodeId)
      return this._partitions
        .Select((partition, index) => new FilesystemDirectoryEntry(
          partition.Name,
          this.Map(index, partition.Session.RootNodeId),
          FilesystemNodeKind.Directory))
        .ToArray();

    var reference = this.Require(directory);
    return this._partitions[reference.PartitionIndex].Session.Enumerate(reference.InnerNodeId)
      .Select(entry => entry with { NodeId = this.Map(reference.PartitionIndex, entry.NodeId) })
      .ToArray();
  }

  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) {
    this.ThrowIfDisposed();
    var reference = this.Require(nodeId);
    var inner = this._partitions[reference.PartitionIndex].Session.OpenFile(reference.InnerNodeId, access);
    return new RemappedFileHandle(nodeId, inner);
  }

  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) => throw ReadOnly();
  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) => throw ReadOnly();
  public void DeleteFile(FilesystemNodeId parentDirectory, string name) => throw ReadOnly();
  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) => throw ReadOnly();
  public void Rename(FilesystemNodeId oldParent, string oldName, FilesystemNodeId newParent, string newName, bool replace) => throw ReadOnly();
  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName) => throw ReadOnly();
  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target) => throw ReadOnly();

  public string ReadSymbolicLink(FilesystemNodeId nodeId) {
    this.ThrowIfDisposed();
    var reference = this.Require(nodeId);
    return this._partitions[reference.PartitionIndex].Session.ReadSymbolicLink(reference.InnerNodeId);
  }

  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch) => throw ReadOnly();

  public void Flush() {
    this.ThrowIfDisposed();
    foreach (var partition in this._partitions)
      partition.Session.Flush();
  }

  public IFilesystemTransaction BeginTransaction()
    => throw new NotSupportedException("The synthetic partition root is read-only and has no cross-filesystem transaction.");

  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    Exception? first = null;
    foreach (var partition in this._partitions) {
      try {
        partition.Session.Dispose();
      } catch (Exception ex) when (first is not null) {
        _ = ex;
      } catch (Exception ex) {
        first = ex;
      }
    }

    try {
      this._owner.Dispose();
    } catch (Exception ex) when (first is not null) {
      _ = ex;
    } catch (Exception ex) {
      first = ex;
    }

    if (first is not null)
      throw first;
  }

  private FilesystemNodeId Map(int partitionIndex, FilesystemNodeId innerNodeId) {
    lock (this._gate) {
      var key = (partitionIndex, innerNodeId);
      if (this._outward.TryGetValue(key, out var existing))
        return existing;

      var mapped = new FilesystemNodeId(this._nextNodeId++, checked((ulong)(partitionIndex + 1)));
      this._outward.Add(key, mapped);
      this._inward.Add(mapped, new NodeReference(partitionIndex, innerNodeId));
      return mapped;
    }
  }

  private NodeReference Require(FilesystemNodeId nodeId) {
    lock (this._gate)
      return this._inward.TryGetValue(nodeId, out var reference)
        ? reference
        : throw new FileNotFoundException($"Composite filesystem node {nodeId.Value}:{nodeId.Generation} does not exist.");
  }

  private static NotSupportedException ReadOnly()
    => new("The synthetic partition-root namespace is read-only.");

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(this._disposed, this);
}
