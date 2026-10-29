#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Reiser4;

/// <summary>How the body of a regular file this writer creates is stored.</summary>
public enum Reiser4FileBody {
  /// <summary>Extent items in twigs pointing at data blocks (<c>reg40</c>, "extents" formatting).</summary>
  Extents,
  /// <summary>Tail (<c>plain40</c>) items in leaves holding the bytes themselves (<c>reg40</c>, "tails" formatting).</summary>
  Tails,
  /// <summary>Tails up to four blocks, extents above that — the kernel's "smart" formatting.</summary>
  Smart,
  /// <summary>A cryptcompress (<c>ccreg40</c>) file: compressed disk clusters in <c>ctail40</c> items.</summary>
  Compressed,
  /// <summary>Whatever the volume's root plugin set says new files are: its create plugin, then its formatting.</summary>
  Profile,
}

/// <summary>One unit of an extent body: a hole (no payload) or blocks that receive the payload's bytes.</summary>
internal readonly record struct Reiser4PlanUnit(ulong Width, FilePayload? Payload);

/// <summary>An object as the writer lays it out.</summary>
internal sealed class Reiser4PlanObject {
  internal required Reiser4Key StatKey { get; set; }
  internal required byte[] Stat { get; set; }
  internal List<Reiser4DirEntry> Entries { get; } = [];
  internal List<(Reiser4Key Key, ushort Plugin, byte[] Body)> Items { get; set; } = [];
  internal List<Reiser4PlanUnit> Extents { get; set; } = [];

  internal ushort Mode => BinaryPrimitives.ReadUInt16LittleEndian(this.Stat.AsSpan(2));
  internal int Kind => this.Mode & Reiser4Stat.FileType;
  internal bool IsDirectory => this.Kind == Reiser4Stat.Directory;
  internal ulong ObjectId => this.StatKey.ObjectId;
  internal bool IsCryptcompress {
    get {
      var stat = Reiser4Stat.Parse(this.Stat, out _);
      return stat is not null && stat.PluginSet.TryGetValue(Reiser4PsetMember.File, out var file) && file == 4;
    }
  }

  internal void SetLinks(uint links) => BinaryPrimitives.WriteUInt32LittleEndian(this.Stat.AsSpan(4), links);
  internal void SetSize(ulong size) => BinaryPrimitives.WriteUInt64LittleEndian(this.Stat.AsSpan(8), size);
  internal void SetBytes(ulong bytes) => BinaryPrimitives.WriteUInt64LittleEndian(this.Stat.AsSpan(36), bytes);
}

/// <summary>
/// A whole volume as the writer will lay it out: the fixed blocks it carries (master, format,
/// status, backup), the node plugin, and every object with its stat data, body and entries.
/// Edits — add, replace, remove, metadata updates, links — are made here, and
/// <see cref="Reiser4Tree.Write" /> turns the result into an image.
/// </summary>
/// <remarks>
/// Objects read from a volume keep their stat data, keys, directory entry keys and file bodies as
/// found; only the counters the namespace implies (link counts, directory sizes) are recomputed.
/// New names are keyed with the directory's own hash and fibration plugins.
/// </remarks>
internal sealed class Reiser4Plan {

  internal byte[] Master { get; set; } = [];
  internal byte[] Format { get; set; } = [];
  internal byte[] Status { get; set; } = [];
  internal byte[] Backup { get; set; } = [];
  internal ushort NodePlugin { get; set; }
  internal uint MkfsId { get; set; }
  internal ulong MinimumBlocks { get; set; }
  internal ulong NextObjectId { get; set; }

  /// <summary>How new regular files are stored.</summary>
  internal Reiser4FileBody FileBody { get; set; } = Reiser4FileBody.Extents;

  private readonly Dictionary<Reiser4Key, Reiser4PlanObject> _objects = [];
  internal IReadOnlyList<Reiser4PlanObject> Objects => [.. this._objects.Values.OrderBy(static o => o.StatKey)];
  internal Reiser4PlanObject Root => this._objects[Reiser4Volume.RootStatKey];

  private int MaxItemBody => Reiser4Volume.BlockSize - (this.NodePlugin == 1 ? 32 : Reiser4Tree.NodeHeaderBytes) - Reiser4Tree.ItemHeaderBytes;

