using Compression.Registry;

namespace FileSystem.Zfs;

/// <summary>
/// Native read-only ZFS driver over <see cref="ZfsReader"/>: one device image, or the
/// devices of a mirror/RAID-Z/striped pool as a <see cref="FilesystemStreamSet"/>. Files
/// are dnodes of their dataset, so hard links share one node; every block read is
/// checksum-verified and decompressed by the reader.
/// </summary>
public sealed class ZfsFilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider,
  IMultiStreamFilesystemDriverProvider {

  /// <summary>Gets the format id.</summary>
  public string FormatId => "Zfs";

  /// <summary>Probes a single device image.</summary>
  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    return Probe([image]);
  }

  /// <summary>Probes the devices of one pool.</summary>
  public FilesystemDriverProfile ProbeFilesystem(FilesystemStreamSet sources) {
    ArgumentNullException.ThrowIfNull(sources);
    return Probe(sources.Select(s => s.Stream).ToArray());
  }

  private FilesystemDriverProfile Probe(IReadOnlyList<Stream> devices) {
    var positions = devices.Select(d => d.CanSeek ? d.Position : 0).ToArray();
    try {
      using var reader = new ZfsReader(devices, leaveOpen: true);
      var limitations = new List<string> {
        $"Pool '{reader.PoolName}' read at txg {reader.Txg} (SPA version {reader.PoolVersion}); vdevs: {string.Join("; ", reader.Topology)}.",
        "Read-only: OpenZFS is the only writer this driver accepts pools from.",
      };
      limitations.AddRange(reader.Datasets.Where(d => d.Refusal != null).Select(d => $"{d.Name}: {d.Refusal}."));
      limitations.AddRange(reader.Warnings);
      return new FilesystemDriverProfile(
        this.FormatId,
        "OpenZFS pool reader",
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.HardLinks |
        FilesystemDriverCapabilities.SymbolicLinks |
        FilesystemDriverCapabilities.CaseSensitiveNames |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: false,
        limitations);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return new FilesystemDriverProfile(
        this.FormatId,
        "unreadable ZFS pool",
        FilesystemDriverCapabilities.None,
        FilesystemMutationModel.None,
        CanMount: false,
        CanMountWritable: false,
        [FirstLine(e.Message)]);
    } finally {
      for (var i = 0; i < devices.Count; ++i)
        if (devices[i].CanSeek) devices[i].Position = positions[i];
    }
  }

  /// <summary>Opens a single device image read-only.</summary>
  public IFilesystemSession OpenFilesystem(Stream image, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    return Open([image], options);
  }

  /// <summary>Opens the devices of one pool read-only.</summary>
  public IFilesystemSession OpenFilesystem(FilesystemStreamSet sources, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(sources);
    ArgumentNullException.ThrowIfNull(options);
    return Open(sources.Select(s => s.Stream).ToArray(), options);
  }

  private ZfsReadOnlyFilesystemSession Open(Stream[] devices, FilesystemOpenOptions options) {
    if (!options.ReadOnly)
      throw new NotSupportedException("ZFS is read-only here: no writer has been proven against zpool import and scrub.");
    var profile = this.Probe(devices);
    if (!profile.CanMount)
      throw new InvalidDataException("ZFS pool cannot be mounted: " + string.Join("; ", profile.Limitations));
    return new ZfsReadOnlyFilesystemSession(devices, profile, options.LeaveOpen);
  }

  /// <summary>Probes a block device.</summary>
  public FilesystemDriverProfile ProbeFilesystem(IRandomAccessBlockDevice device) {
    ArgumentNullException.ThrowIfNull(device);
    using var stream = new BlockDeviceStream(device, leaveOpen: true);
    return this.ProbeFilesystem(stream);
  }

  /// <summary>Opens a block device read-only.</summary>
  public IFilesystemSession OpenFilesystem(IRandomAccessBlockDevice device, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(device);
    ArgumentNullException.ThrowIfNull(options);
    var stream = new BlockDeviceStream(device, leaveOpen: false);
    try {
      return this.OpenFilesystem(stream, options with { LeaveOpen = false });
    } catch {
      stream.Dispose();
      throw;
    }
  }

  /// <summary>Describes which driver layers are available.</summary>
  public FilesystemDriverReadinessReport DescribeFilesystemDriverReadiness(Stream image, FilesystemDriverTarget target) {
    var profile = this.ProbeFilesystem(image);
    var readRequired =
      FilesystemDriverReadinessLayer.ImageValidation |
      FilesystemDriverReadinessLayer.Namespace |
      FilesystemDriverReadinessLayer.SessionStableNodeIds |
      FilesystemDriverReadinessLayer.NativeStableNodeIds |
      FilesystemDriverReadinessLayer.ReadData |
      FilesystemDriverReadinessLayer.RandomAccessRead;
    var writeRequired = readRequired |
      FilesystemDriverReadinessLayer.AllocationMap |
      FilesystemDriverReadinessLayer.WriteData |
      FilesystemDriverReadinessLayer.Truncate |
      FilesystemDriverReadinessLayer.NamespaceMutation |
      FilesystemDriverReadinessLayer.MetadataMutation |
      FilesystemDriverReadinessLayer.Links |
      FilesystemDriverReadinessLayer.Flush |
      FilesystemDriverReadinessLayer.DurabilityModel |
      FilesystemDriverReadinessLayer.Recovery |
      FilesystemDriverReadinessLayer.Concurrency;
    var available = profile.CanMount
      ? readRequired | FilesystemDriverReadinessLayer.Links | FilesystemDriverReadinessLayer.ValidationCorpus
      : FilesystemDriverReadinessLayer.None;
    var blockers = new List<string>(profile.Limitations);
    if (target == FilesystemDriverTarget.ReadWrite)
      blockers.Add("Writing needs metaslab/space-map allocation, copy-on-write DSL/ZAP/dnode updates, new uberblocks and ZIL semantics, proven by zpool import and a clean scrub.");
    var required = target == FilesystemDriverTarget.ReadOnly ? readRequired : writeRequired;
    return new FilesystemDriverReadinessReport(
      this.FormatId,
      target,
      available,
      required,
      profile.CanMount && (available & required) == required,
      UsesNativeProvider: true,
      blockers.Distinct(StringComparer.Ordinal).ToArray());
  }

  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }
}

