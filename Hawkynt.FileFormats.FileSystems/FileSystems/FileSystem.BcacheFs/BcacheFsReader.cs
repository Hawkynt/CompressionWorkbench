#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Reads the files a bcachefs volume holds.
/// </summary>
/// <remarks>
/// <para>There is no directory to walk and no inode table to index. Names come
/// from the dirents tree, each key of which sits at a position made of its
/// directory's inode and a hash of the name; sizes and modes come from the inodes
/// tree; and the bytes come from the extents tree, whose keys are positioned by
/// the inode and the sector one past the end of what they cover. A path is rebuilt
/// by joining the three.</para>
///
/// <para>The keys are taken from the recovered view of the volume — every bset of
/// every node, packed or not, with the journal replayed over them — because that is
/// what a volume someone else wrote looks like: <c>bcachefs format</c> and the
/// kernel append bsets as they go and pack every key against the node's format,
/// where this package writes one unpacked bset per node.</para>
/// </remarks>
public sealed class BcacheFsReader : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<Entry> _entries = [];

  /// <summary>True when the volume's superblock and b-tree roots read as they should.</summary>
  public bool Valid { get; }

  /// <summary>Why the volume did not read, when it did not.</summary>
  public string Status { get; } = "";

  /// <summary>Every file and symbolic link the volume holds, by full path.</summary>
  public IReadOnlyList<Entry> Entries => this._entries;

  /// <summary>The volume's length in bytes.</summary>
  public long Length => this._stream.Length;

  /// <summary>The label the superblock carries.</summary>
  public string Label { get; } = "";

  /// <summary>Directories the volume holds, by full path.</summary>
  public IReadOnlyList<string> Directories { get; } = [];

  /// <summary>One run of sectors belonging to a file.</summary>
  /// <param name="FirstSector">Where its live data starts on the device.</param>
  /// <param name="Sectors">How long it is.</param>
  /// <param name="FileOffset">Which byte of the file it begins at.</param>
  public readonly record struct Extent(long FirstSector, int Sectors, long FileOffset);

  /// <summary>One file: its path, its length, and where its bytes are.</summary>
  public sealed record Entry(string Name, long Size, ulong Inode, IReadOnlyList<Extent> Extents) {

    /// <summary>Where the file's first byte is, or zero when it holds none.</summary>
    public long FirstSector => this.Extents.Count == 0 ? 0 : this.Extents[0].FirstSector;

    /// <summary>Where a symbolic link points, or null for a regular file.</summary>
    public string? LinkTarget { get; init; }

    /// <summary>
    /// Why the file's bytes cannot be read here — a compressed or encrypted
    /// extent — or null when they can.
    /// </summary>
    public string? Unreadable { get; init; }
  }

  /// <summary>
  /// Initializes a new instance of <see cref="BcacheFsReader"/>.
  /// </summary>
  public BcacheFsReader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    this._leaveOpen = leaveOpen;
    if (stream.CanSeek) stream.Position = 0;

    BcacheFsCoreVolume core;
    try {
      core = BcacheFsCoreVolume.Open(stream);
    } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException or NotSupportedException) {
      this.Status = "bcachefs: " + e.Message;
      return;
    }

    this.Label = core.Superblock.Label;
    if (!core.Recoverable) {
      this.Status = "bcachefs: " + string.Join("; ", core.Diagnostics.DefaultIfEmpty("the volume could not be recovered."));
      return;
    }

    var directories = new List<string>();
    var problems = this.Build(core, directories);
    this.Directories = directories;
    this.Valid = problems.Count == 0;
    this.Status = string.Join("; ", problems);
  }

  private List<string> Build(BcacheFsCoreVolume core, List<string> directories) {
    var problems = new List<string>();

    IReadOnlyList<BcacheFsRawKey> Keys(BcacheFsBtreeId id) {
      if (core.Root(id) == null) return [];
      var tree = BcacheFsBtreeReader.ReadTree(core, id);
      if (!tree.Complete) problems.AddRange(tree.Diagnostics);
      return tree.MaterializedLeafSlots;
    }

    // Only the root subvolume's view: a key in another snapshot belongs to another tree of names.
    var snapshot = SnapshotIdMax;
    foreach (var key in Keys(BcacheFsBtreeId.Subvolumes))
      if (key.RawType == KeySubvolume && key.Position.Offset == RootSubvolume && key.Value.Length >= 8)
        snapshot = BinaryPrimitives.ReadUInt32LittleEndian(key.Value.AsSpan(4));

    // Names first: each dirent says which directory it is in and what it points at.
    var children = new Dictionary<ulong, List<(string Name, ulong Target, byte Type)>>();
    foreach (var key in Keys(BcacheFsBtreeId.Dirents)) {
      if (key.RawType != KeyDirent || key.Value.Length < 10 || key.Position.Snapshot != snapshot) continue;

      var type = key.Value[8];
      if (type is not (DtDir or DtReg or DtLnk)) continue;
      var target = BinaryPrimitives.ReadUInt64LittleEndian(key.Value);
      var name = ReadName(key.Value.AsSpan(9));
      if (name.Length == 0) continue;

      if (!children.TryGetValue(key.Position.Inode, out var list))
        children[key.Position.Inode] = list = [];
      list.Add((name, target, type));
    }

    // Then sizes, from the inodes tree.
    var sizes = new Dictionary<ulong, long>();
    foreach (var key in Keys(BcacheFsBtreeId.Inodes)) {
      if (key.RawType != KeyInodeV3 || key.Value.Length < 48 || key.Position.Snapshot != snapshot) continue;
      sizes[key.Position.Offset] = (long)BinaryPrimitives.ReadUInt64LittleEndian(key.Value.AsSpan(32));
    }

    // Then the extents, gathered per inode and ordered by where they land in the file.
    var extents = new Dictionary<ulong, List<Extent>>();
    var unreadable = new Dictionary<ulong, string>();
    foreach (var key in Keys(BcacheFsBtreeId.Extents)) {
      if (key.Position.Snapshot != snapshot) continue;
      // A reservation is space promised and never written: it reads as zeroes, like a hole.
      if (key.RawType != KeyExtent) {
        if (key.RawType is not (KeyReservation or KeyDeleted or KeyWhiteout))
          unreadable.TryAdd(key.Position.Inode, $"extent key type {key.RawType} is not read here");
        continue;
      }

      if (!TryLocate(core, key, out var sector, out var reason)) {
        unreadable.TryAdd(key.Position.Inode, reason);
        continue;
      }

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

      foreach (var (name, target, type) in list) {
        var full = path.Length == 0 ? name : path + "/" + name;
        if (type == DtDir) {
          if (!seen.Add(target)) continue;
          directories.Add(full);
          pending.Enqueue((target, full));
          continue;
        }

        var size = sizes.GetValueOrDefault(target, 0L);
        var runs = extents.TryGetValue(target, out var found) ? found : [];
        var entry = new Entry(full, size, target, runs) { Unreadable = unreadable.GetValueOrDefault(target) };
        if (type == DtLnk && entry.Unreadable == null) {
          var bytes = this.Read(entry);
          var end = Array.IndexOf(bytes, (byte)0);
          entry = entry with { LinkTarget = Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end) };
        }
        this._entries.Add(entry);
      }
    }

    this._entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    directories.Sort(StringComparer.Ordinal);
    return problems;
  }

  /// <summary>
  /// Where an extent's live data starts: its pointer, advanced by the checksummed
  /// region's offset when the extent is the live part of a larger one.
  /// </summary>
  private static bool TryLocate(BcacheFsCoreVolume core, BcacheFsRawKey key, out long sector, out string reason) {
    sector = -1;
    if (!BcacheFsExtentCodec.TryParseEntries(key.Value, core.Superblock, out var entries, out reason))
      return false;

    uint offset = 0;
    foreach (var entry in entries) {
      switch (entry.KnownType) {
        case BcacheFsExtentEntryType.Crc32 or BcacheFsExtentEntryType.Crc64 or BcacheFsExtentEntryType.Crc128:
          if (!BcacheFsExtentCodec.TryReadExtentCrc(entry, out var crc, out reason)) return false;
          if (crc!.Compressed) {
            reason = $"extent compressed with {crc.CompressionType} is not read here";
            return false;
          }
          if (crc.Nonce != 0 || crc.ChecksumType is BcacheFsChecksumType.ChaCha20Poly1305_80 or BcacheFsChecksumType.ChaCha20Poly1305_128) {
            reason = "encrypted extent is not read here";
            return false;
          }
          offset = crc.Offset;
          break;
        case BcacheFsExtentEntryType.Pointer: {
          var word = BinaryPrimitives.ReadUInt64LittleEndian(entry.RawBytes);
          if ((word & (1UL << 1)) != 0) break;                                // a cached copy is not the data
          sector = PointerSector(word) + offset;
          reason = string.Empty;
          return true;
        }
      }
    }

    reason = "extent carries no dirty pointer";
    return false;
  }

  private static string ReadName(ReadOnlySpan<byte> source) {
    // The name runs to the end of the value, less whatever zero padding rounded it
    // out to a whole number of words.
    var end = source.Length;
    while (end > 0 && source[end - 1] == 0) --end;
    return end == 0 ? string.Empty : Encoding.UTF8.GetString(source[..end]);
  }

  /// <summary>Writes one file's bytes to <paramref name="output" />.</summary>
  /// <remarks>
  /// A stretch of the file no extent covers is a hole and reads as zeroes, as it
  /// does through the kernel.
  /// </remarks>
  public void ExtractTo(Entry entry, Stream output) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(output);
    if (entry.Unreadable != null)
      throw new NotSupportedException($"bcachefs: '{entry.Name}': {entry.Unreadable}.");

    var buffer = new byte[BucketBytes];
    var written = 0L;

    void Zeroes(long upTo) {
      Array.Clear(buffer);
      while (written < upTo) {
        var chunk = (int)Math.Min(buffer.Length, upTo - written);
        output.Write(buffer, 0, chunk);
        written += chunk;
      }
    }

    foreach (var extent in entry.Extents) {
      if (written >= entry.Size) break;
      if (extent.FileOffset > written) Zeroes(Math.Min(extent.FileOffset, entry.Size));

      var skip = written - extent.FileOffset;
      var want = Math.Min((long)extent.Sectors * SectorSize - skip, entry.Size - written);
      if (want <= 0) continue;
      this._stream.Position = extent.FirstSector * SectorSize + skip;
      while (want > 0) {
        var chunk = (int)Math.Min(buffer.Length, want);
        this._stream.ReadExactly(buffer, 0, chunk);
        output.Write(buffer, 0, chunk);
        want -= chunk;
        written += chunk;
      }
    }

    Zeroes(entry.Size);
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
