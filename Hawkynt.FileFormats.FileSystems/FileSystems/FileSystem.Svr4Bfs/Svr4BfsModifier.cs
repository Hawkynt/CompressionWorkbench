using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Svr4Bfs;

/// <summary>
/// Edits an SVR4 BFS volume in place, the way the Linux <c>bfs</c> driver does: a new
/// file takes a free inode slot, a free root-directory slot and one contiguous run of
/// free blocks; a removed name clears its directory slot and, with the last link, its
/// inode and data blocks.
/// </summary>
/// <remarks>
/// Every check runs before the first byte is written, so a refused edit
/// (<see cref="NotSupportedException"/>) leaves the image byte-identical. The volume
/// size never changes: a file that finds no contiguous free run is refused.
/// </remarks>
public static class Svr4BfsModifier {

  /// <summary>Adds <paramref name="name"/>, replacing an existing file of that name.</summary>
  public static void AddFile(Stream image, string name, byte[] data, ArchiveEntryMetadata? metadata = null, DateTimeOffset? now = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var encoded = Svr4BfsLayout.EncodeName(name)
      ?? throw new NotSupportedException($"SVR4 BFS has one flat directory with names of at most {Svr4BfsLayout.NameMax} bytes; '{name}' cannot be stored.");

    var reader = Open(image);
    var stamp = Svr4BfsWriter.ToStamp(now ?? DateTimeOffset.UtcNow);
    var existing = reader.Entries.FirstOrDefault(e => e.Name == name);

    // Blocks, inode and directory slot the replaced name gives back count as free for
    // the new file, so a same-size replace always fits.
    var releasesInode = existing is not null && existing.Inode.Nlink <= 1;
    var blocks = Svr4BfsInode.BlocksFor(data.LongLength);
    uint start = 0;
    if (blocks > 0)
      start = FindRun(reader, blocks, releasesInode ? existing!.Ino : -1)
        ?? throw new NotSupportedException($"SVR4 BFS volume has no run of {blocks} contiguous free blocks for '{name}'; files are never split.");

    var ino = releasesInode ? existing!.Ino : FindFreeInode(reader)
      ?? throw new NotSupportedException("SVR4 BFS volume has no free inode.");
    var direntOffset = existing?.DirentOffset ?? FindFreeDirent(reader, image)
      ?? throw new NotSupportedException("SVR4 BFS root directory has no free slot; its block run is fixed.");

    // ── commit ──
    if (existing is not null) {
      if (releasesInode) WipeBlocks(image, existing.Inode);
      else DropLink(image, existing.Inode);
    }

    if (blocks > 0) {
      image.Position = (long)start * Svr4BfsLayout.BlockSize;
      image.Write(data);
      var tail = (int)(data.LongLength % Svr4BfsLayout.BlockSize);
      if (tail != 0) image.Write(new byte[Svr4BfsLayout.BlockSize - tail]);
    }

    var mtime = metadata?.LastWriteTimeUtc is { } w ? Svr4BfsWriter.ToStamp(w) : stamp;
    var mode = 0x8000u | (metadata?.UnixMode is { } um ? um & 0xFFFu : 0x1A4u);
    WriteInode(image, new((ushort)ino, start, blocks == 0 ? 0 : start + blocks - 1,
      Svr4BfsInode.EndOffsetFor(start, data.LongLength), Svr4BfsLayout.VReg, mode,
      metadata?.UnixUserId ?? 0, metadata?.UnixGroupId ?? 0, 1,
      metadata?.LastAccessTimeUtc is { } a ? Svr4BfsWriter.ToStamp(a) : mtime, mtime,
      metadata?.StatusChangeTimeUtc is { } c ? Svr4BfsWriter.ToStamp(c) : stamp));

    var slot = new byte[Svr4BfsLayout.DirentSize];
    Svr4BfsWriter.WriteDirent(slot, (ushort)ino, encoded);
    image.Position = direntOffset;
    image.Write(slot);

    // The directory's size covers up to its highest used slot, as the kernel keeps it.
    var root = reader.Root;
    var dirBase = (long)root.StartBlock * Svr4BfsLayout.BlockSize;
    var size = Math.Max(root.Size, direntOffset - dirBase + Svr4BfsLayout.DirentSize);
    WriteInode(image, root with { EndOffset = Svr4BfsInode.EndOffsetFor(root.StartBlock, size), Mtime = stamp, Ctime = stamp });
  }

