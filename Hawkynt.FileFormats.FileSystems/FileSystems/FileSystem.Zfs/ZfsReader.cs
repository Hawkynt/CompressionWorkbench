using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Zfs;

/// <summary>A dataset the reader found, and why it was not listed if it was not.</summary>
/// <param name="Name">Full dataset name (<c>pool/fs</c>, <c>pool/fs@snap</c>).</param>
/// <param name="Path">Where its contents appear in <see cref="ZfsReader.Entries"/>.</param>
/// <param name="Kind"><c>filesystem</c>, <c>snapshot</c> or <c>volume</c>.</param>
/// <param name="Refusal">Null when listed; otherwise the reason its contents are absent.</param>
public sealed record ZfsDataset(string Name, string Path, string Kind, string? Refusal);

/// <summary>
/// Reads OpenZFS storage pools: one or more vdev images, every dataset, snapshot and file
/// in them, with all of ZFS's block compressions, gang and embedded blocks, holes, ditto
/// copies, mirrors and RAID-Z. Written from <c>docs/ZFS-ON-DISK.md</c>.
/// </summary>
/// <remarks>
/// Read-only by design. Natively encrypted datasets, volumes (zvols) and big-endian pools
/// are reported and refused rather than guessed at. Checksums are verified for Fletcher-2,
/// Fletcher-4, SHA-256 and SHA-512; the salted ones (Skein, Edon-R, BLAKE3) are read
/// unverified and named in <see cref="UnverifiedChecksums"/>.
/// </remarks>
public sealed class ZfsReader : IDisposable {
  private const int MaxDepth = 256;

  private readonly ZfsPool _pool;
  private readonly List<ZfsEntry> _entries = [];
  private readonly Dictionary<string, int> _byPath = new(StringComparer.Ordinal);
  private readonly List<ZfsDataset> _datasets = [];
  private readonly List<string> _warnings = [];

  /// <summary>Opens the pool on a single device image.</summary>
  public ZfsReader(Stream stream, bool leaveOpen = true) : this([stream], leaveOpen) { }

  /// <summary>Opens the pool spread over <paramref name="devices"/> (mirror, RAID-Z or striped vdevs).</summary>
  /// <exception cref="InvalidDataException">No label, uberblock or readable MOS was found.</exception>
  /// <exception cref="NotSupportedException">The pool was written big-endian.</exception>
  public ZfsReader(IReadOnlyList<Stream> devices, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(devices);
    foreach (var device in devices) {
      ArgumentNullException.ThrowIfNull(device);
      if (device.CanSeek) device.Position = 0;
    }
    this._pool = new ZfsPool(devices, leaveOpen);
    try {
      this.Walk();
    } catch {
      this._pool.Dispose();
      throw;
    }
  }

  /// <summary>Pool name from the vdev labels.</summary>
  public string? PoolName => this._pool.Name;
  /// <summary>Pool GUID.</summary>
  public ulong PoolGuid => this._pool.Guid;
  /// <summary>SPA version of the active uberblock (5000 for feature-flag pools).</summary>
  public ulong PoolVersion => this._pool.Version;
  /// <summary>Transaction group of the uberblock the namespace was read from.</summary>
  public ulong Txg => this._pool.Txg;
  /// <summary>Length of the first device image.</summary>
  public long Length => this._pool.FirstDeviceLength;
  /// <summary>Every file, directory and symbolic link, parents before children.</summary>
  public IReadOnlyList<ZfsEntry> Entries => this._entries;
  /// <summary>Every dataset and snapshot found, listed or refused.</summary>
  public IReadOnlyList<ZfsDataset> Datasets => this._datasets;
  /// <summary>Parts of the pool that could not be read; the rest is still listed.</summary>
  public IReadOnlyList<string> Warnings => this._warnings;
  /// <summary>Checksum algorithms met that the reader does not verify.</summary>
  public IReadOnlyCollection<string> UnverifiedChecksums => this._pool.UnverifiedChecksums;
  internal ZfsReadStatistics Statistics => this._pool.Statistics;

  /// <summary>Top-level vdevs as reconstructed from the labels.</summary>
  public IReadOnlyList<string> Topology => this._pool.TopLevelVdevs.OrderBy(v => v.Key).Select(v => v.Value.Describe()).ToArray();

  private void Walk() {
    var mos = this._pool.Mos;
    var directory = mos.Zap(1, "MOS object directory");
    var root = directory.FirstOrDefault(e => e.Name == "root_dataset")
      ?? throw new InvalidDataException("ZFS: object directory has no root_dataset.");
    this.WalkDslDirectory(root.Value, this.PoolName ?? "pool", "", 0);
  }

