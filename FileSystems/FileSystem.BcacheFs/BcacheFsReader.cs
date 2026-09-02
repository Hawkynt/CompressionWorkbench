#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Reads the files a bcachefs volume holds.
/// </summary>
/// <remarks>
/// There is no directory to walk and no inode table to index. Names come from the
/// dirents tree, each key of which sits at a position made of its directory's
/// inode and a hash of the name; sizes come from the inodes tree; and the bytes
/// come from the extents tree, whose keys are positioned by the inode and the
/// sector one past the end of what they cover. A path is rebuilt by joining the
/// three.
/// </remarks>
public sealed class BcacheFsReader : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<Entry> _entries = [];

  /// <summary>True when the volume's superblock and b-tree roots read as they should.</summary>
  public bool Valid { get; }

  /// <summary>Why the volume did not read, when it did not.</summary>
  public string Status { get; } = "";

  /// <summary>Every file the volume holds, by full path.</summary>
  public IReadOnlyList<Entry> Entries => this._entries;

  /// <summary>The volume's length in bytes.</summary>
  public long Length => this._stream.Length;

  /// <summary>The label the superblock carries.</summary>
  public string Label { get; } = "";

  /// <summary>Directories the volume holds, by full path.</summary>
  public IReadOnlyList<string> Directories { get; } = [];

  /// <summary>One run of sectors belonging to a file.</summary>
  /// <param name="FirstSector">Where it starts on the device.</param>
  /// <param name="Sectors">How long it is.</param>
  /// <param name="FileOffset">Which byte of the file it begins at.</param>
  public readonly record struct Extent(long FirstSector, int Sectors, long FileOffset);

  /// <summary>One file: its path, its length, and where its bytes are.</summary>
  public sealed record Entry(string Name, long Size, ulong Inode, IReadOnlyList<Extent> Extents) {

    /// <summary>Where the file's first byte is, or zero when it holds none.</summary>
    public long FirstSector => this.Extents.Count == 0 ? 0 : this.Extents[0].FirstSector;
  }

    /// <summary>
  /// Initializes a new instance of <see cref="BcacheFsReader"/>.
  /// </summary>
public BcacheFsReader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    this._leaveOpen = leaveOpen;
    if (stream.CanSeek) stream.Position = 0;

    var volume = BcacheFsVolume.Open(stream);
    this.Valid = volume.Valid;
    this.Status = volume.Status;
    this.Label = volume.Label;
    if (!volume.Valid) return;

    var directories = new List<string>();
    this.Build(volume, directories);
    this.Directories = directories;
  }

  private void Build(BcacheFsVolume volume, List<string> directories) {
    // Names first: each dirent says which directory it is in and what it points at.
    var children = new Dictionary<ulong, List<(string Name, ulong Target, bool IsDirectory)>>();
    foreach (var key in volume.Keys(BtreeDirents)) {
      if (key.Type != KeyDirent || key.Value.Length < 16) continue;

      var target = BinaryPrimitives.ReadUInt64LittleEndian(key.Value);
      var type = key.Value[8] & 0x1F;
      var name = ReadName(key.Value.AsSpan(9));
      if (name.Length == 0) continue;

      if (!children.TryGetValue(key.Position.Inode, out var list))
        children[key.Position.Inode] = list = [];
      list.Add((name, target, type == DtDir));
    }

    // Then sizes, from the inodes tree.
    var sizes = new Dictionary<ulong, long>();
    foreach (var key in volume.Keys(BtreeInodes)) {
      if (key.Type != KeyInodeV3 || key.Value.Length < 48) continue;
      sizes[key.Position.Offset] = (long)BinaryPrimitives.ReadUInt64LittleEndian(key.Value.AsSpan(32));
    }

    // Then the extents, gathered per inode and ordered by where they land in the file.
    var extents = new Dictionary<ulong, List<Extent>>();
    foreach (var key in volume.Keys(BtreeExtents)) {
      if (key.Type != KeyExtent || key.Value.Length < 8) continue;

      long sector = -1;
      for (var offset = 0; offset + 8 <= key.Value.Length; offset += 8) {
        var word = BinaryPrimitives.ReadUInt64LittleEndian(key.Value.AsSpan(offset));
        if (!IsPointer(word)) continue;
        sector = PointerSector(word);
        break;
      }

      if (sector < 0) continue;

      // A key names the sector one past its end, so its start is that less its size.
      var start = (long)key.Position.Offset - key.Size;
      if (!extents.TryGetValue(key.Position.Inode, out var list))
        extents[key.Position.Inode] = list = [];
      list.Add(new Extent(sector, (int)key.Size, start * SectorSize));
    }

    foreach (var list in extents.Values)
      list.Sort((a, b) => a.FileOffset.CompareTo(b.FileOffset));

    // Finally the paths, walked down from the root directory.
    var pending = new Queue<(ulong Inode, string Path)>();
    pending.Enqueue((RootInode, string.Empty));
    var seen = new HashSet<ulong> { RootInode };

    while (pending.Count > 0) {
      var (inode, path) = pending.Dequeue();
      if (!children.TryGetValue(inode, out var list)) continue;

      foreach (var (name, target, isDirectory) in list) {
        var full = path.Length == 0 ? name : path + "/" + name;
        if (isDirectory) {
          if (!seen.Add(target)) continue;
          directories.Add(full);
          pending.Enqueue((target, full));
          continue;
        }

        var size = sizes.GetValueOrDefault(target, 0L);
        var runs = extents.TryGetValue(target, out var found) ? found : [];
        this._entries.Add(new Entry(full, size, target, runs));
      }
    }

    this._entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    directories.Sort(StringComparer.Ordinal);
  }

  private static string ReadName(ReadOnlySpan<byte> source) {
    // The name runs to the end of the value, less whatever zero padding rounded it
    // out to a whole number of words.
    var end = source.Length;
    while (end > 0 && source[end - 1] == 0) --end;
    return end == 0 ? string.Empty : Encoding.UTF8.GetString(source[..end]);
  }

  /// <summary>Writes one file's bytes to <paramref name="output" />.</summary>
  public void ExtractTo(Entry entry, Stream output) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(output);

    var buffer = new byte[BucketBytes];
    var remaining = entry.Size;
    foreach (var extent in entry.Extents) {
      if (remaining <= 0) break;

      var want = Math.Min((long)extent.Sectors * SectorSize, remaining);
      this._stream.Position = extent.FirstSector * SectorSize;

      while (want > 0) {
        var chunk = (int)Math.Min(buffer.Length, want);
        this._stream.ReadExactly(buffer, 0, chunk);
        output.Write(buffer, 0, chunk);
        want -= chunk;
        remaining -= chunk;
      }
    }
  }

  /// <summary>The whole of one file.</summary>
  public byte[] Read(Entry entry) {
    using var buffer = new MemoryStream();
    this.ExtractTo(entry, buffer);
    return buffer.ToArray();
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() {
    if (!this._leaveOpen) this._stream.Dispose();
  }
}
