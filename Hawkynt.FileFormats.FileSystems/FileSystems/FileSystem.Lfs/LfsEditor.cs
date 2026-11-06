using System.Buffers.Binary;

namespace FileSystem.Lfs;

/// <summary>
/// Path-level edits on an <see cref="LfsVolume"/>: files are added or replaced, files and
/// directories removed, missing parent directories created. Every directory that changes is
/// rewritten whole; everything else stays where the log put it. <see cref="Commit"/> appends
/// the changes as new partial segments and checkpoints.
/// </summary>
internal sealed class LfsEditor {
  private const byte DtDir = 4, DtReg = 8, DtLnk = 10;

  private sealed class Dir {
    public required uint Ino;
    public required uint Parent;
    public readonly SortedDictionary<string, (uint Ino, byte Type)> Children = new(StringComparer.Ordinal);
  }

  private readonly LfsVolume _v;
  private readonly Dictionary<string, Dir> _dirs = new(StringComparer.Ordinal);
  private readonly HashSet<uint> _dirtyDirs = [];
  private readonly Dictionary<uint, byte[]> _files = [];
  private readonly HashSet<uint> _relinked = [];
  private readonly uint _now;

  private LfsEditor(LfsVolume v, DateTimeOffset now) {
    this._v = v;
    this._now = (uint)Math.Clamp(now.ToUnixTimeSeconds(), 0, uint.MaxValue);
  }

  /// <summary>An editor over a freshly formatted, empty volume.</summary>
  public static LfsEditor ForNew(LfsVolume v, DateTimeOffset now) {
    var e = new LfsEditor(v, now);
    e._dirs[""] = new Dir { Ino = LfsVolume.RootIno, Parent = LfsVolume.RootIno };
    e._dirtyDirs.Add(LfsVolume.RootIno);
    return e;
  }

  /// <summary>An editor over an existing volume, its tree taken from <paramref name="reader"/>.</summary>
  public static LfsEditor ForExisting(LfsVolume v, LfsReader reader, DateTimeOffset now) {
    var e = new LfsEditor(v, now);
    e._dirs[""] = new Dir { Ino = LfsVolume.RootIno, Parent = LfsVolume.RootIno };
    foreach (var entry in reader.Entries) {
      var (parent, name) = Split(entry.Path);
      var type = entry.IsDirectory ? DtDir : entry.IsSymlink ? DtLnk : DtReg;
      e._dirs[parent].Children[name] = (entry.Ino, type);
      if (entry.IsDirectory) e._dirs[entry.Path] = new Dir { Ino = entry.Ino, Parent = e._dirs[parent].Ino };
    }
    return e;
  }

  private static (string Parent, string Name) Split(string path) {
    var cut = path.LastIndexOf('/');
    return cut < 0 ? ("", path) : (path[..cut], path[(cut + 1)..]);
  }

  private static string Normalize(string path) {
    var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    foreach (var p in parts)
      if (p is "." or ".." || System.Text.Encoding.UTF8.GetByteCount(p) > 255)
        throw new ArgumentException($"'{path}' is not a usable LFS path.", nameof(path));
    return string.Join('/', parts);
  }

  /// <summary>Creates <paramref name="path"/> and its missing parents as directories.</summary>
  public void AddDirectory(string path, DateTimeOffset? mtime = null) {
    path = Normalize(path);
    if (path.Length == 0 || this._dirs.ContainsKey(path)) return;
    var (parentPath, name) = Split(path);
    this.AddDirectory(parentPath);
    var parent = this._dirs[parentPath];
    if (parent.Children.ContainsKey(name)) throw new IOException($"'{path}' already exists and is not a directory.");
    var n = this._v.Allocate();
    n.Mode = 0x41ED;   // IFDIR | 0755
    n.Nlink = 2;
    this._v.InitTimes(n, mtime is { } t ? (uint)Math.Max(0, t.ToUnixTimeSeconds()) : null);
    parent.Children[name] = (n.Ino, DtDir);
    this._dirs[path] = new Dir { Ino = n.Ino, Parent = parent.Ino };
    this._v.Inodes[parent.Ino].Nlink++;
    this._dirtyDirs.Add(parent.Ino);
    this._dirtyDirs.Add(n.Ino);
  }

