#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Reiser4;

/// <summary>
/// Reads a Reiser4 image: the master superblock's label, UUID and block size, and every object of
/// the node40/node41 tree after the journal is replayed — directories, regular files whose bodies
/// are extents, tails or compressed clusters (holes included), symbolic links, device nodes, FIFOs,
/// sockets and hard links. The older workbench payload directory remains a compatibility fallback
/// for images written before native tree items became authoritative.
/// </summary>
public sealed class Reiser4Reader : IDisposable {

  /// <summary>Byte offset of the master superblock: block 16 at a 4 KB block size.</summary>
  public const long MasterOffset = 65536;

  private static readonly byte[] MasterMagic = "ReIsEr4"u8.ToArray();

  private readonly ImageAccessor _image;
  private readonly Reiser4Volume? _volume;
  private readonly List<Entry> _entries = [];

  /// <summary>True when the image carries a valid Reiser4 master superblock.</summary>
  public bool Valid { get; }

  /// <summary>Filesystem block size from the master superblock.</summary>
  public int BlockSize { get; } = Reiser4Writer.BlockSize;

  /// <summary>Volume label from the master superblock.</summary>
  public string Label { get; } = "";

  /// <summary>Volume UUID from the master superblock, as hex.</summary>
  public string UuidHex { get; } = "";

  /// <summary>Files, directories, links and special files found in the native tree, or files in the legacy payload directory.</summary>
  public IReadOnlyList<Entry> Entries => this._entries;

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._image.Length;

  /// <summary>True when the complete native namespace was decoded.</summary>
  public bool NativeTreeValid { get; }

  /// <summary>Why the native tree was not decoded, when it was not.</summary>
  public string? NativeProblem { get; }

  /// <summary>Journal transactions replayed before the tree was read (0 when the journal was clean).</summary>
  public int ReplayedTransactions { get; }

  /// <summary>Root stat-data, including inherited plugin settings.</summary>
  public FileMetadata? RootMetadata { get; }

  internal IReadOnlyList<ulong> NativeNodeBlocks { get; } = [];

  internal Reiser4Volume? Volume => this.NativeTreeValid ? this._volume : null;

  internal readonly record struct NativeRun(ulong Start, ulong Width);

  /// <summary>What kind of object an entry names.</summary>
  public enum EntryKind {
    /// <summary>A regular file.</summary>
    File,
    /// <summary>A directory.</summary>
    Directory,
    /// <summary>A symbolic link; <see cref="Entry.LinkTarget" /> holds its target.</summary>
    Symlink,
    /// <summary>A character device; <see cref="Entry.Device" /> holds its number.</summary>
    CharacterDevice,
    /// <summary>A block device; <see cref="Entry.Device" /> holds its number.</summary>
    BlockDevice,
    /// <summary>A named pipe.</summary>
    Fifo,
    /// <summary>A socket.</summary>
    Socket,
  }

  /// <summary>Stat-data fields which can be represented by the current writer.</summary>
  public sealed record FileMetadata(
      long Size, ushort Mode, uint LinkCount, uint UserId, uint GroupId,
      uint ModifiedTime, uint AccessedTime, uint ChangedTime,
      byte[] RawStatData) {
    /// <summary>Stable filesystem object identity.</summary>
    public ulong ObjectId { get; init; }
    /// <summary>Locality of the object stat key.</summary>
    public ulong StatLocality { get; init; }
    /// <summary>Ordering word of the object stat key (the kernel derives it from the object's first name).</summary>
    public ulong StatOrdering { get; init; }

    /// <summary>Last-write timestamp, when the seconds value is representable.</summary>
    public DateTime? LastModified => ModifiedTime == 0
      ? null
      : DateTimeOffset.FromUnixTimeSeconds(ModifiedTime).UtcDateTime;
  }

  /// <summary>One filesystem object and its physical extents.</summary>
  public sealed record Entry(string Name, ulong FirstBlock, long Size) {
    internal IReadOnlyList<NativeRun>? NativeRuns { get; init; }
    internal Reiser4Object? Native { get; init; }
    /// <summary>True when this entry is a directory.</summary>
    public bool IsDirectory { get; init; }
    /// <summary>What kind of object this entry names.</summary>
    public EntryKind Kind { get; init; }
    /// <summary>The target of a symbolic link.</summary>
    public string? LinkTarget { get; init; }
    /// <summary>The device number of a device node, as the kernel encodes it (major &lt;&lt; 20 | minor).</summary>
    public ulong Device { get; init; }
    /// <summary>The path this entry is a further name of, when the object has more than one name.</summary>
    public string? HardLinkOf { get; init; }
    /// <summary>How the body of a regular file is stored: "extent", "tail", "ctail" or "" for none.</summary>
    public string Body { get; init; } = "";
    /// <summary>On-disk stat-data fields retained for metadata-preserving rebuilds.</summary>
    public FileMetadata? Metadata { get; init; }
  }

