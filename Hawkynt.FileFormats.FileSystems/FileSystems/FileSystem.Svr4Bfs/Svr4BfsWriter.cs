using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Svr4Bfs;

/// <summary>
/// Builds a fresh SVR4 / UnixWare Boot File System volume in the shape <c>mkfs.bfs</c>
/// produces — superblock, inode area, a root directory with <c>.</c> and <c>..</c> —
/// with the given files laid out contiguously behind the root directory, as the Linux
/// <c>bfs</c> driver itself allocates them.
/// </summary>
/// <remarks>
/// BFS has a single, flat directory and 14-byte names; a name that cannot be stored is
/// refused with <see cref="NotSupportedException"/> rather than shortened.
/// </remarks>
public sealed class Svr4BfsWriter {
  /// <summary>Fewest inodes a volume gets — the count <c>mkfs.bfs</c> gives a small volume.</summary>
  public const int MinimumInodes = 48;

  /// <summary>Inode slots and directory slots left free beyond the files written, for later adds.</summary>
  public const int SpareInodes = 16;

  /// <summary>Largest volume the 32-bit <c>s_end</c> byte offset can describe.</summary>
  public const long MaximumVolumeSize = 1L << 32;

  private readonly List<(byte[] Name, string Text, byte[] Data, ArchiveEntryMetadata? Meta)> _files = [];

  /// <summary>Six-character volume name (<c>s_volume</c>); padded with spaces.</summary>
  public string VolumeLabel { get; set; } = "";

  /// <summary>Six-character file-system name (<c>s_fsname</c>); padded with spaces.</summary>
  public string FsName { get; set; } = "";

  /// <summary>Inode count; null picks enough for the files plus <see cref="SpareInodes"/>.</summary>
  public int? InodeCount { get; set; }

  /// <summary>Volume size in bytes; null picks the used size plus a quarter (at least 64 KiB of free space).</summary>
  public long? VolumeSize { get; set; }

  /// <summary>Timestamp used where an input carries none; defaults to now.</summary>
  public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