  /// <summary>Adds the file at <paramref name="path"/>, or replaces the content of the one there.</summary>
  public void AddFile(string path, byte[] data, DateTimeOffset? mtime = null, ushort? mode = null) {
    path = Normalize(path);
    if (path.Length == 0) throw new ArgumentException("An LFS file needs a name.", nameof(path));
    var (parentPath, name) = Split(path);
    this.AddDirectory(parentPath);
    var parent = this._dirs[parentPath];
    var stamp = mtime is { } t ? (uint)Math.Max(0, t.ToUnixTimeSeconds()) : (uint?)null;
    LfsVolume.Inode n;
    if (parent.Children.TryGetValue(name, out var existing)) {
      if (existing.Type == DtDir) throw new IOException($"'{path}' is a directory.");
      n = this._v.Inodes[existing.Ino];
      if (existing.Type == DtLnk) n.Mode = (ushort)(0x8000 | (n.Mode & 0x0FFF));
      parent.Children[name] = (existing.Ino, DtReg);
      this._dirtyDirs.Add(parent.Ino);
    } else {
      n = this._v.Allocate();
      n.Mode = (ushort)(0x8000 | ((mode ?? 0x1A4) & 0x0FFF));   // IFREG | 0644 unless told otherwise
      n.Nlink = 1;
      parent.Children[name] = (n.Ino, DtReg);
      this._dirtyDirs.Add(parent.Ino);
    }
    this._v.InitTimes(n, stamp);
    this._files[n.Ino] = data;
  }

  /// <summary>Removes a file (one link of it) or a directory with everything below it.</summary>
  public bool Remove(string path) {
    path = Normalize(path);
    if (path.Length == 0) return false;
    var (parentPath, name) = Split(path);
    if (!this._dirs.TryGetValue(parentPath, out var parent) || !parent.Children.TryGetValue(name, out var child)) return false;
    if (child.Type == DtDir) {
      foreach (var sub in this._dirs[path].Children.Keys.ToList()) this.Remove(path + "/" + sub);
      this._dirs.Remove(path);
      this._dirtyDirs.Remove(child.Ino);
      this._v.Free(child.Ino);
      this._v.Inodes[parent.Ino].Nlink--;
    } else {
      var n = this._v.Inodes[child.Ino];
      n.Nlink--;
      if (n.Nlink <= 0) { this._files.Remove(child.Ino); this._v.Free(child.Ino); }
      else {
        this._v.InitTimes(n, BinaryPrimitives.ReadUInt32LittleEndian(n.Raw.AsSpan(24)));
        this._relinked.Add(child.Ino);
      }
    }
    parent.Children.Remove(name);
    this._dirtyDirs.Add(parent.Ino);
    return true;
  }

  /// <summary>Paths of every file (not directory) below the root.</summary>
  public IEnumerable<string> Files() =>
    this._dirs.SelectMany(d => d.Value.Children.Where(c => c.Value.Type != DtDir).Select(c => d.Key.Length == 0 ? c.Key : d.Key + "/" + c.Key));

  /// <summary>Writes every change as new partial segments and a checkpoint.</summary>
  public void Commit() {
    var pending = new List<LfsVolume.Pending>();
    foreach (var (ino, data) in this._files)
      if (this._v.Inodes.TryGetValue(ino, out var n)) pending.Add(new(n, data, false));
    foreach (var (path, dir) in this._dirs) {
      if (!this._dirtyDirs.Contains(dir.Ino)) continue;
      var n = this._v.Inodes[dir.Ino];
      this._v.InitTimes(n, this._now);
      var content = LfsVolume.BuildDirectory(dir.Ino, dir.Parent, dir.Children.Select(c => (c.Key, c.Value.Ino, c.Value.Type)));
      pending.Add(new(n, content, false));
    }
    // A link count changed without new content (a removed hard link): the inode is rewritten
    // with the content it has.
    foreach (var ino in this._relinked) {
      if (this._files.ContainsKey(ino) || !this._v.Inodes.TryGetValue(ino, out var n)) continue;
      var size = (int)n.Size;
      var inline = (n.Mode & 0xF000) == 0xA000 && size < 60 && BinaryPrimitives.ReadUInt32LittleEndian(n.Raw.AsSpan(104)) == 0;
      pending.Add(new(n, inline ? n.Raw.AsSpan(40, size).ToArray() : this._v.ReadContent(n), inline));
    }
    this._v.Commit(pending);
    this._relinked.Clear();
    this._files.Clear();
    this._dirtyDirs.Clear();
  }
}