  /// <summary>Initializes a new instance of <see cref="Reiser4Reader"/>.</summary>
  public Reiser4Reader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    this._image = new ImageAccessor(stream, leaveOpen: true);
    this._leaveOpen = leaveOpen;
    this._stream = stream;
    if (this._image.Length < MasterOffset + Reiser4Writer.BlockSize) return;

    var master = this._image.Read(MasterOffset, Reiser4Writer.BlockSize);
    if (!master.AsSpan(0, MasterMagic.Length).SequenceEqual(MasterMagic)) return;
    this.Valid = true;

    var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(master.AsSpan(18, 2));
    if (blockSize is >= 512 and <= 8192) this.BlockSize = blockSize;
    this.UuidHex = Convert.ToHexString(master.AsSpan(20, 16));
    this.Label = ReadCString(master.AsSpan(36, 16));

    // Native metadata is authoritative. The private payload directory is kept
    // only so old workbench images remain readable while they are migrated.
    this._volume = new Reiser4Volume(stream, leaveOpen: true);
    if (this._volume.TreeValid) {
      this.NativeTreeValid = true;
      this.ReplayedTransactions = this._volume.ReplayedTransactions;
      this.NativeNodeBlocks = [.. this._volume.NodeBlocks];
      this.RootMetadata = ToMetadata(this._volume.Root!);
      this.BuildEntries(this._volume);
      return;
    }
    this.NativeProblem = this._volume.Problem;

    if (!master.AsSpan(Reiser4Writer.MasterPayloadMarkerOff, Reiser4Writer.PayloadMarker.Length)
        .SequenceEqual(Reiser4Writer.PayloadMarker))
      return;