  private void WalkDslDirectory(ulong objectId, string name, string path, int depth) {
    if (depth > MaxDepth) throw new InvalidDataException("ZFS: dataset tree too deep.");
    var mos = this._pool.Mos;
    var dir = mos.RequireDnode(objectId, $"DSL directory of {name}");
    if (dir.Bonus.Length < 40) throw new InvalidDataException($"ZFS: DSL directory of {name} has no dsl_dir_phys.");
    var head = BinaryPrimitives.ReadUInt64LittleEndian(dir.Bonus.AsSpan(8));
    var children = BinaryPrimitives.ReadUInt64LittleEndian(dir.Bonus.AsSpan(32));

    if (path.Length > 0) this.AddDirectory(path, name);
    if (head != 0) this.Guarded(name, () => this.LoadDataset(head, name, path, isSnapshot: false));
    if (children == 0) return;

    List<ZfsZapEntry> childEntries;
    try {
      childEntries = mos.Zap(children, $"child map of {name}");
    } catch (Exception e) when (IsReadFailure(e)) {
      this._warnings.Add($"{name}: child datasets unreadable: {e.Message}");
      return;
    }
    foreach (var child in childEntries.OrderBy(c => c.Name, StringComparer.Ordinal)) {
      if (child.Name.StartsWith('$') || child.Name.Contains('%')) continue;
      var childPath = path.Length == 0 ? child.Name : path + "/" + child.Name;
      this.Guarded(name + "/" + child.Name,
        () => this.WalkDslDirectory(child.Value, name + "/" + child.Name, childPath, depth + 1));
    }
  }

  private void LoadDataset(ulong objectId, string name, string path, bool isSnapshot) {
    var mos = this._pool.Mos;
    var ds = mos.RequireDnode(objectId, $"DSL dataset {name}");
    if (ds.Bonus.Length < 256) throw new InvalidDataException($"ZFS: DSL dataset {name} has no dsl_dataset_phys.");
    var snapshots = BinaryPrimitives.ReadUInt64LittleEndian(ds.Bonus.AsSpan(32));
    var bp = ZfsBlockPointer.Read(ds.Bonus.AsSpan(128));
    var kind = isSnapshot ? "snapshot" : "filesystem";

    if (bp.IsHole) {
      this._datasets.Add(new(name, path, kind, "the dataset has no object set yet"));
      return;
    }
    if (bp.IsEncrypted) {
      this._datasets.Add(new(name, path, kind, "native encryption: names and contents are ciphertext and no key is available"));
      return;
    }

    var os = this._pool.ReadObjectSet(bp);
    switch (os.Type) {
      case ZfsConstants.OstZfs:
        this._datasets.Add(new(name, path, kind, null));
        this.WalkZpl(os, name, path);
        break;
      case ZfsConstants.OstZvol:
        this._datasets.Add(new(name, path, "volume", "volumes (zvols) are block devices, not listed as files"));
        return;
      default:
        this._datasets.Add(new(name, path, kind, $"object set type {os.Type} is not a filesystem"));
        return;
    }

    if (isSnapshot || snapshots == 0) return;
    foreach (var snap in mos.Zap(snapshots, $"snapshots of {name}").OrderBy(s => s.Name, StringComparer.Ordinal)) {
      var snapPath = Combine(Combine(Combine(path, ".zfs"), "snapshot"), snap.Name);
      this.Guarded(name + "@" + snap.Name, () => {
        this.AddDirectory(snapPath, name + "@" + snap.Name);
        this.LoadDataset(snap.Value, name + "@" + snap.Name, snapPath, isSnapshot: true);
      });
    }
  }

  private void WalkZpl(ZfsObjectSet os, string dataset, string rootPath) {
    var master = os.Zap(1, $"master node of {dataset}").ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
    if (!master.TryGetValue("ROOT", out var rootObject))
      throw new InvalidDataException($"ZFS: master node of {dataset} has no ROOT.");
    var sa = master.TryGetValue("SA_ATTRS", out var saObject) ? new ZfsSystemAttributes(os, saObject) : null;
    var context = new ZplContext(os, sa, dataset);
    if (rootPath.Length > 0) {
      var rootDnode = os.RequireDnode(rootObject, $"root directory of {dataset}");
      this.AddDirectory(rootPath, dataset, ReadAttributes(context, rootDnode).ModifiedUtc);
    }
    this.WalkDirectory(context, rootObject, rootPath, [rootObject], 0);
  }