  /// <summary>Removes <paramref name="name"/>; returns false when the root directory has no such name.</summary>
  public static bool RemoveFile(Stream image, string name, DateTimeOffset? now = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    var reader = Open(image);
    var existing = reader.Entries.FirstOrDefault(e => e.Name == name);
    if (existing is null) return false;

    image.Position = existing.DirentOffset;
    image.Write(new byte[Svr4BfsLayout.DirentSize]);
    if (existing.Inode.Nlink <= 1) WipeBlocks(image, existing.Inode);
    else DropLink(image, existing.Inode);

    var stamp = Svr4BfsWriter.ToStamp(now ?? DateTimeOffset.UtcNow);
    WriteInode(image, reader.Root with { Mtime = stamp, Ctime = stamp });
    return true;
  }

  private static Svr4BfsReader Open(Stream image) {
    if (!image.CanSeek || !image.CanWrite)
      throw new NotSupportedException("SVR4 BFS edits need a seekable, writable image.");
    var reader = new Svr4BfsReader(image);
    if (!reader.Superblock.IsClean)
      throw new NotSupportedException("SVR4 BFS volume records an interrupted compaction (s_from/s_to set); it is not edited.");
    if ((long)reader.Superblock.End + 1 > image.Length)
      throw new NotSupportedException("SVR4 BFS superblock describes a volume larger than the image; it is not edited.");
    // An inode whose extent is inconsistent may own blocks nothing else can account for;
    // allocating around it would be guesswork.
    foreach (var inode in reader.Inodes)
      if (!inode.IsFree && !reader.IsReadable(inode))
        throw new NotSupportedException($"SVR4 BFS inode {inode.Ino} names blocks outside the volume; it is not edited.");
    return reader;
  }

  /// <summary>First-fit run of <paramref name="blocks"/> free blocks; <paramref name="ignoreIno"/>'s blocks count as free.</summary>
  private static uint? FindRun(Svr4BfsReader reader, uint blocks, int ignoreIno) {
    var used = reader.Inodes
      .Where(i => !i.IsFree && !i.HasNoBlocks && i.Ino != ignoreIno)
      .Select(i => (i.StartBlock, i.EndBlock))
      .OrderBy(r => r.StartBlock)
      .ToList();
    long candidate = reader.Superblock.FirstDataBlock;
    foreach (var (s, e) in used) {
      if (s - candidate >= blocks) break;
      candidate = Math.Max(candidate, (long)e + 1);
    }
    return candidate + blocks - 1 <= reader.Superblock.LastBlock ? (uint)candidate : null;
  }

  private static int? FindFreeInode(Svr4BfsReader reader) {
    for (var ino = Svr4BfsLayout.RootIno + 1; ino <= reader.LastInode; ++ino)
      if (reader.Inodes[ino - Svr4BfsLayout.RootIno].IsFree) return ino;
    return null;
  }

  /// <summary>First free slot anywhere in the root directory's block run, as the kernel searches it.</summary>
  private static long? FindFreeDirent(Svr4BfsReader reader, Stream image) {
    var root = reader.Root;
    var blocks = (int)(root.EndBlock - root.StartBlock + 1);
    var raw = new byte[blocks * Svr4BfsLayout.BlockSize];
    var dirBase = (long)root.StartBlock * Svr4BfsLayout.BlockSize;
    image.Position = dirBase;
    image.ReadExactly(raw);
    for (var off = 0; off < raw.Length; off += Svr4BfsLayout.DirentSize)
      if (BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(off)) == 0) return dirBase + off;
    return null;
  }

  /// <summary>Zeroes a file's blocks and frees its inode slot.</summary>
  private static void WipeBlocks(Stream image, Svr4BfsInode inode) {
    if (!inode.HasNoBlocks) {
      var zero = new byte[Svr4BfsLayout.BlockSize];
      image.Position = (long)inode.StartBlock * Svr4BfsLayout.BlockSize;
      for (var b = inode.StartBlock; b <= inode.EndBlock; ++b) image.Write(zero);
    }
    image.Position = Svr4BfsLayout.InodeOffset(inode.Ino);
    image.Write(new byte[Svr4BfsLayout.InodeSize]);
  }

  private static void DropLink(Stream image, Svr4BfsInode inode)
    => WriteInode(image, inode with { Nlink = inode.Nlink - 1 });

  /// <summary>
  /// Rewrites an inode's fields, keeping the bytes the format leaves unused
  /// (<c>i_unused</c>, <c>i_padding</c>) exactly as they were on disk.
  /// </summary>
  private static void WriteInode(Stream image, Svr4BfsInode inode) {
    var offset = Svr4BfsLayout.InodeOffset(inode.Ino);
    var old = new byte[Svr4BfsLayout.InodeSize];
    image.Position = offset;
    image.ReadExactly(old);
    var raw = new byte[Svr4BfsLayout.InodeSize];
    inode.WriteTo(raw);
    old.AsSpan(2, 2).CopyTo(raw.AsSpan(2));
    old.AsSpan(48, 16).CopyTo(raw.AsSpan(48));
    image.Position = offset;
    image.Write(raw);
  }
}