    var dirBlock = BinaryPrimitives.ReadUInt64LittleEndian(
      master.AsSpan(Reiser4Writer.MasterPayloadDirOff, 8));
    this.ReadLegacyDirectory(dirBlock);
  }

  private readonly bool _leaveOpen;
  private readonly Stream _stream;

  private void BuildEntries(Reiser4Volume volume) {
    var firstName = new Dictionary<Reiser4Key, string>();
    foreach (var (path, _, obj, _) in volume.Paths) {
      var metadata = ToMetadata(obj);
      var stat = obj.Stat;
      string? linkOf = null;
      if (!obj.IsDirectory) {
        if (firstName.TryGetValue(obj.StatKey, out var first)) linkOf = first;
        else firstName[obj.StatKey] = path;
      }
      var kind = stat.Kind switch {
        Reiser4Stat.Directory => EntryKind.Directory,
        Reiser4Stat.Symlink => EntryKind.Symlink,
        Reiser4Stat.CharDevice => EntryKind.CharacterDevice,
        Reiser4Stat.BlockDevice => EntryKind.BlockDevice,
        Reiser4Stat.Fifo => EntryKind.Fifo,
        Reiser4Stat.Socket => EntryKind.Socket,
        _ => EntryKind.File,
      };
      if (kind == EntryKind.Directory) {
        this._entries.Add(new Entry(path, 0, 0) { IsDirectory = true, Kind = kind, Metadata = metadata, Native = obj });
        continue;
      }
      if (kind != EntryKind.File) {
        this._entries.Add(new Entry(path, 0, 0) {
          Kind = kind, Metadata = metadata, Native = obj, HardLinkOf = linkOf,
          LinkTarget = stat.SymlinkTarget is { } target ? Reiser4Volume.DecodeName(target) : null,
          Device = kind is EntryKind.CharacterDevice or EntryKind.BlockDevice ? stat.BytesOrDevice : 0,
        });
        continue;
      }
      var body = obj.Body.Count == 0 ? "" : obj.Body[0].Plugin switch {
        Reiser4ItemPlugin.Extent => "extent",
        Reiser4ItemPlugin.Tail => "tail",
        _ => "ctail",
      };
      var runs = body == "extent"
        ? volume.ExtentUnits(obj).Where(static u => u.Start != 0).Select(static u => new NativeRun(u.Start, u.Width)).ToArray()
        : [];
      this._entries.Add(new Entry(path, runs.Length == 0 ? 0 : runs[0].Start, (long)stat.Size) {
        Kind = kind, Metadata = metadata, Native = obj, HardLinkOf = linkOf, Body = body,
        NativeRuns = runs,
      });
    }
  }

  private static FileMetadata ToMetadata(Reiser4Object obj) {
    var stat = obj.Stat;
    return new FileMetadata(checked((long)stat.Size), stat.Mode, stat.Links, stat.UserId, stat.GroupId,
      stat.ModifyTime, stat.AccessTime, stat.ChangeTime, stat.Raw.ToArray()) {
      ObjectId = obj.ObjectId, StatLocality = obj.StatKey.Locality, StatOrdering = obj.StatKey.Ordering,
    };
  }

  /// <summary>Reads a file's contents. Only valid below the array limit.</summary>
  public byte[] Extract(Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size > Array.MaxLength)
      throw new IOException(
        $"Reiser4: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    using var buffer = new MemoryStream();
    this.ExtractTo(entry, buffer);
    return buffer.ToArray();
  }

  /// <summary>Writes <paramref name="entry"/>'s contents into <paramref name="destination"/>.</summary>
  public long ExtractTo(Entry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory || entry.Kind != EntryKind.File) return 0;
    if (entry.Size <= 0) return 0;

    if (entry.Native is { } native && this._volume is { } volume)
      return volume.ExtractTo(native, destination);

    var blocksPerBitmap = Reiser4Writer.BlocksPerBitmap;
    var block = entry.FirstBlock;
    long written = 0;
    while (written < entry.Size) {
      while (IsBitmapBlock(block, blocksPerBitmap)) ++block;
      var offset = (long)block * this.BlockSize;
      if (offset < 0 || offset >= this._image.Length) break;
      var take = (int)Math.Min(Math.Min(this.BlockSize, entry.Size - written),
        this._image.Length - offset);
      if (take <= 0) break;
      this._image.CopyTo(offset, destination, take);
      written += take;
      ++block;
    }
    return written;
  }

  /// <summary>Where an entry's bytes live as physical byte runs (extent data only; tails and clusters live in tree nodes).</summary>
  public IEnumerable<(long Offset, long Length)> EnumerateRuns(Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size <= 0) yield break;

    if (entry.Native is { } native && this._volume is { } volume) {
      foreach (var run in volume.DataRuns(native)) yield return run;
      yield break;
    }

    var blocksPerBitmap = Reiser4Writer.BlocksPerBitmap;
    var block = entry.FirstBlock;
    long remainingLegacy = entry.Size;
    while (remainingLegacy > 0) {
      while (IsBitmapBlock(block, blocksPerBitmap)) ++block;
      var start = (long)block * this.BlockSize;
      if (start < 0 || start >= this._image.Length) yield break;

      long run = 0;
      while (remainingLegacy - run > 0 && !IsBitmapBlock(block, blocksPerBitmap)) {
        var take = Math.Min((long)this.BlockSize, remainingLegacy - run);
        take = Math.Min(take, this._image.Length - (start + run));
        if (take <= 0) break;
        run += take;
        ++block;
      }
      if (run <= 0) yield break;
      yield return (start, run);
      remainingLegacy -= run;
    }
  }

  private void ReadLegacyDirectory(ulong firstBlock) {
    var visited = new HashSet<ulong>();
    var block = firstBlock;
    while (block != 0 && visited.Add(block)) {
      var offset = (long)block * this.BlockSize;
      if (offset < 0 || offset + this.BlockSize > this._image.Length) break;
      var buf = this._image.Read(offset, this.BlockSize);
      if (!buf.AsSpan(0, Reiser4Writer.DirMagic.Length).SequenceEqual(Reiser4Writer.DirMagic)) break;

      var next = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(8, 8));
      var count = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16, 4));
      var capacity = (this.BlockSize - Reiser4Writer.DirHeadSize) / Reiser4Writer.DirEntrySize;
      for (var i = 0; i < count && i < capacity; ++i) {
        var o = Reiser4Writer.DirHeadSize + i * Reiser4Writer.DirEntrySize;
        var name = ReadCString(buf.AsSpan(o, Reiser4Writer.DirNameLength));
        if (name.Length == 0) continue;
        var first = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(o + Reiser4Writer.DirNameLength, 8));
        var size = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(o + Reiser4Writer.DirNameLength + 8, 8));
        if (size < 0) continue;
        this._entries.Add(new Entry(name, first, size));
      }
      block = next;
    }
  }

  private static bool IsBitmapBlock(ulong block, ulong blocksPerBitmap)
    => block == 18 || (block != 0 && block % blocksPerBitmap == 0);

  private static string ReadCString(ReadOnlySpan<byte> span) {
    var n = span.IndexOf((byte)0);
    if (n < 0) n = span.Length;
    return n == 0 ? "" : Encoding.UTF8.GetString(span[..n]);
  }

  /// <summary>Releases resources held by this instance.</summary>
  public void Dispose() {
    this._volume?.Dispose();
    this._image.Dispose();
    if (!this._leaveOpen) this._stream.Dispose();
  }
}