  private sealed record ZplContext(ZfsObjectSet ObjectSet, ZfsSystemAttributes? Attributes, string Dataset);

  private void WalkDirectory(ZplContext context, ulong directoryObject, string path, HashSet<ulong> visited, int depth) {
    if (depth > MaxDepth) {
      this._warnings.Add($"{context.Dataset}: '{path}' is nested too deeply; not descended.");
      return;
    }
    var os = context.ObjectSet;
    List<ZfsZapEntry> names;
    try {
      names = os.Zap(directoryObject, $"directory '{path}'");
    } catch (Exception e) when (IsReadFailure(e)) {
      this._warnings.Add($"{context.Dataset}: directory '{path}' unreadable: {e.Message}");
      return;
    }

    foreach (var name in names.OrderBy(n => n.Name, StringComparer.Ordinal)) {
      var objectId = name.Value & ZfsConstants.DirentObjectMask;
      var childPath = Combine(path, name.Name);
      try {
        var dnode = os.Dnode(objectId);
        if (dnode == null) {
          this._warnings.Add($"{context.Dataset}: '{childPath}' names free object {objectId}.");
          continue;
        }
        var attributes = ReadAttributes(context, dnode);
        switch (attributes.Mode & ZfsConstants.ModeTypeMask) {
          case ZfsConstants.ModeDirectory:
            this.AddDirectory(childPath, context.Dataset, attributes.ModifiedUtc, attributes.Mode);
            if (visited.Add(objectId)) {
              this.WalkDirectory(context, objectId, childPath, visited, depth + 1);
              visited.Remove(objectId);
            } else
              this._warnings.Add($"{context.Dataset}: '{childPath}' loops back to an ancestor.");
            break;
          case ZfsConstants.ModeSymlink: {
            var target = this.ReadSymlink(context, dnode, attributes);
            this.Add(new ZfsEntry {
              Name = childPath, Size = Encoding.UTF8.GetByteCount(target), LinkTarget = target,
              LastModified = attributes.ModifiedUtc, Mode = (uint)attributes.Mode, Dataset = context.Dataset,
              ObjectId = objectId, ObjectSet = os, Dnode = dnode,
            });
            break;
          }
          case ZfsConstants.ModeRegular:
            this.Add(new ZfsEntry {
              Name = childPath, Size = (long)attributes.Size, LastModified = attributes.ModifiedUtc,
              Mode = (uint)attributes.Mode, Dataset = context.Dataset,
              ObjectId = objectId, ObjectSet = os, Dnode = dnode,
            });
            break;
          default:
            // Devices, FIFOs and sockets have no contents to extract.
            break;
        }
      } catch (Exception e) when (IsReadFailure(e)) {
        this._warnings.Add($"{context.Dataset}: '{childPath}' unreadable: {e.Message}");
      }
    }
  }

  private readonly record struct Attributes(ulong Mode, ulong Size, DateTime? ModifiedUtc, byte[]? Symlink);

  private static Attributes ReadAttributes(ZplContext context, ZfsDnode dnode) {
    if (dnode.BonusType == ZfsConstants.OtSa) {
      if (context.Attributes == null)
        throw new InvalidDataException("ZFS: object carries system attributes but the dataset has no SA tables.");
      var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
      context.Attributes.Parse(dnode.Bonus, values);
      if (dnode.Spill is { } spill)
        context.Attributes.Parse(context.ObjectSet.Pool.ReadBlock(spill), values);
      return new(
        U64(values, "ZPL_MODE"),
        U64(values, "ZPL_SIZE"),
        values.TryGetValue("ZPL_MTIME", out var mtime) && mtime.Length >= 16 ? Time(mtime) : null,
        values.GetValueOrDefault("ZPL_SYMLINK"));
    }

    if (dnode.BonusType == ZfsConstants.OtZnode && dnode.Bonus.Length >= 88) {
      var bonus = dnode.Bonus;
      return new(
        BinaryPrimitives.ReadUInt64LittleEndian(bonus.AsSpan(72)),
        BinaryPrimitives.ReadUInt64LittleEndian(bonus.AsSpan(80)),
        Time(bonus.AsSpan(16, 16)),
        null);
    }

    throw new InvalidDataException($"ZFS: object has bonus type {dnode.BonusType}, not a znode.");
  }