  // ── Construction ───────────────────────────────────────────────────────

  /// <summary>A plan holding everything <paramref name="volume" /> holds, bodies referencing its blocks.</summary>
  internal static Reiser4Plan FromVolume(Reiser4Volume volume) {
    if (!volume.TreeValid || volume.Root is null)
      throw new NotSupportedException($"Reiser4: refusing to rebuild: {volume.Problem ?? "the tree was not read"}.");
    var status = volume.ReadBlock(21);
    if (!status.AsSpan(0, 15).SequenceEqual("ReiSeR4StATusBl"u8) || BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(16)) != 0)
      throw new NotSupportedException("Reiser4: refusing to rebuild: the status block does not mark the volume consistent.");
    var format = volume.ReadBlock(17);
    var plan = new Reiser4Plan {
      Master = volume.ReadBlock(16),
      Format = format,
      Status = status,
      Backup = volume.ReadBlock(22),
      NodePlugin = volume.NodePlugin,
      MkfsId = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(48)),
      MinimumBlocks = volume.BlockCount,
    };
    var maxId = Reiser4Volume.RootObjectId;
    foreach (var obj in volume.Objects.Values) {
      var planned = new Reiser4PlanObject { StatKey = obj.StatKey, Stat = obj.Stat.Raw.ToArray() };
      planned.Entries.AddRange(obj.Entries);
      if (obj.Body.Count > 0 && obj.Body[0].Plugin == Reiser4ItemPlugin.Extent) {
        foreach (var (start, width) in volume.ExtentUnits(obj))
          planned.Extents.Add(new Reiser4PlanUnit(width, start == 0 ? null : BlockPayload(volume, start, width)));
      } else {
        foreach (var item in obj.Body) planned.Items.Add((item.Key, item.Plugin, volume.ReadBody(item)));
      }
      plan._objects.Add(obj.StatKey, planned);
      maxId = Math.Max(maxId, obj.ObjectId);
    }
    var next = BinaryPrimitives.ReadUInt64LittleEndian(format.AsSpan(24));
    plan.NextObjectId = Math.Max(next, Math.Max(0x10000UL, maxId + 1));
    plan._paths = null;
    return plan;
  }

  /// <summary>An empty volume made from the captured mkfs blocks: a root directory holding only its dot entries.</summary>
  internal static Reiser4Plan Fresh(byte[] master, byte[] format, byte[] status, byte[] backup, byte[] rootLeaf, uint mkfsId, ulong blocks) {
    var rootStatOffset = BinaryPrimitives.ReadUInt16LittleEndian(rootLeaf.AsSpan(Reiser4Volume.BlockSize - Reiser4Tree.ItemHeaderBytes + 32));
    var rootStatEnd = BinaryPrimitives.ReadUInt16LittleEndian(rootLeaf.AsSpan(Reiser4Volume.BlockSize - 2 * Reiser4Tree.ItemHeaderBytes + 32));
    var plan = new Reiser4Plan {
      Master = master, Format = format, Status = status, Backup = backup,
      NodePlugin = 0, MkfsId = mkfsId, MinimumBlocks = blocks, NextObjectId = 0x10000,
    };
    var root = new Reiser4PlanObject { StatKey = Reiser4Volume.RootStatKey, Stat = rootLeaf.AsSpan(rootStatOffset, rootStatEnd - rootStatOffset).ToArray() };
    root.Entries.Add(new Reiser4DirEntry(0, 0, 0, [(byte)'.'], Reiser4Volume.RootStatKey));
    var (o, i, f, _) = Reiser4NameKeys.Make(".."u8, Reiser4HashId.R5, Reiser4FibrationId.Extension1);
    root.Entries.Add(new Reiser4DirEntry(o, i, f, ".."u8.ToArray(), Reiser4Volume.RootStatKey));
    plan._objects.Add(root.StatKey, root);
    return plan;
  }

  private static FilePayload BlockPayload(Reiser4Volume volume, ulong start, ulong width)
    => FilePayload.FromStream(checked((long)width * Reiser4Volume.BlockSize), () => {
      var buffer = new MemoryStream();
      volume.CopyBlocks(start, width, buffer, checked((long)width * Reiser4Volume.BlockSize));
      buffer.Position = 0;
      return buffer;
    });

  // ── Profile ────────────────────────────────────────────────────────────

  /// <summary>A plugin-set member of <paramref name="owner" />: its own, else the root's, else the kernel default.</summary>
  internal ushort Plugin(Reiser4PlanObject owner, ushort member) {
    if (Reiser4Stat.Parse(owner.Stat, out _) is { } own && own.PluginSet.TryGetValue(member, out var value)) return value;
    if (Reiser4Stat.Parse(this.Root.Stat, out _) is { } root && root.PluginSet.TryGetValue(member, out var inherited)) return inherited;
    return member switch {
      Reiser4PsetMember.Formatting => 2,
      Reiser4PsetMember.Hash => (ushort)Reiser4HashId.R5,
      Reiser4PsetMember.Fibration => (ushort)Reiser4FibrationId.Extension1,
      Reiser4PsetMember.CompressionMode => (ushort)Reiser4CompressionModeId.Convert,
      _ => 0,
    };
  }

  // ── Namespace ──────────────────────────────────────────────────────────

  private Dictionary<string, (Reiser4PlanObject Parent, Reiser4DirEntry Entry)>? _paths;

  /// <summary>Every path, with the directory that names it and the entry that does.</summary>
  internal IReadOnlyDictionary<string, (Reiser4PlanObject Parent, Reiser4DirEntry Entry)> Paths {
    get {
      if (this._paths is { } cached) return cached;
      var paths = new Dictionary<string, (Reiser4PlanObject, Reiser4DirEntry)>(StringComparer.Ordinal);
      void Walk(Reiser4PlanObject directory, string prefix, int depth) {
        if (depth > 128) throw new InvalidDataException("Reiser4: the namespace is deeper than this writer walks.");
        foreach (var entry in directory.Entries) {
          if (entry.IsDot || entry.IsDotDot) continue;
          var path = prefix.Length == 0 ? Reiser4Volume.DecodeName(entry.Name) : prefix + "/" + Reiser4Volume.DecodeName(entry.Name);
          paths[path] = (directory, entry);
          if (this._objects.TryGetValue(entry.Target, out var target) && target.IsDirectory) Walk(target, path, depth + 1);
        }
      }
      Walk(this.Root, "", 0);
      return this._paths = paths;
    }
  }

  internal Reiser4PlanObject? Find(string path) {
    var normalized = Normalize(path);
    if (normalized.Length == 0) return this.Root;
    return this.Paths.TryGetValue(normalized, out var hit) ? this._objects.GetValueOrDefault(hit.Entry.Target) : null;
  }

  internal static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

  private Reiser4PlanObject EnsureDirectory(string path, Reiser4Reader.FileMetadata? metadata = null) {
    var normalized = Normalize(path);
    if (normalized.Length == 0) return this.Root;
    if (this.Find(normalized) is { } existing) {
      if (!existing.IsDirectory) throw new InvalidDataException($"Reiser4: file/directory collision at '{normalized}'.");
      return existing;
    }
    var slash = normalized.LastIndexOf('/');
    var parent = this.EnsureDirectory(slash < 0 ? "" : normalized[..slash]);
    var name = Encoding.UTF8.GetBytes(slash < 0 ? normalized : normalized[(slash + 1)..]);
    var stat = this.NewStat(metadata, 0x41ED, 0, 0, symlink: null, pluginSet: null);
    var directory = this.Link(parent, name, stat, metadata?.ObjectId ?? 0);
    var (o, i, f, _) = Reiser4NameKeys.Make(".."u8, Reiser4HashId.R5, Reiser4FibrationId.Extension1);
    directory.Entries.Add(new Reiser4DirEntry(0, 0, 0, [(byte)'.'], directory.StatKey));
    directory.Entries.Add(new Reiser4DirEntry(o, i, f, ".."u8.ToArray(), parent.StatKey));
    return directory;
  }

  /// <summary>Creates a new object named <paramref name="name" /> in <paramref name="parent" />.</summary>
  private Reiser4PlanObject Link(Reiser4PlanObject parent, byte[] name, byte[] stat, ulong requestedId) {
    var hash = (Reiser4HashId)this.Plugin(parent, Reiser4PsetMember.Hash);
    var fibration = (Reiser4FibrationId)this.Plugin(parent, Reiser4PsetMember.Fibration);
    var (ordering, objectId, offset, hashed) = MakeKey(name, hash, fibration);
    if (parent.Entries.Any(e => e.Ordering == ordering && e.ObjectId == objectId && e.Offset == offset))
      throw new NotSupportedException($"Reiser4: the name '{Reiser4Volume.DecodeName(name)}' would share its key with an existing entry.");
    ulong id;
    if (requestedId > Reiser4Volume.RootObjectId && this._objects.Values.All(o => o.ObjectId != requestedId)) {
      id = requestedId;
      this.NextObjectId = Math.Max(this.NextObjectId, id + 1);
    } else id = this.NextObjectId++;
    var statKey = Reiser4Key.Of(parent.ObjectId, Reiser4Key.MinorStatData, ordering, id, 0);
    var obj = new Reiser4PlanObject { StatKey = statKey, Stat = stat };
    this._objects.Add(statKey, obj);
    parent.Entries.Add(new Reiser4DirEntry(ordering, objectId, offset, name, statKey));
    this._paths = null;
    _ = hashed;
    return obj;
  }

  private static (ulong Ordering, ulong ObjectId, ulong Offset, bool Hashed) MakeKey(byte[] name, Reiser4HashId hash, Reiser4FibrationId fibration) {
    if (name.Length > Reiser4NameKeys.MaxInlineLength && !Reiser4NameKeys.CanHash(hash))
      throw new NotSupportedException(
        $"Reiser4: names longer than {Reiser4NameKeys.MaxInlineLength} bytes cannot be added under the {hash} hash, which the kernel and reiser4progs compute differently.");
    return Reiser4NameKeys.Make(name, hash, fibration);
  }

  private (Reiser4PlanObject Parent, byte[] Name) ParentOf(string path) {
    var normalized = Normalize(path);
    if (Reiser4Writer.RejectName(normalized) is { } reason) throw new NotSupportedException("Reiser4: " + reason + ".");
    var slash = normalized.LastIndexOf('/');
    var parent = this.EnsureDirectory(slash < 0 ? "" : normalized[..slash]);
    return (parent, Encoding.UTF8.GetBytes(slash < 0 ? normalized : normalized[(slash + 1)..]));
  }

  // ── Edits ──────────────────────────────────────────────────────────────

  internal void AddDirectory(string path, Reiser4Reader.FileMetadata? metadata = null) {
    var normalized = Normalize(path);
    if (normalized.Length == 0) {
      if (metadata is not null) this.ApplyMetadata(this.Root, metadata);
      return;
    }
    if (Reiser4Writer.RejectName(normalized) is { } reason) throw new NotSupportedException("Reiser4: " + reason + ".");
    var existing = this.Find(normalized);
    var directory = this.EnsureDirectory(normalized, metadata);
    if (existing is not null && metadata is not null) this.ApplyMetadata(directory, metadata);
  }

  /// <summary>Adds a regular file, or replaces the body of the one already there (keeping its identity and metadata).</summary>
  internal void AddFile(string path, FilePayload payload, Reiser4Reader.FileMetadata? metadata = null) {
    var (parent, name) = this.ParentOf(path);
    var existing = this.Find(path);
    Reiser4PlanObject file;
    bool compressed;
    if (existing is not null) {
      if (existing.Kind != Reiser4Stat.Regular)
        throw new InvalidDataException($"Reiser4: '{Normalize(path)}' exists and is not a regular file.");
      file = existing;
      compressed = existing.IsCryptcompress;
      if (metadata is not null) this.ApplyMetadata(file, metadata);
    } else {
      compressed = this.ResolveBody(parent, payload.Size) == Reiser4FileBody.Compressed ||
        metadata is { RawStatData.Length: >= 44 } carried && Reiser4Stat.Parse(carried.RawStatData, out _) is { } carriedStat &&
        carriedStat.PluginSet.TryGetValue(Reiser4PsetMember.File, out var carriedFile) && carriedFile == 4;
      var pset = compressed && metadata is not { RawStatData.Length: >= 44 } ? new Dictionary<ushort, ushort> { [Reiser4PsetMember.File] = 4 } : null;
      file = this.Link(parent, name, this.NewStat(metadata, 0x81A4, 0, 0, null, pset), metadata?.ObjectId ?? 0);
    }
    this.SetBody(file, parent, payload, compressed);
  }

  private Reiser4FileBody ResolveBody(Reiser4PlanObject parent, long size) {
    var body = this.FileBody;
    if (body == Reiser4FileBody.Profile) {
      if (this.Plugin(parent, Reiser4PsetMember.Create) == 4) return Reiser4FileBody.Compressed;
      body = this.Plugin(parent, Reiser4PsetMember.Formatting) switch { 0 => Reiser4FileBody.Extents, 1 => Reiser4FileBody.Tails, _ => Reiser4FileBody.Smart };
    }
    if (body == Reiser4FileBody.Smart) body = size > 4 * Reiser4Volume.BlockSize ? Reiser4FileBody.Extents : Reiser4FileBody.Tails;
    return body;
  }

  private void SetBody(Reiser4PlanObject file, Reiser4PlanObject parent, FilePayload payload, bool compressed) {
    file.Items = [];
    file.Extents = [];
    var size = (ulong)payload.Size;
    file.SetSize(size);
    var key = file.StatKey;
    Reiser4Key BodyKey(ulong offset) => Reiser4Key.Of(key.Locality, Reiser4Key.MinorBody, key.Ordering, key.ObjectId, offset);
    if (compressed) {
      var clusterId = this.Plugin(file, Reiser4PsetMember.Cluster);
      if (clusterId > 4) throw new NotSupportedException($"Reiser4: cluster plugin {clusterId} is not supported.");
      var shift = 16 - clusterId;
      var clusterSize = 1 << shift;
      var compression = (Reiser4CompressionId)this.Plugin(file, Reiser4PsetMember.Compression);
      var mode = (Reiser4CompressionModeId)this.Plugin(file, Reiser4PsetMember.CompressionMode);
      using var source = payload.Open();
      var buffer = new byte[clusterSize];
      ulong clusters = 0;
      for (ulong start = 0; start < size; start += (ulong)clusterSize) {
        var length = (int)Math.Min((ulong)clusterSize, size - start);
        source.ReadExactly(buffer, 0, length);
        var disk = Reiser4Clusters.Encode(buffer.AsSpan(0, length), compression, mode);
        for (var at = 0; at < disk.Length;) {
          var take = Math.Min(this.MaxItemBody - 1, disk.Length - at);
          var item = new byte[take + 1];
          item[0] = (byte)shift;
          disk.AsSpan(at, take).CopyTo(item.AsSpan(1));
          file.Items.Add((BodyKey(start + (ulong)at), Reiser4ItemPlugin.CompressedTail, item));
          at += take;
        }
        ++clusters;
      }
      file.SetBytes(clusters * (ulong)clusterSize);
      return;
    }
    var body = this.ResolveBody(parent, payload.Size);
    if (body == Reiser4FileBody.Tails) {
      using var source = payload.Open();
      for (ulong at = 0; at < size;) {
        var take = (int)Math.Min((ulong)this.MaxItemBody, size - at);
        var chunk = new byte[take];
        source.ReadExactly(chunk);
        file.Items.Add((BodyKey(at), Reiser4ItemPlugin.Tail, chunk));
        at += (ulong)take;
      }
      file.SetBytes(size);
      return;
    }
    var blocks = (size + Reiser4Volume.BlockSize - 1) / Reiser4Volume.BlockSize;
    if (blocks > 0) file.Extents.Add(new Reiser4PlanUnit(blocks, payload));
    file.SetBytes(blocks * Reiser4Volume.BlockSize);
  }

  /// <summary>Adds a symbolic link holding <paramref name="target" />.</summary>
  internal void AddSymlink(string path, string target, Reiser4Reader.FileMetadata? metadata = null) {
    var bytes = Encoding.UTF8.GetBytes(target);
    if (bytes.Length is 0 or > 4095 || Array.IndexOf(bytes, (byte)0) >= 0)
      throw new NotSupportedException("Reiser4: a symlink target must be 1..4095 bytes without NUL.");
    if (this.Find(path) is not null) throw new InvalidDataException($"Reiser4: '{Normalize(path)}' already exists.");
    var (parent, name) = this.ParentOf(path);
    this.Link(parent, name, this.NewStat(metadata, 0xA1FF, (ulong)bytes.Length, 0, bytes, null), metadata?.ObjectId ?? 0);
  }

  /// <summary>Adds a device node, FIFO or socket. <paramref name="device" /> is the kernel's (major &lt;&lt; 20 | minor).</summary>
  internal void AddSpecial(string path, ushort mode, ulong device, Reiser4Reader.FileMetadata? metadata = null) {
    var kind = mode & Reiser4Stat.FileType;
    if (kind is not (Reiser4Stat.CharDevice or Reiser4Stat.BlockDevice or Reiser4Stat.Fifo or Reiser4Stat.Socket))
      throw new NotSupportedException("Reiser4: special files are character or block devices, FIFOs or sockets.");
    if (kind is Reiser4Stat.Fifo or Reiser4Stat.Socket && device != 0)
      throw new NotSupportedException("Reiser4: a FIFO or socket has no device number.");
    if (this.Find(path) is not null) throw new InvalidDataException($"Reiser4: '{Normalize(path)}' already exists.");
    var (parent, name) = this.ParentOf(path);
    this.Link(parent, name, this.NewStat(metadata, mode, 0, device, null, null), metadata?.ObjectId ?? 0);
  }

  /// <summary>Adds a second name for an existing non-directory object.</summary>
  internal void AddHardLink(string path, string existingPath) {
    var target = this.Find(existingPath) ?? throw new FileNotFoundException("Reiser4: the link target was not found.", existingPath);
    if (target.IsDirectory) throw new NotSupportedException("Reiser4: directories cannot be hard-linked.");
    if (this.Find(path) is not null) throw new InvalidDataException($"Reiser4: '{Normalize(path)}' already exists.");
    var (parent, name) = this.ParentOf(path);
    var hash = (Reiser4HashId)this.Plugin(parent, Reiser4PsetMember.Hash);
    var fibration = (Reiser4FibrationId)this.Plugin(parent, Reiser4PsetMember.Fibration);
    var (ordering, objectId, offset, _) = MakeKey(name, hash, fibration);
    if (parent.Entries.Any(e => e.Ordering == ordering && e.ObjectId == objectId && e.Offset == offset))
      throw new NotSupportedException($"Reiser4: the name '{Reiser4Volume.DecodeName(name)}' would share its key with an existing entry.");
    parent.Entries.Add(new Reiser4DirEntry(ordering, objectId, offset, name, target.StatKey));
    this._paths = null;
  }

  /// <summary>Removes a name; a directory goes with everything beneath it, an object with its last name.</summary>
  internal bool Remove(string path) {
    var normalized = Normalize(path);
    if (normalized.Length == 0 || !this.Paths.TryGetValue(normalized, out var hit)) return false;
    hit.Parent.Entries.Remove(hit.Entry);
    this._paths = null;
    this.CollectUnreachable();
    return true;
  }

  private void CollectUnreachable() {
    var reached = new HashSet<Reiser4Key> { this.Root.StatKey };
    var stack = new Stack<Reiser4PlanObject>([this.Root]);
    while (stack.Count > 0)
      foreach (var entry in stack.Pop().Entries) {
        if (entry.IsDot || entry.IsDotDot || !reached.Add(entry.Target)) continue;
        if (this._objects.TryGetValue(entry.Target, out var target) && target.IsDirectory) stack.Push(target);
      }
    foreach (var key in this._objects.Keys.Where(k => !reached.Contains(k)).ToList()) this._objects.Remove(key);
  }

  /// <summary>Sets mode permission bits, owner and the three timestamps of an object.</summary>
  internal void ApplyMetadata(Reiser4PlanObject obj, Reiser4Reader.FileMetadata metadata) {
    var stat = obj.Stat;
    var kind = obj.Mode & Reiser4Stat.FileType;
    BinaryPrimitives.WriteUInt16LittleEndian(stat.AsSpan(2), (ushort)(kind | (metadata.Mode & 0x0FFF)));
    BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(16), metadata.UserId);
    BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(20), metadata.GroupId);
    BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(24), metadata.AccessedTime);
    BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(28), metadata.ModifiedTime);
    BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(32), metadata.ChangedTime);
  }

  /// <summary>
  /// A stat data for a new object: the light-weight and Unix extensions, plus a symlink target or a
  /// plugin set where the object needs one. A caller's raw stat data is carried instead when it
  /// parses, with the fields this writer owns patched in.
  /// </summary>
  private byte[] NewStat(Reiser4Reader.FileMetadata? metadata, ushort mode, ulong size, ulong bytesOrDevice,
      byte[]? symlink, Dictionary<ushort, ushort>? pluginSet) {
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    byte[] stat;
    if (metadata is { RawStatData.Length: >= 44 } carried && symlink is null && pluginSet is null) {
      if (Reiser4Stat.Parse(carried.RawStatData, out var problem) is null) throw new NotSupportedException($"Reiser4: {problem}.");
      stat = carried.RawStatData.ToArray();
    } else {
      var mask = (ushort)(0x3 | (symlink is null ? 0 : 0x8) | (pluginSet is null ? 0 : 0x10));
      var length = 44 + (symlink is null ? 0 : symlink.Length + 1) + (pluginSet is null ? 0 : 2 + 4 * pluginSet.Count);
      stat = new byte[length];
      BinaryPrimitives.WriteUInt16LittleEndian(stat, mask);
      var at = 44;
      if (symlink is not null) { symlink.CopyTo(stat.AsSpan(at)); at += symlink.Length + 1; }
      if (pluginSet is not null) {
        BinaryPrimitives.WriteUInt16LittleEndian(stat.AsSpan(at), (ushort)pluginSet.Count);
        var slot = at + 2;
        foreach (var (member, id) in pluginSet.OrderBy(static p => p.Key)) {
          BinaryPrimitives.WriteUInt16LittleEndian(stat.AsSpan(slot), member);
          BinaryPrimitives.WriteUInt16LittleEndian(stat.AsSpan(slot + 2), id);
          slot += 4;
        }
      }
    }
    var kind = mode & Reiser4Stat.FileType;
    BinaryPrimitives.WriteUInt16LittleEndian(stat.AsSpan(2), (ushort)(kind | ((metadata?.Mode ?? mode) & 0x0FFF)));
    BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(stat.AsSpan(8), size);
    BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(16), metadata?.UserId ?? 0);
    BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(20), metadata?.GroupId ?? 0);
    BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(24), metadata?.AccessedTime ?? now);
    BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(28), metadata?.ModifiedTime ?? now);
    BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(32), metadata?.ChangedTime ?? now);
    BinaryPrimitives.WriteUInt64LittleEndian(stat.AsSpan(36), bytesOrDevice);
    return stat;
  }

  // ── Finishing ──────────────────────────────────────────────────────────

  /// <summary>Recomputes what the namespace implies: link counts, directory sizes and directory bytes.</summary>
  internal void Finish() {
    var names = new Dictionary<Reiser4Key, uint>();
    var subdirectories = new Dictionary<Reiser4Key, uint>();
    foreach (var directory in this._objects.Values.Where(static o => o.IsDirectory))
      foreach (var entry in directory.Entries) {
        if (entry.IsDot || entry.IsDotDot) continue;
        names[entry.Target] = names.GetValueOrDefault(entry.Target) + 1;
        if (this._objects.TryGetValue(entry.Target, out var target) && target.IsDirectory)
          subdirectories[directory.StatKey] = subdirectories.GetValueOrDefault(directory.StatKey) + 1;
      }
    foreach (var obj in this._objects.Values) {
      if (obj.IsDirectory) {
        var isRoot = obj.StatKey == Reiser4Volume.RootStatKey;
        obj.SetLinks((isRoot ? 3U : 2U) + subdirectories.GetValueOrDefault(obj.StatKey));
        obj.SetSize((ulong)obj.Entries.Count);
        obj.SetBytes(Reiser4Tree.DirectoryBytes(obj.Entries));
      } else {
        obj.SetLinks(names.GetValueOrDefault(obj.StatKey));
      }
    }
  }

  internal Reiser4Tree.Result Write(Stream output) {
    this.Finish();
    return Reiser4Tree.Write(this, output);
  }
}