  /// <summary>Queues a root-level file.</summary>
  public void AddFile(string name, byte[] data, ArchiveEntryMetadata? metadata = null) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var encoded = Svr4BfsLayout.EncodeName(name)
      ?? throw new NotSupportedException($"SVR4 BFS has one flat directory with names of at most {Svr4BfsLayout.NameMax} bytes; '{name}' cannot be stored.");
    if (this._files.Any(f => f.Text == name))
      throw new NotSupportedException($"SVR4 BFS cannot hold two files named '{name}'.");
    this._files.Add((encoded, name, data, metadata));
  }

  /// <summary>Builds a volume from <paramref name="files"/> with default geometry.</summary>
  public static byte[] Build(params IEnumerable<(string Name, byte[] Data)> files) {
    var w = new Svr4BfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  /// <summary>Writes the volume to <paramref name="output"/> from its current position.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    var inodes = this.InodeCount ?? Math.Max(MinimumInodes, this._files.Count + 1 + SpareInodes);
    inodes = (inodes + Svr4BfsLayout.InodesPerBlock - 1) / Svr4BfsLayout.InodesPerBlock * Svr4BfsLayout.InodesPerBlock;
    if (inodes < this._files.Count + 1 || inodes + 1 > ushort.MaxValue)
      throw new NotSupportedException($"SVR4 BFS inode count {inodes} cannot hold {this._files.Count} files and the root directory.");

    var inodeBlocks = (uint)(inodes / Svr4BfsLayout.InodesPerBlock);
    var start = (1 + inodeBlocks) * Svr4BfsLayout.BlockSize;
    // The kernel never grows a directory's block run, so the root directory gets a slot
    // for every inode up front ("." and ".." included); mkfs.bfs gives 48 inodes two blocks.
    var dirSlots = inodes + 1;
    var dirBlocks = (uint)Math.Max(2, (dirSlots * Svr4BfsLayout.DirentSize + Svr4BfsLayout.BlockSize - 1) / Svr4BfsLayout.BlockSize);
    var rootStart = start / Svr4BfsLayout.BlockSize;

    var next = rootStart + dirBlocks;
    var layout = new List<(uint Start, uint Blocks)>();
    foreach (var f in this._files) {
      var blocks = Svr4BfsInode.BlocksFor(f.Data.LongLength);
      layout.Add((blocks == 0 ? 0 : next, blocks));
      next += blocks;
    }

    long usedBytes = (long)next * Svr4BfsLayout.BlockSize;
    var size = this.VolumeSize ?? Math.Max(usedBytes + usedBytes / 4, usedBytes + 64 * 1024);
    size = (size + Svr4BfsLayout.BlockSize - 1) / Svr4BfsLayout.BlockSize * Svr4BfsLayout.BlockSize;
    if (size < usedBytes)
      throw new NotSupportedException($"SVR4 BFS volume of {size} bytes cannot hold {usedBytes} bytes of metadata and files.");
    if (size > MaximumVolumeSize)
      throw new NotSupportedException("SVR4 BFS addresses its volume with 32-bit byte offsets; the files need more than 4 GiB.");

    var now = ToStamp(this.Now);
    var sb = new Svr4BfsSuperblock(start, (uint)(size - 1),
      Svr4BfsLayout.NoCompaction, Svr4BfsLayout.NoCompaction, Svr4BfsLayout.NoCompaction, Svr4BfsLayout.NoCompaction,
      this.FsName, this.VolumeLabel);

    var meta = new byte[(long)(rootStart + dirBlocks) * Svr4BfsLayout.BlockSize];
    sb.WriteTo(meta);

    var dirSize = (this._files.Count + 2) * Svr4BfsLayout.DirentSize;
    // Root: mode drwxr-xr-x, owner root, group 1 ("other") and two links, as mkfs.bfs writes it.
    new Svr4BfsInode((ushort)Svr4BfsLayout.RootIno, rootStart, rootStart + dirBlocks - 1,
      Svr4BfsInode.EndOffsetFor(rootStart, dirSize), Svr4BfsLayout.VDir, 0x41ED, 0, 1, 2, now, now, now)
      .WriteTo(meta.AsSpan((int)Svr4BfsLayout.InodeOffset(Svr4BfsLayout.RootIno), Svr4BfsLayout.InodeSize));

    var dir = meta.AsSpan((int)(rootStart * Svr4BfsLayout.BlockSize));
    WriteDirent(dir, (ushort)Svr4BfsLayout.RootIno, "."u8);
    WriteDirent(dir[Svr4BfsLayout.DirentSize..], (ushort)Svr4BfsLayout.RootIno, ".."u8);

    for (var i = 0; i < this._files.Count; ++i) {
      var (name, _, data, m) = this._files[i];
      var ino = (ushort)(Svr4BfsLayout.RootIno + 1 + i);
      var (fileStart, blocks) = layout[i];
      var mtime = m?.LastWriteTimeUtc is { } w ? ToStamp(w) : now;
      var atime = m?.LastAccessTimeUtc is { } a ? ToStamp(a) : mtime;
      var ctime = m?.StatusChangeTimeUtc is { } c ? ToStamp(c) : mtime;
      var mode = 0x8000u | (m?.UnixMode is { } um ? um & 0xFFFu : 0x1A4u);
      new Svr4BfsInode(ino, fileStart, blocks == 0 ? 0 : fileStart + blocks - 1,
        Svr4BfsInode.EndOffsetFor(fileStart, data.LongLength), Svr4BfsLayout.VReg, mode,
        m?.UnixUserId ?? 0, m?.UnixGroupId ?? 0, 1, atime, mtime, ctime)
        .WriteTo(meta.AsSpan((int)Svr4BfsLayout.InodeOffset(ino), Svr4BfsLayout.InodeSize));
      WriteDirent(dir[((i + 2) * Svr4BfsLayout.DirentSize)..], ino, name);
    }

    output.Write(meta);
    var pad = new byte[Svr4BfsLayout.BlockSize];
    foreach (var f in this._files) {
      output.Write(f.Data);
      var tail = (int)(f.Data.LongLength % Svr4BfsLayout.BlockSize);
      if (tail != 0) output.Write(pad, 0, Svr4BfsLayout.BlockSize - tail);
    }

    // Free space to the end of the volume, zeroed.
    var remaining = size - usedBytes;
    var zero = new byte[64 * 1024];
    while (remaining > 0) {
      var n = (int)Math.Min(remaining, zero.Length);
      output.Write(zero, 0, n);
      remaining -= n;
    }
  }

  internal static void WriteDirent(Span<byte> slot, ushort ino, ReadOnlySpan<byte> name) {
    slot[..Svr4BfsLayout.DirentSize].Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(slot, ino);
    name.CopyTo(slot.Slice(2, Svr4BfsLayout.NameMax));
  }

  internal static uint ToStamp(DateTimeOffset t)
    => (uint)Math.Clamp(t.ToUnixTimeSeconds(), 0, uint.MaxValue);
}