internal sealed class ZfsReadOnlyFilesystemSession : IFilesystemSession {
  private readonly Stream[] _devices;
  private readonly bool _leaveOpen;
  private readonly object _ioGate = new();
  private readonly ZfsReader _reader;
  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private bool _disposed;

  public ZfsReadOnlyFilesystemSession(Stream[] devices, FilesystemDriverProfile profile, bool leaveOpen) {
    foreach (var device in devices)
      if (!device.CanRead || !device.CanSeek)
        throw new ArgumentException("ZFS mounted reads require readable, seekable images.", nameof(devices));
    this._devices = devices;
    this._leaveOpen = leaveOpen;
    this._reader = new ZfsReader(devices, leaveOpen: true);
    var rootId = new FilesystemNodeId(0, 0);
    var (nodes, links) = this.BuildNamespace(rootId);
    this._namespace = new ReadOnlyFilesystemSnapshotSession(profile, rootId, nodes, links);
  }

  public FilesystemDriverProfile Profile => this._namespace.Profile;
  public FilesystemNodeId RootNodeId => this._namespace.RootNodeId;
  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) => this._namespace.Stat(nodeId);
  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) => this._namespace.Lookup(parentDirectory, name);
  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) => this._namespace.Enumerate(directory);
  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) => this._namespace.OpenFile(nodeId, access);
  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) => this._namespace.CreateFile(parentDirectory, name);
  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) => this._namespace.CreateDirectory(parentDirectory, name);
  public void DeleteFile(FilesystemNodeId parentDirectory, string name) => this._namespace.DeleteFile(parentDirectory, name);
  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) => this._namespace.RemoveDirectory(parentDirectory, name);
  public void Rename(FilesystemNodeId oldParent, string oldName, FilesystemNodeId newParent, string newName, bool replace) => this._namespace.Rename(oldParent, oldName, newParent, newName, replace);
  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName) => this._namespace.CreateHardLink(existingNode, newParent, newName);
  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target) => this._namespace.CreateSymbolicLink(parentDirectory, name, target);
  public string ReadSymbolicLink(FilesystemNodeId nodeId) => this._namespace.ReadSymbolicLink(nodeId);
  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch) => this._namespace.SetMetadata(nodeId, patch);
  public void Flush() => this._namespace.Flush();
  public IFilesystemTransaction BeginTransaction() => this._namespace.BeginTransaction();

  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    this._namespace.Dispose();
    this._reader.Dispose();
    if (!this._leaveOpen)
      foreach (var device in this._devices) device.Dispose();
  }

  private (FilesystemSnapshotNode[] Nodes, FilesystemSnapshotDirectoryEntry[] Links) BuildNamespace(FilesystemNodeId rootId) {
    // A node is (object, object set): objects are numbered per dataset. Directories the
    // reader synthesised (dataset roots it had no object for, .zfs) get their own space.
    var objectSets = new Dictionary<ZfsObjectSet, ulong>(ReferenceEqualityComparer.Instance);
    ulong synthetic = 0;
    FilesystemNodeId IdOf(ZfsEntry e) {
      if (e.ObjectSet is not { } os) return new(++synthetic, ulong.MaxValue);
      if (!objectSets.TryGetValue(os, out var key)) objectSets[os] = key = (ulong)objectSets.Count + 1;
      return new(e.ObjectId, key);
    }

    var entries = this._reader.Entries;
    var ids = entries.Select(IdOf).ToArray();
    var aliases = ids.GroupBy(i => i).ToDictionary(g => g.Key, g => (uint)g.Count());
    var nodes = new Dictionary<FilesystemNodeId, FilesystemSnapshotNode> {
      [rootId] = new(rootId, default, string.Empty, FilesystemNodeKind.Directory, 0, 0),
    };
    var links = new List<FilesystemSnapshotDirectoryEntry>(entries.Count);
    var pathToNode = new Dictionary<string, FilesystemNodeId>(StringComparer.Ordinal) { [string.Empty] = rootId };

    for (var i = 0; i < entries.Count; ++i) {
      var entry = entries[i];
      var slash = entry.Name.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : entry.Name[..slash];
      var name = slash < 0 ? entry.Name : entry.Name[(slash + 1)..];
      if (!pathToNode.TryGetValue(parentPath, out var parent))
        throw new InvalidDataException($"ZFS entry '{entry.Name}' has no parent '{parentPath}'.");
      var nodeId = ids[i];
      var kind = entry.IsDirectory ? FilesystemNodeKind.Directory
        : entry.IsSymlink ? FilesystemNodeKind.SymbolicLink
        : FilesystemNodeKind.RegularFile;

      if (!nodes.ContainsKey(nodeId)) {
        Func<IFilesystemFileHandle>? open = null;
        if (kind == FilesystemNodeKind.RegularFile) {
          var captured = entry;
          open = () => SpoolingReadOnlyFileHandle.Create(nodeId, captured.Size, output => {
            long written;
            lock (this._ioGate) written = this._reader.ExtractTo(captured, output);
            if (written != captured.Size)
              throw new InvalidDataException($"ZFS: '{captured.Name}' yielded {written:N0} of {captured.Size:N0} bytes.");
          });
        }
        nodes[nodeId] = new FilesystemSnapshotNode(
          nodeId, parent, name, kind,
          kind == FilesystemNodeKind.Directory ? 0 : entry.Size, 0,
          LinkCount: aliases[nodeId],
          NativeAttributes: entry.Mode,
          Modified: entry.LastModified is { } m ? new DateTimeOffset(DateTime.SpecifyKind(m, DateTimeKind.Utc)) : null,
          SymbolicLinkTarget: entry.LinkTarget,
          OpenReadHandle: open);
      }

      links.Add(new FilesystemSnapshotDirectoryEntry(parent, name, nodeId));
      if (kind == FilesystemNodeKind.Directory) pathToNode[entry.Name] = nodeId;
    }

    return (nodes.Values.ToArray(), links.ToArray());
  }
}