  private string ReadSymlink(ZplContext context, ZfsDnode dnode, Attributes attributes) {
    if (attributes.Symlink is { } stored)
      return Encoding.UTF8.GetString(stored);
    // Version 4 and older: behind the znode in the bonus when it fits, else the object's data.
    var size = (int)Math.Min(attributes.Size, int.MaxValue);
    if (ZfsConstants.OldZnodeSize + size <= dnode.Bonus.Length)
      return Encoding.UTF8.GetString(dnode.Bonus, ZfsConstants.OldZnodeSize, size);
    var data = context.ObjectSet.ReadBlock(dnode, 0);
    return Encoding.UTF8.GetString(data, 0, Math.Min(size, data.Length));
  }

  private static ulong U64(Dictionary<string, byte[]> values, string name)
    => values.TryGetValue(name, out var v) && v.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(v) : 0;

  private static DateTime? Time(ReadOnlySpan<byte> secondsAndNanoseconds) {
    var seconds = BinaryPrimitives.ReadInt64LittleEndian(secondsAndNanoseconds);
    var nanoseconds = BinaryPrimitives.ReadInt64LittleEndian(secondsAndNanoseconds[8..]);
    if (seconds is < -62135596800L or > 253402300799L || nanoseconds is < 0 or >= 1_000_000_000) return null;
    return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(nanoseconds / 100);
  }

  private void AddDirectory(string path, string dataset, DateTime? modified = null, ulong mode = ZfsConstants.ModeDirectory | 0x1ED) {
    if (this._byPath.TryGetValue(path, out var existing)) {
      if (!this._entries[existing].IsDirectory)
        this._warnings.Add($"'{path}' is both a file and a directory; the file is kept.");
      return;
    }
    this.Add(new ZfsEntry { Name = path, IsDirectory = true, LastModified = modified, Mode = (uint)mode, Dataset = dataset });
  }

  private void Add(ZfsEntry entry) {
    var slash = entry.Name.LastIndexOf('/');
    if (slash > 0) this.AddDirectory(entry.Name[..slash], entry.Dataset);
    if (this._byPath.ContainsKey(entry.Name)) {
      if (!entry.IsDirectory) this._warnings.Add($"'{entry.Name}' appears twice; the first is kept.");
      return;
    }
    this._byPath[entry.Name] = this._entries.Count;
    this._entries.Add(entry);
  }

  private void Guarded(string what, Action action) {
    try {
      action();
    } catch (Exception e) when (IsReadFailure(e)) {
      this._warnings.Add($"{what}: {e.Message}");
      if (e is ZfsEncryptedException && !this._datasets.Any(d => d.Name == what))
        this._datasets.Add(new(what, "", "filesystem", "native encryption"));
    }
  }

  private static bool IsReadFailure(Exception e)
    => e is InvalidDataException or NotSupportedException or IOException or ArgumentException
         or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException;

  private static string Combine(string path, string name) => path.Length == 0 ? name : path + "/" + name;

  /// <summary>Reads a file's contents (or a link's target) into memory.</summary>
  public byte[] Extract(ZfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size > Array.MaxLength)
      throw new IOException($"ZFS: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    using var ms = new MemoryStream();
    this.ExtractTo(entry, ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Streams a file's contents record by record into <paramref name="destination"/>; holes
  /// and the sparse tail come out as zeros. A link writes its target. Returns the byte count.
  /// </summary>
  public long ExtractTo(ZfsEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return 0;
    if (entry.LinkTarget is { } link) {
      var bytes = Encoding.UTF8.GetBytes(link);
      destination.Write(bytes);
      return bytes.Length;
    }
    if (entry.Dnode is not { } dnode || entry.ObjectSet is not { } os)
      throw new InvalidOperationException($"ZFS: '{entry.Name}' has no object behind it.");

    var size = entry.Size;
    var blockSize = (long)dnode.DataBlockSize;
    if (blockSize <= 0) throw new InvalidDataException($"ZFS: '{entry.Name}' has no block size.");
    long written = 0;
    for (ulong blockId = 0; written < size; ++blockId) {
      var take = (int)Math.Min(blockSize, size - written);
      if (blockId > dnode.MaxBlockId) {
        destination.Write(new byte[take]);
      } else {
        var block = os.ReadBlock(dnode, blockId, cacheData: false);
        destination.Write(block, 0, Math.Min(take, block.Length));
        if (block.Length < take) destination.Write(new byte[take - block.Length]);
      }
      written += take;
    }
    return written;
  }

  /// <summary>Releases the device images (unless opened with <c>leaveOpen</c>).</summary>
  public void Dispose() => this._pool.Dispose();
}
