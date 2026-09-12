#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Nilfs2;

/// <summary>
/// Writes a kernel-mountable NILFS2 image. Emits the full single-checkpoint log
/// structure the Linux <c>nilfs2</c> driver needs to mount — a super root with
/// the DAT / cpfile / sufile inodes, a segment summary with the spec checksums,
/// an ifile holding the root directory inode, a DAT (disk-address-translation)
/// table, and a root directory carrying the user files — alongside the byte-
/// accurate, CRC-valid superblock pair. A real <c>mount -t nilfs2</c> mounts the
/// image and reads the files back (verified via the libguestfs appliance kernel).
/// </summary>
/// <remarks>
/// <para><b>Verified mountable.</b> The emitted image mounts under the real
/// kernel nilfs2 driver: the directory lists, the file contents read back, and
/// the kernel can write new files into it. This was confirmed segment-by-segment
/// against a real <c>mkfs.nilfs2</c> reference image and gated by a guestfish
/// mount + read-back test.</para>
///
/// <para><b>On-disk layout (4 KiB blocks shown; any legal block size works).</b></para>
/// <list type="bullet">
///   <item><description>0x000..0x3FF — boot sector area (zeroed).</description></item>
///   <item><description>0x400..0x7FF — primary NILFS2 superblock (magic 0x3434 at
///   +6, <c>s_rev_level == 2</c>, crc32_le-sealed <c>s_sum</c>, label at +0xA8).
///   <c>s_last_pseg</c> points at the committed log.</description></item>
///   <item><description>0x800.. — writer-private compact directory guarded by
///   <see cref="WriterMagic"/> (the writer magic) + i64 directory length + i64 payload
///   base + i64 payload length + (u32 name_len, name, u64 payload_offset,
///   u64 size) entries. This is read by <see cref="Nilfs2Reader"/> and mutated by
///   the in-place modifier; the kernel never looks here (it jumps straight to
///   <c>s_last_pseg</c>).</description></item>
///   <item><description>log region — a single partial segment at the first
///   segment boundary past the private directory: segment summary, payload blocks
///   (every directory of the tree, user-file data and the leaves of their maps,
///   ifile palloc blocks, DAT palloc blocks, cpfile, sufile) and the super root as
///   the last block of the segment.</description></item>
///   <item><description>payload region — the private directory's file bytes,
///   starting at the block after the log.</description></item>
///   <item><description>secondary superblock one block before EOF
///   (<c>dev_size - 4096</c>).</description></item>
/// </list>
///
/// <para><b>Why the log comes first.</b> The sufile is a single block, so it can
/// only describe <c>block_size / 16</c> segments. With the payload ahead of the
/// log, a volume of any size pushed the log into a segment the sufile could not
/// address. Keeping the log at the front bounds the sufile slot for every volume
/// size, and lets the payload be streamed rather than held in memory.</para>
///
/// <para><b>Scope.</b> Single checkpoint (cno=1), single partial segment. A file
/// of a few blocks is mapped by pointers written into its inode; a longer one by
/// a b-tree of one level, whose leaves the log carries and the address table
/// translates like any other block of the file. What bounds a volume now is the
/// height of that tree, which grows as the file does; the address table and the
/// inode file map themselves the same way and grow with it, the table across as
/// many allocation groups as it needs. Half a gigabyte in one file has been read
/// back under the kernel driver, as have twenty thousand files, and directories
/// nested several deep and hundreds of entries wide — a directory spans as many
/// blocks as its entries need, mapped from its inode like anything else. A name with a path in it makes the directories it implies, each
/// holding as many entries as fit one block. The writer-private directory carries
/// every file in full for the reader either way. Snapshots and multi-checkpoint
/// chains are out of scope.</para>
/// </remarks>
public sealed class Nilfs2Writer {

  /// <summary>
  /// Magic prefix that identifies the writer-private directory. Lets our reader
  /// pick up files without disturbing the kernel mount path (the kernel reads the
  /// superblock's <c>s_last_pseg</c> and never scans for this magic).
  /// </summary>
  /// <remarks>The value spells nothing: a marker that reads as words names whoever chose them.</remarks>
  internal static readonly byte[] WriterMagic =
    [0x8F, 0xD3, 0x1A, 0xE7, 0x05, 0xBC, 0x92, 0x14];

  /// <summary>
  /// Magic that prefixes each appended log-segment block written by
  /// <c>Nilfs2InPlaceModifier</c> (our snapshot-append mechanism — distinct from
  /// the kernel log). Each segment carries a u64 checkpoint number + a directory
  /// + a payload region; the reader merges all segments by highest-cno-per-name.
  /// </summary>
  internal static readonly byte[] SegmentMagic =
    [0xA6, 0x0E, 0xF1, 0x83, 0x1D, 0xC5, 0x7F, 0x9B];

  /// <summary>Where the writer-private directory begins.</summary>
  internal const int SegmentStart = 2048;

  /// <summary>
  /// Bytes of private-directory header ahead of the entries: magic, directory
  /// length, payload base and payload length.
  /// </summary>
  internal const int PrivateHeaderBytes = 8 + 8 + 8 + 8;

  /// <summary>Superblock offset on disk (NILFS2 spec).</summary>
  internal const int SuperblockOffsetOnDisk = 1024;

  /// <summary>Offset of <c>s_last_cno</c> field within the superblock.</summary>
  internal const int LastCnoFieldOffset = 0x38;

  /// <summary>Largest user file (in blocks) embedded into the kernel root dir.</summary>
  internal const int MaxKernelFileBlocks = 6; // pointers that fit in the inode itself.

  /// <summary>Children the b-tree root holds, the root living in the inode's map area.</summary>
  private const int BtreeRootChildren = (BmapBytes - BtreeNodeHeaderBytes) / 16;

  /// <summary>How many of those a tree is built to use.</summary>
  /// <remarks>
  /// One, though the root has room for two. A root naming two children reads back
  /// wrong under the kernel driver where a root naming one — over a node that names
  /// as many as it likes — reads back exactly; measured across files from a block
  /// to sixty-four megabytes. Growing a level instead costs one block and is what
  /// the driver's own trees do.
  /// </remarks>
  private const int BtreeRootUsableChildren = 1;

  /// <summary>Bytes of an inode given over to its map.</summary>
  /// <remarks>Seven pointers' worth, which is where the root of a tree lives.</remarks>
  private const int BmapBytes = 56;

  /// <summary>
  /// Bytes a b-tree node spends on itself before its keys begin.
  /// </summary>
  /// <remarks>
  /// The fields come to eight — a flag byte, a level, a count and a pad — and the
  /// keys begin eight bytes after that. Measured against a tree the kernel driver
  /// wrote into one of our own volumes: with the keys read eight bytes early every
  /// key but the first comes out shifted, which is a tree that reads back wrong
  /// rather than one that fails outright.
  /// </remarks>
  private const int BtreeNodeHeaderBytes = 16;
  private const byte BtreeNodeRootFlag = 0x01;
  private const byte BtreeLevelLeaf = 1;   // a node whose pointers are data blocks
  private const byte BtreeLevelRoot = 2;   // a node whose pointers are leaves

  /// <summary>Children one b-tree node in a block of its own holds.</summary>
  private static int BtreeNodeChildren(int blockSize) => (blockSize - BtreeNodeHeaderBytes) / 16;

  /// <summary>How many entries a node is filled to.</summary>
  /// <remarks>
  /// All of them. The earlier belief that the last slot had to stay free came from
  /// keys written eight bytes early, which made a full node overrun its own pointer
  /// array; with the header measured properly a node fills to its capacity, which
  /// is what the driver's own trees do.
  /// </remarks>
  private static int BtreeLeafFill(int blockSize) => BtreeNodeChildren(blockSize);

  // NILFS2 on-disk constants (cross-checked against fs/nilfs2/nilfs2_ondisk.h).
  private const int MinSegBlocks = 16;               // NILFS_SEG_MIN_BLOCKS.

  /// <summary>
  /// The largest block size a volume can use and still be mounted.
  /// </summary>
  /// <remarks>
  /// Linux sets a block device's block size to the filesystem's, and refuses any
  /// larger than a page. A NILFS2 volume with 8 KiB blocks is legal on paper and
  /// unmountable everywhere this writer runs, so it is never written.
  /// </remarks>
  private const int MaxMountableBlockSize = 4096;
  /// <summary>Segments kept free for the cleaner and for checkpoints to come.</summary>
  /// <remarks>
  /// Two, not more. A segment has to hold the whole log this writer commits, so a
  /// large volume has large segments, and counting spare room in segments then
  /// multiplies a volume of a hundred megabytes into one of two gigabytes.
  /// </remarks>
  private const int SpareSegments = 2;

  /// <summary>The smallest volume worth making, whatever the segment size works out to.</summary>
  private const long MinSpareBytes = 16L * 1024 * 1024;
  private const uint SegsumMagic = 0x1eaffa11;       // NILFS_SEGSUM_MAGIC.
  private const int SegsumHeaderBytes = 64;
  private const int InodeSize = 128;                 // NILFS_MIN_INODE_SIZE.
  private const int CheckpointSize = 192;
  private const int SegUsageSize = 16;
  private const int DatEntrySize = 32;
  private const int SuperRootBytes = 16 + InodeSize * 3; // sr header + 3 inodes = 400.
  private const ulong LiveEnd = 0xffffffffffffffffUL; // de_end == -1 -> entry is live.

  // Segment summary flags (NILFS_SS_*).
  private const ushort SsLogBgn = 0x0001, SsLogEnd = 0x0002, SsSr = 0x0004;
  // Special inode numbers (NILFS_*_INO).
  private const ulong RootIno = 2, DatIno = 3, CpfileIno = 4, SufileIno = 5, IfileIno = 6;
  private const int UserIno = 11;                    // NILFS_USER_INO — first user inode.
  // File modes.
  private const ushort SIfdir = 0x4000, SIfreg = 0x8000;

  private readonly List<(string Name, FilePayload Payload)> _files = [];

  // The volume's identity, the seed its checksums are salted with, and when it was
  // made. mkfs.nilfs2 draws the first two fresh and takes the third from the clock;
  // a volume that reports the same three every time says who wrote it.
  private byte[] _uuid = NewUuid();
  private uint _crcSeed = NewCrcSeed();
  private ulong _ctime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

  /// <summary>Fixes the volume's identity and creation time, for a build that has to come out the same twice.</summary>
  /// <param name="uuid">The volume's identity, sixteen bytes.</param>
  /// <param name="crcSeed">The seed the volume's checksums are salted with.</param>
  /// <param name="createdAt">When the volume claims it was made.</param>
  public void SetIdentity(ReadOnlySpan<byte> uuid, uint crcSeed, DateTimeOffset createdAt) {
    if (uuid.Length != 16)
      throw new ArgumentException("A NILFS2 volume identity is sixteen bytes.", nameof(uuid));

    this._uuid = uuid.ToArray();
    this._crcSeed = crcSeed;
    this._ctime = (ulong)createdAt.ToUnixTimeSeconds();
  }

  private static byte[] NewUuid() {
    var uuid = new byte[16];
    System.Security.Cryptography.RandomNumberGenerator.Fill(uuid);
    return uuid;
  }

  private static uint NewCrcSeed() {
    Span<byte> bytes = stackalloc byte[4];
    System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
    return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
  }

  /// <summary>Adds a file to the image.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name.Replace('\\', '/'), FilePayload.FromBytes(data)));
  }

  /// <summary>
  /// Adds a file whose bytes are copied straight from <paramref name="opener"/>
  /// into the image at write time, so a payload larger than memory — or than a
  /// byte[] — can be stored.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> opener) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(opener);
    ArgumentOutOfRangeException.ThrowIfNegative(size);
    this._files.Add((name.Replace('\\', '/'), FilePayload.FromStream(size, opener)));
  }

  /// <summary>
  /// Builds the NILFS2 image in memory. <paramref name="blockSize"/> must be a
  /// power of two in [1024, 65536]; <paramref name="volumeLabel"/> is written into
  /// the superblock's volume-label slot at +0xA8. Use
  /// <see cref="Build(Stream, int, string?)"/> for volumes past the array limit.
  /// </summary>
  public byte[] Build(int blockSize = 4096, string? volumeLabel = null) {
    using var buffer = new MemoryStream();
    this.Build(buffer, blockSize, volumeLabel);
    return buffer.ToArray();
  }

  /// <summary>Writes the assembled image to a stream.</summary>
  public void WriteTo(Stream output) => this.Build(output);

  /// <summary>
  /// Writes the NILFS2 image into <paramref name="output"/> from its current
  /// position. Only the metadata is assembled in memory; file payloads are
  /// copied through, so the volume may be arbitrarily large.
  /// </summary>
  public void Build(Stream output, int blockSize = 4096, string? volumeLabel = null) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek)
      throw new ArgumentException("Nilfs2 needs a seekable stream to place the tail superblock.", nameof(output));
    if (blockSize < 1024 || blockSize > 65536 || (blockSize & (blockSize - 1)) != 0)
      throw new ArgumentException("blockSize must be a power of two in [1024, 65536].", nameof(blockSize));

    // The single-block metadata structures (inode table, DAT entry table, root
    // directory) must each fit in one block for the mountable log. Raise the
    // block size to the smallest power of two that holds them; the superblock
    // records the actual size, so the image stays self-consistent and mountable.
    var minBlock = this.MinBlockSizeForLog();
    if (blockSize < minBlock) blockSize = minBlock;
    var logBlockSize = (uint)(System.Numerics.BitOperations.Log2((uint)blockSize) - 10);

    // ── writer-private directory (read by Nilfs2Reader) ─────────────────────
    var dirSize = this.ComputeDirectoryBytes();
    var payloadBytes = 0L;
    foreach (var (_, payload) in this._files) payloadBytes += payload.Size;

    // A partial segment has to fit inside one segment, and this writer commits the
    // whole volume as a single one — so how large a segment is follows from how
    // much the log holds, not the other way round.
    var segBlocks = Math.Max(MinSegBlocks, RoundUpToPowerOfTwo(this.PlanLog(0, blockSize).NBlocks));

    // The kernel log starts at the first segment boundary past the directory;
    // the payload follows the log, so the log's segment index — and with it the
    // sufile slot it needs — stays small however large the payload is.
    var segBytes = (long)segBlocks * blockSize;
    var dirEnd = SegmentStart + PrivateHeaderBytes + dirSize;
    var psegStart = (int)(((dirEnd + segBytes - 1) / segBytes) * segBlocks);
    if (psegStart < segBlocks) psegStart = segBlocks;

    // Plan the kernel log blocks (counts) so we can size the image up front.
    var plan = this.PlanLog(psegStart, blockSize);
    var payloadBase = (long)(psegStart + plan.NBlocks) * blockSize;

    // The kernel needs spare segments to commit new checkpoints, so the volume
    // must span several segments past the one holding the log (NILFS reserves
    // segments for the cleaner; mkfs.nilfs2's smallest accepted volume is a few
    // MiB). Size to at least MinSegments segments and one tail block for the
    // secondary superblock, rounded to whole blocks.
    var tailReserve = Math.Max(Nilfs2Superblock.SecondaryBackOffset, blockSize);
    // Spare room for the cleaner, counted in bytes rather than in segments: a log
    // large enough to need large segments would otherwise multiply out to a volume
    // of gigabytes for a file of a few.
    var spare = Math.Max(MinSpareBytes, SpareSegments * segBytes);
    var minImageBytes = Math.Max(spare, payloadBase + payloadBytes + tailReserve + spare / 2);
    // Round up to a whole number of segments so s_nsegments * blocks_per_segment
    // describes the device exactly.
    var imageBytes = ((minImageBytes + segBytes - 1) / segBytes) * segBytes;
    var totalBlocks = (ulong)(imageBytes / blockSize);
    var nSegments = Math.Max(1ul, totalBlocks / (ulong)segBlocks);

    // ── metadata prefix: private directory + kernel log + primary superblock ──
    var head = new SparseBlockImage(blockSize, payloadBase);
    this.WritePrivateDirectory(head, dirSize, payloadBase, payloadBytes);
    this.WriteKernelLog(head, plan, blockSize, nSegments, segBlocks);

    var freeBlocks = (nSegments - 1) * (ulong)segBlocks; // one segment consumed by the log.
    Nilfs2Superblock.Encode(
      head.At(Nilfs2Superblock.PrimaryOffset, Nilfs2Superblock.Size),
      logBlockSize, nSegments, (ulong)imageBytes, (uint)segBlocks,
      lastCno: 1, lastPseg: (ulong)psegStart, lastSeq: 0, freeBlocks: freeBlocks,
      ctime: this._ctime, state: Nilfs2Superblock.StateValid,
      crcSeed: this._crcSeed, uuid: this._uuid, volumeLabel: volumeLabel);

    var basePosition = output.Position;
    head.WriteTo(output);

    // ── payload region: copied through, never held ──────────────────────────
    foreach (var (name, payload) in this._files) {
      if (payload.Size <= 0) continue;
      using var source = payload.Open();
      var copied = CopyExactly(source, output, payload.Size);
      if (copied != payload.Size)
        throw new InvalidOperationException(
          $"Nilfs2: '{name}' was announced as {payload.Size:N0} bytes but only {copied:N0} could be read.");
    }

    // ── tail: padding to a whole segment + the secondary superblock ─────────
    output.SetLength(basePosition + imageBytes);
    var secondaryOffset = imageBytes - Nilfs2Superblock.SecondaryBackOffset;
    if (secondaryOffset >= payloadBase + payloadBytes) {
      var secondary = new byte[Nilfs2Superblock.Size];
      Nilfs2Superblock.Encode(
        secondary,
        logBlockSize, nSegments, (ulong)imageBytes, (uint)segBlocks,
        lastCno: 1, lastPseg: (ulong)psegStart, lastSeq: 0, freeBlocks: freeBlocks,
        ctime: this._ctime, state: Nilfs2Superblock.StateValid,
        crcSeed: this._crcSeed, uuid: this._uuid, volumeLabel: volumeLabel);
      output.Position = basePosition + secondaryOffset;
      output.Write(secondary);
    }
    output.Position = basePosition + imageBytes;
    output.Flush();
  }

  private static long CopyExactly(Stream source, Stream destination, long count) {
    var buffer = new byte[81920];
    var remaining = count;
    while (remaining > 0) {
      var want = (int)Math.Min(buffer.Length, remaining);
      var read = source.Read(buffer, 0, want);
      if (read <= 0) break;
      destination.Write(buffer, 0, read);
      remaining -= read;
    }
    return count - remaining;
  }

  /// <summary>
  /// Smallest legal block size (power of two in [1024, 65536]) whose single
  /// block can hold each of the metadata structures the mountable log keeps in
  /// one block: the ifile inode table, the DAT entry table, and the root
  /// directory. Files too large for a direct bmap are excluded (they live only
  /// in the writer-private directory).
  /// </summary>
  private static int RoundUpToPowerOfTwo(int value) {
    var result = 1;
    while (result < value) result <<= 1;
    return result;
  }

  private int MinBlockSizeForLog() {
    var nFiles = 0;
    var largest = 0L;
    // How much each directory of the tree needs for its own entries, since every
    // one of them has to fit in a block.
    var dirBytes = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = 16 + 16 };
    var known = new HashSet<string>(StringComparer.Ordinal) { "" };
    foreach (var (rawName, payload) in this._files) {
      var name = rawName.Replace('\\', '/').Trim('/');
      if (name.Length == 0) continue;

      ++nFiles;
      largest = Math.Max(largest, payload.Size);

      var cut = name.LastIndexOf('/');
      var parent = cut < 0 ? "" : name[..cut];
      for (var walk = parent; walk.Length > 0 && known.Add(walk);) {
        var up = walk.LastIndexOf('/');
        var owner = up < 0 ? "" : walk[..up];
        dirBytes[walk] = dirBytes.GetValueOrDefault(walk, 16 + 16);
        dirBytes[owner] = dirBytes.GetValueOrDefault(owner, 16 + 16)
          + DirRecLen(Encoding.UTF8.GetByteCount(up < 0 ? walk : walk[(up + 1)..]));
        walk = owner;
      }

      dirBytes[parent] = dirBytes.GetValueOrDefault(parent, 16 + 16)
        + DirRecLen(Encoding.UTF8.GetByteCount(cut < 0 ? name : name[(cut + 1)..]));
    }

    // A directory spans as many blocks as it needs, so its size no longer forces
    // the block size; the count of them still feeds the tables below.
    var rootDirBytes = 32;
    var nDirs = known.Count;

    var bs = 1024;
    while (bs < MaxMountableBlockSize) {
      var blocks = 0L;
      var leaves = 0L;
      foreach (var (rawName, payload) in this._files) {
        if (rawName.Replace('\\', '/').Trim('/').Length == 0) continue;

        var nblk = Math.Max(1L, (payload.Size + bs - 1) / bs);
        blocks += nblk;
        if (nblk > MaxKernelFileBlocks)
          leaves += (nblk + BtreeNodeChildren(bs) - 1) / BtreeNodeChildren(bs);
      }

      var inodeTableBytes = (UserIno + nFiles + nDirs) * InodeSize;
      // DAT entries are indexed by virtual block number, and every block the log
      // translates gets one: the root directory, each file's data and each leaf of
      // its map, three for the ifile, then the cpfile and the sufile.
      var datEntryBytes = (nDirs + 1 + blocks + leaves + 3 + 2 + 1) * DatEntrySize;
      var need = Math.Max(Math.Max(inodeTableBytes, datEntryBytes), rootDirBytes);

      if (need <= bs) break;

      bs <<= 1;
    }

    return bs;
  }

  private int ComputeDirectoryBytes() {
    var total = 0;
    foreach (var (name, _) in this._files) {
      var nameLen = Encoding.UTF8.GetByteCount(name);
      total += 4 + nameLen + 16;
    }
    return total;
  }

  private void WritePrivateDirectory(SparseBlockImage image, int dirSize, long payloadBase, long payloadBytes) {
    Span<byte> header = stackalloc byte[PrivateHeaderBytes];
    WriterMagic.CopyTo(header);
    BinaryPrimitives.WriteInt64LittleEndian(header[8..], dirSize);
    BinaryPrimitives.WriteInt64LittleEndian(header[16..], payloadBase);
    BinaryPrimitives.WriteInt64LittleEndian(header[24..], payloadBytes);
    image.Write(SegmentStart, header);

    var cursor = (long)SegmentStart + PrivateHeaderBytes;
    var payloadCursor = 0L;
    Span<byte> record = stackalloc byte[16];
    foreach (var (name, payload) in this._files) {
      var nameBytes = Encoding.UTF8.GetBytes(name);
      BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)nameBytes.Length);
      image.Write(cursor, record[..4]);
      image.Write(cursor + 4, nameBytes);
      BinaryPrimitives.WriteInt64LittleEndian(record, payloadCursor);
      BinaryPrimitives.WriteInt64LittleEndian(record[8..], payload.Size);
      image.Write(cursor + 4 + nameBytes.Length, record);
      cursor += 4 + nameBytes.Length + 16;
      payloadCursor += payload.Size;
    }
  }

  // ── kernel log ────────────────────────────────────────────────────────────

  /// <summary>
  /// The shape of a b-tree over <paramref name="items" /> entries: what each level
  /// holds, leaves first, each node given by the key it starts at and how many
  /// children it takes.
  /// </summary>
  /// <remarks>
  /// Levels are added until what remains fits the handful of pointers the root has
  /// room for in the inode. A level maps <c>fill</c> of the level below, so a tree
  /// two deep already reaches further than any volume this writer builds.
  /// </remarks>
  private static List<List<(long FirstKey, int Count)>> PlanBtree(long items, int fill) {
    var levels = new List<List<(long FirstKey, int Count)>>();
    var below = items;
    var span = 1L;   // entries of the file one child at this level stands for

    while (true) {
      var nodes = new List<(long FirstKey, int Count)>();
      for (long taken = 0; taken < below; taken += fill)
        nodes.Add((taken * span, (int)Math.Min(fill, below - taken)));

      levels.Add(nodes);
      if (nodes.Count <= BtreeRootUsableChildren) return levels;

      below = nodes.Count;
      span *= fill;
    }
  }

  /// <summary>
  /// How a palloc file — one indexed by entry number — is laid out in its own
  /// logical blocks.
  /// </summary>
  /// <remarks>
  /// A block of group descriptors, then for each group a bitmap block followed by
  /// the blocks of entries it accounts for. A group's bitmap is one block, so a
  /// group holds eight entries per byte of a block; past that the file simply has
  /// another group, and past a blockful of descriptors another descriptor block.
  /// </remarks>
  private readonly record struct Palloc(int EntriesPerBlock, int EntriesPerGroup,
                                        int BlocksPerGroup, int GroupsPerDescBlock) {

    internal static Palloc For(int blockSize, int entrySize) {
      var entriesPerBlock = blockSize / entrySize;
      var entriesPerGroup = blockSize * 8;
      return new(entriesPerBlock, entriesPerGroup,
                 (entriesPerGroup + entriesPerBlock - 1) / entriesPerBlock + 1,
                 blockSize / 4);
    }

    internal int Groups(int entries) => Math.Max(1, (entries + this.EntriesPerGroup - 1) / this.EntriesPerGroup);

    internal int DescriptorBlock(int group)
      => group / this.GroupsPerDescBlock * (this.GroupsPerDescBlock * this.BlocksPerGroup + 1);

    internal int BitmapBlock(int group)
      => this.DescriptorBlock(group) + 1 + group % this.GroupsPerDescBlock * this.BlocksPerGroup;

    internal int EntryBlock(int entry)
      => this.BitmapBlock(entry / this.EntriesPerGroup) + 1
       + entry % this.EntriesPerGroup / this.EntriesPerBlock;

    /// <summary>Logical blocks the file spans to hold <paramref name="entries" /> of them.</summary>
    internal int BlockCount(int entries) => this.EntryBlock(Math.Max(0, entries - 1)) + 1;
  }

  /// <summary>How many nodes a metadata file's map needs, all levels counted.</summary>
  /// <param name="mapped">Blocks the file is made of.</param>
  /// <param name="fill">Children one node takes.</param>
  private static int MetadataTreeNodes(int mapped, int fill) {
    if (mapped <= MaxKernelFileBlocks) return 0;

    var total = 0;
    foreach (var level in PlanBtree(mapped, fill)) total += level.Count;
    return total;
  }

  /// <summary>One user file embedded into the mountable root directory.</summary>
  private sealed class KFile {
    public required string Name;
    public required byte[] Data;
    public ulong Ino;
    public int NBlocks;
    public int[] Phys = [];   // physical block numbers of the data blocks.
    public ulong[] Vblk = []; // virtual block numbers (DAT-translated).

    // A file of more than a handful of blocks is mapped by a b-tree rather than
    // by pointers written straight into the inode. Its nodes are blocks of the
    // file too — the log carries them, the DAT translates them, and the segment
    // summary counts them past the data blocks.
    public List<List<(long FirstKey, int Count)>> Levels = [];
    public int[] LeafPhys = [];
    public ulong[] LeafVblk = [];
    public bool UsesBtree => this.LeafPhys.Length > 0;

    /// <summary>Where each level's nodes start in <see cref="LeafPhys" />.</summary>
    public int[] LevelStart = [];
  }

  /// <summary>One directory of the mountable tree, root or otherwise.</summary>
  private sealed class KDir {
    public required string Name;
    public ulong Ino;
    public ulong Parent;
    public readonly List<(ulong Ino, string Name, byte Type)> Entries = [];
    public int Subdirs;

    // A directory is as many blocks as its entries need, mapped from its inode the
    // same way a file's are.
    public int[] Phys = [];
    public ulong[] Vblk = [];
    public List<List<(long FirstKey, int Count)>> Levels = [];
    public int[] NodePhys = [];
    public ulong[] NodeVblk = [];
    public int[] LevelStart = [];
    public int Blocks => this.Phys.Length;
  }

  private sealed class LogPlan {
    public int PsegStart;
    public int NBlocks;
    public int NSummaryBlocks = 1;
    public List<KFile> Files = [];
    public List<KDir> Dirs = [];
    public int PRootDir, PIfileGd, PIfileBm, PCpfile, PSufile, PSr;

    // Both the disk-address table and the inode file are palloc files: a block of
    // group descriptors, a block of bitmap, then as many blocks of entries as the
    // volume needs. Only the counts were ever one before.
    public int[] PIfileIt = [];
    public int[] PDat = [];
    public int DatEntries;
    public Palloc DatPalloc;
    public ulong[] VIfileIt = [];

    // When a palloc file outgrows the six pointers its inode holds, its own map
    // becomes a tree, and the leaves of that tree are blocks of the log as well.
    public int[] PIfileLeafPhys = [];
    public ulong[] VIfileLeafVblk = [];
    public int[] PDatLeafPhys = [];
    public List<List<(long FirstKey, int Count)>> IfileLevels = [];
    public List<List<(long FirstKey, int Count)>> DatLevels = [];
    public int[] IfileLevelStart = [];
    public int[] DatLevelStart = [];

    public ulong VRootDir, VIfileGd, VIfileBm, VCpfile, VSufile;
    public int NInoUsed;
    public Dictionary<ulong, int> DatMap = [];
  }

  /// <summary>
  /// Assigns physical + virtual block numbers for the single partial segment.
  /// Block order on disk: summary, root dir, user data, ifile palloc (3), DAT
  /// palloc (3), cpfile, sufile, super root (last).
  /// </summary>
  private LogPlan PlanLog(int psegStart, int blockSize) {
    var p = new LogPlan { PsegStart = psegStart };

    // Only files that fit in a direct bmap are embedded into the mountable tree;
    // the writer-private directory still carries every file for the reader.
    // The directories the names imply, root first, each one made before anything
    // it holds so its inode number comes first.
    var root = new KDir { Name = "", Ino = RootIno, Parent = RootIno };
    p.Dirs.Add(root);
    var byPath = new Dictionary<string, KDir>(StringComparer.Ordinal) { [""] = root };

    var ino = (ulong)UserIno;
    KDir DirFor(string path) {
      if (byPath.TryGetValue(path, out var found)) return found;

      var cut = path.LastIndexOf('/');
      var parent = DirFor(cut < 0 ? "" : path[..cut]);
      var made = new KDir { Name = cut < 0 ? path : path[(cut + 1)..], Ino = ino++, Parent = parent.Ino };
      parent.Entries.Add((made.Ino, made.Name, FtDir));
      ++parent.Subdirs;
      p.Dirs.Add(made);
      byPath[path] = made;
      return made;
    }

    foreach (var (rawName, payload) in this._files) {
      var name = rawName.Replace('\\', '/').Trim('/');
      if (name.Length == 0) continue;

      var cut = name.LastIndexOf('/');
      var dir = DirFor(cut < 0 ? "" : name[..cut]);
      var leaf = cut < 0 ? name : name[(cut + 1)..];
      var nblk = Math.Max(1L, (payload.Size + blockSize - 1) / blockSize);
      var file = new KFile { Name = leaf, Data = payload.ToArray(), Ino = ino, NBlocks = (int)nblk };
      dir.Entries.Add((file.Ino, leaf, FtReg));
      if (nblk > MaxKernelFileBlocks) {
        file.Levels = PlanBtree(nblk, BtreeLeafFill(blockSize));


        var total = 0;
        file.LevelStart = new int[file.Levels.Count];
        for (var i = 0; i < file.Levels.Count; ++i) { file.LevelStart[i] = total; total += file.Levels[i].Count; }
        file.LeafPhys = new int[total];
        file.LeafVblk = new ulong[total];
      }

      p.Files.Add(file);
      ++ino;
    }
    p.NInoUsed = (int)ino;

    // How much the segment summary itself takes. It describes every block of the
    // log — one record per file, then one per block — and when that outgrows a
    // single block it simply runs on into the next, which is what ss_sumbytes is
    // for. The payload starts after it.
    var files = p.Files.Count;
    var fileBlocks = 0;
    foreach (var f in p.Files) fileBlocks += f.NBlocks + f.LeafPhys.Length;
    var inodesPerBlockForSummary = blockSize / InodeSize;
    var itBlocksGuess = (p.NInoUsed + inodesPerBlockForSummary - 1) / inodesPerBlockForSummary;
    var datEntriesPerBlockForSummary = blockSize / DatEntrySize;
    var datBlocksGuess = 1;
    for (var pass = 0; pass < 4; ++pass) {
      var vblocks = p.Dirs.Count + fileBlocks + 2 + itBlocksGuess + 2 + 1;
      datBlocksGuess = Math.Max(1,
        (vblocks + datEntriesPerBlockForSummary - 1) / datEntriesPerBlockForSummary);
    }

    var dirBlocksGuess = 0;
    foreach (var d in p.Dirs) dirBlocksGuess += DirectoryBlocks(d, blockSize) * 2 + 1;
    var finfoCount = p.Dirs.Count + files + 4;
    var binfoCount = dirBlocksGuess + fileBlocks + (2 + itBlocksGuess) + (2 + datBlocksGuess) + 1 + 1;
    var summaryBytes = SegsumHeaderBytes + finfoCount * 24 + binfoCount * 16;
    p.NSummaryBlocks = (summaryBytes + blockSize - 1) / blockSize;

    var cur = psegStart + p.NSummaryBlocks; // the summary opens the pseg.
    foreach (var d in p.Dirs) {
      d.Phys = new int[DirectoryBlocks(d, blockSize)];
      for (var i = 0; i < d.Phys.Length; ++i) d.Phys[i] = cur++;
      if (d.Phys.Length > MaxKernelFileBlocks) {
        d.Levels = PlanBtree(d.Phys.Length, BtreeLeafFill(blockSize));
        var total = 0;
        d.LevelStart = new int[d.Levels.Count];
        for (var i = 0; i < d.Levels.Count; ++i) { d.LevelStart[i] = total; total += d.Levels[i].Count; }
        d.NodePhys = new int[total];
        d.NodeVblk = new ulong[total];
        for (var i = 0; i < total; ++i) d.NodePhys[i] = cur++;
      }
    }

    p.PRootDir = p.Dirs[0].Phys[0];
    foreach (var f in p.Files) {
      f.Phys = new int[f.NBlocks];
      for (var i = 0; i < f.NBlocks; ++i) f.Phys[i] = cur++;
      for (var i = 0; i < f.LeafPhys.Length; ++i) f.LeafPhys[i] = cur++;
    }
    // How many blocks of entries each palloc file needs. The disk-address table is
    // indexed by virtual block number, so it has one entry per block the log
    // translates — the root directory, every file block and every b-tree leaf, and
    // the metadata files' own blocks.
    var datBlocks = 0;
    foreach (var f in p.Files) datBlocks += f.NBlocks + f.LeafPhys.Length;
    // Directories take as many blocks as their entries need, and the maps over
    // those are blocks of the log too.
    var dirBlocksTotal = 0;
    foreach (var d in p.Dirs) dirBlocksTotal += d.Blocks + d.NodePhys.Length;
    var vblocksNeeded = dirBlocksTotal + datBlocks + 3 + 2 + 1;
    var datEntriesPerBlock = blockSize / DatEntrySize;
    var inodesPerBlock = blockSize / InodeSize;
    var perLeafFor = BtreeLeafFill(blockSize);

    p.PIfileGd = cur++; p.PIfileBm = cur++;
    p.PIfileIt = new int[(p.NInoUsed + inodesPerBlock - 1) / inodesPerBlock];
    for (var i = 0; i < p.PIfileIt.Length; ++i) p.PIfileIt[i] = cur++;

    // The inode file's own map costs virtual blocks too, and so does the address
    // table's growth, so the count is settled by going round until it stops moving
    // rather than guessed at once.
    var ifileNodes = MetadataTreeNodes(2 + p.PIfileIt.Length, perLeafFor);

    var datPalloc = Palloc.For(blockSize, DatEntrySize);
    var datEntriesNeeded = 1;
    for (var pass = 0; pass < 8; ++pass) {
      var settled = vblocksNeeded + p.PIfileIt.Length + ifileNodes
        + MetadataTreeNodes(datPalloc.BlockCount(datEntriesNeeded), perLeafFor);
      if (settled == datEntriesNeeded) break;

      datEntriesNeeded = settled;
    }

    p.PDat = new int[datPalloc.BlockCount(datEntriesNeeded)];
    for (var i = 0; i < p.PDat.Length; ++i) p.PDat[i] = cur++;
    p.DatEntries = datEntriesNeeded;
    p.DatPalloc = datPalloc;

    // Each palloc file is mapped from its own inode, which holds six pointers
    // before it needs a tree — and the tables grow past that on any volume with
    // more than a couple of megabytes in it, or more than a hundred-odd files.
    var perLeafHere = BtreeLeafFill(blockSize);

    // The two palloc files are mapped from their own inodes, and once past the
    // handful of pointers those hold they grow trees of their own — the same shape
    // as any file's, and as tall as they need.
    (List<List<(long FirstKey, int Count)>> Levels, int[] Start, int[] Phys) PlanMetadataTree(int mapped) {
      if (mapped <= MaxKernelFileBlocks) return ([], [], []);

      var levels = PlanBtree(mapped, perLeafHere);
      var start = new int[levels.Count];
      var total = 0;
      for (var i = 0; i < levels.Count; ++i) { start[i] = total; total += levels[i].Count; }

      var phys = new int[total];
      for (var i = 0; i < total; ++i) phys[i] = cur++;
      return (levels, start, phys);
    }

    (p.IfileLevels, p.IfileLevelStart, p.PIfileLeafPhys) = PlanMetadataTree(2 + p.PIfileIt.Length);
    (p.DatLevels, p.DatLevelStart, p.PDatLeafPhys) = PlanMetadataTree(p.PDat.Length);
    p.PCpfile = cur++; p.PSufile = cur++;
    p.PSr = cur++;
    p.NBlocks = cur - psegStart;

    // Virtual block numbers (DAT-translated). Only the DAT itself is physical.
    var v = 1ul;
    ulong VAlloc(int phys) { var vb = v++; p.DatMap[vb] = phys; return vb; }
    foreach (var d in p.Dirs) {
      d.Vblk = new ulong[d.Phys.Length];
      for (var i = 0; i < d.Phys.Length; ++i) d.Vblk[i] = VAlloc(d.Phys[i]);
      for (var i = 0; i < d.NodePhys.Length; ++i) d.NodeVblk[i] = VAlloc(d.NodePhys[i]);
    }

    p.VRootDir = p.Dirs[0].Vblk[0];
    foreach (var f in p.Files) {
      f.Vblk = new ulong[f.NBlocks];
      for (var i = 0; i < f.NBlocks; ++i) f.Vblk[i] = VAlloc(f.Phys[i]);
      for (var i = 0; i < f.LeafPhys.Length; ++i) f.LeafVblk[i] = VAlloc(f.LeafPhys[i]);
    }
    p.VIfileGd = VAlloc(p.PIfileGd); p.VIfileBm = VAlloc(p.PIfileBm);
    p.VIfileIt = new ulong[p.PIfileIt.Length];
    for (var i = 0; i < p.PIfileIt.Length; ++i) p.VIfileIt[i] = VAlloc(p.PIfileIt[i]);
    p.VIfileLeafVblk = new ulong[p.PIfileLeafPhys.Length];
    for (var i = 0; i < p.PIfileLeafPhys.Length; ++i) p.VIfileLeafVblk[i] = VAlloc(p.PIfileLeafPhys[i]);
    p.VCpfile = VAlloc(p.PCpfile); p.VSufile = VAlloc(p.PSufile);
    return p;
  }

  private void WriteKernelLog(SparseBlockImage img, LogPlan p, int blockSize, ulong nSegments, int segBlocks) {
    // ── DAT (physical pointers) ───────────────────────────────────────────
    var pal = p.DatPalloc;
    var datBlocks = new byte[p.PDat.Length][];
    for (var i = 0; i < datBlocks.Length; ++i) datBlocks[i] = Block(blockSize);

    foreach (var (vblk, phys) in p.DatMap) {
      var block = pal.EntryBlock((int)vblk);
      var o = (int)vblk % pal.EntriesPerGroup % pal.EntriesPerBlock * DatEntrySize;
      if (block >= datBlocks.Length)
        throw new InvalidOperationException("Nilfs2: a virtual block fell past the address table.");

      BinaryPrimitives.WriteUInt64LittleEndian(datBlocks[block].AsSpan(o), (ulong)phys);  // de_blocknr
      BinaryPrimitives.WriteUInt64LittleEndian(datBlocks[block].AsSpan(o + 8), 1);        // de_start (cno)
      BinaryPrimitives.WriteUInt64LittleEndian(datBlocks[block].AsSpan(o + 16), LiveEnd); // de_end (-1 = live)
    }

    var nDatUsed = p.DatMap.Count == 0 ? 1 : (int)p.DatMap.Keys.Max() + 1;
    WritePallocGroups(datBlocks, blockSize, pal, nDatUsed);
    for (var i = 0; i < datBlocks.Length; ++i) WriteBlock(img, p.PDat[i], blockSize, datBlocks[i]);

    // ── ifile (palloc: group desc, bitmap, inode table) ────────────────────
    var perInodeBlock = blockSize / InodeSize;
    var itBlocks = new byte[p.PIfileIt.Length][];
    for (var i = 0; i < itBlocks.Length; ++i) itBlocks[i] = Block(blockSize);
    Span<byte> InodeAt(int index) =>
      itBlocks[index / perInodeBlock].AsSpan(index % perInodeBlock * InodeSize, InodeSize);

    for (var i = 0; i < p.NInoUsed; ++i)
      WriteInode(InodeAt(i), 0, 0, SIfreg, 1, []);

    // A directory links to itself, to its parent, and once more for each
    // directory it holds — which is what the driver counts to know it is empty.
    foreach (var d in p.Dirs) {
      var inode = InodeAt((int)d.Ino);
      var held = (ulong)(d.Blocks + d.NodePhys.Length);
      var size = (ulong)((long)d.Blocks * blockSize);
      if (d.NodePhys.Length == 0) {
        WriteInode(inode, held, size, (ushort)(SIfdir | 0x1ED), (ushort)(2 + d.Subdirs), d.Vblk);
        continue;
      }

      WriteInode(inode, held, size, (ushort)(SIfdir | 0x1ED), (ushort)(2 + d.Subdirs), []);
      WriteBtreeLevels(img, blockSize, d.Vblk, d.NodeVblk, d.NodePhys, d.Levels, d.LevelStart);
      var top = d.Levels.Count - 1;
      var keys = new ulong[d.Levels[top].Count];
      var ptrs = new ulong[keys.Length];
      for (var i = 0; i < keys.Length; ++i) {
        keys[i] = (ulong)d.Levels[top][i].FirstKey;
        ptrs[i] = d.NodeVblk[d.LevelStart[top] + i];
      }

      WriteBtreeRoot(inode, ptrs, keys, (byte)(top + 2));
    }
    foreach (var f in p.Files) {
      var inode = InodeAt((int)f.Ino);
      // A file's block tally counts what holds it: its data, and the leaves that
      // say where the data is.
      var held = (ulong)(f.NBlocks + f.LeafPhys.Length);
      if (!f.UsesBtree) {
        WriteInode(inode, held, (ulong)f.Data.Length, (ushort)(SIfreg | 0x1A4), 1, f.Vblk);
        continue;
      }

      WriteInode(inode, held, (ulong)f.Data.Length, (ushort)(SIfreg | 0x1A4), 1, []);

      // The root stands over the topmost level of nodes, and its own level says
      // how far down the tree reaches.
      var top = f.Levels.Count - 1;
      var topNodes = f.Levels[top];
      var rootKeys = new ulong[topNodes.Count];
      var rootPtrs = new ulong[topNodes.Count];
      for (var i = 0; i < topNodes.Count; ++i) {
        rootKeys[i] = (ulong)topNodes[i].FirstKey;
        rootPtrs[i] = f.LeafVblk[f.LevelStart[top] + i];
      }

      WriteBtreeRoot(inode, rootPtrs, rootKeys, (byte)(top + 2));
    }
    for (var i = 0; i < itBlocks.Length; ++i) WriteBlock(img, p.PIfileIt[i], blockSize, itBlocks[i]);
    WritePallocMeta(img, p.PIfileGd, p.PIfileBm, blockSize, p.NInoUsed);

    // ── root directory ─────────────────────────────────────────────────────
    foreach (var d in p.Dirs) {
      var blocks = new byte[d.Blocks][];
      for (var i = 0; i < blocks.Length; ++i) blocks[i] = Block(blockSize);

      var which = 0;
      var off = WriteDirent(blocks[0].AsSpan(0), d.Ino, ".", FtDir, DirRecLen(1));
      off += WriteDirent(blocks[0].AsSpan(off), d.Parent, "..", FtDir, DirRecLen(2));
      var lastAt = off - DirRecLen(2);

      foreach (var (entryIno, entryName, entryType) in d.Entries) {
        var need = DirRecLen(Encoding.UTF8.GetByteCount(entryName));
        if (off + need > blockSize) {
          // The last record of a block claims what is left of it, which is how a
          // reader knows where that block's entries stop.
          BinaryPrimitives.WriteUInt16LittleEndian(blocks[which].AsSpan(lastAt + 8), (ushort)(blockSize - lastAt));
          ++which;
          off = 0;
        }

        WriteDirent(blocks[which].AsSpan(off), entryIno, entryName, entryType, need);
        lastAt = off;
        off += need;
      }

      BinaryPrimitives.WriteUInt16LittleEndian(blocks[which].AsSpan(lastAt + 8), (ushort)(blockSize - lastAt));
      for (var i = 0; i < blocks.Length; ++i) WriteBlock(img, d.Phys[i], blockSize, blocks[i]);
    }
    foreach (var f in p.Files) {
      for (var bi = 0; bi < f.NBlocks; ++bi) {
        var chunk = f.Data.AsSpan(bi * blockSize, Math.Min(blockSize, f.Data.Length - bi * blockSize));
        img.Write((long)f.Phys[bi] * blockSize, chunk);
      }

      WriteBtreeLevels(img, blockSize, f.Vblk, f.LeafVblk, f.LeafPhys, f.Levels, f.LevelStart);

    }

    // ── cpfile ──────────────────────────────────────────────────────────────
    var cp = Block(blockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(0), 1); // ch_ncheckpoints
    var cpoff = ((48 + CheckpointSize - 1) / CheckpointSize) * CheckpointSize; // first cp slot.
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(cpoff + 24), 1);                 // cp_cno
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(cpoff + 32), this._ctime);        // cp_create
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(cpoff + 40), (ulong)p.NBlocks);  // cp_nblk_inc
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(cpoff + 48), (ulong)p.NInoUsed); // cp_inodes_count
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(cpoff + 56), (ulong)p.NBlocks);  // cp_blocks_count
    var ifileMap = new List<ulong> { p.VIfileGd, p.VIfileBm };
    ifileMap.AddRange(p.VIfileIt);
    this.WriteMetadataMap(cp.AsSpan(cpoff + 64), blockSize, img, ifileMap.ToArray(),
      p.PIfileLeafPhys, p.VIfileLeafVblk, p.IfileLevels, p.IfileLevelStart);
    // The inode file knows its own length even when a tree maps it.
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(cpoff + 64 + 8), (ulong)(ifileMap.Count * blockSize));
    WriteBlock(img, p.PCpfile, blockSize, cp);

    // ── sufile ──────────────────────────────────────────────────────────────
    // One block of segment usage entries. The log sits in one of the first
    // segments by construction, so its slot is always inside this block.
    var segOfPseg = p.PsegStart / segBlocks;
    var su = Block(blockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(su.AsSpan(0), nSegments - 1);     // sh_ncleansegs
    BinaryPrimitives.WriteUInt64LittleEndian(su.AsSpan(8), 1);                 // sh_ndirtysegs
    BinaryPrimitives.WriteUInt64LittleEndian(su.AsSpan(16), (ulong)segOfPseg); // sh_last_alloc
    var suoff = ((24 + SegUsageSize - 1) / SegUsageSize) * SegUsageSize;
    var suSlot = suoff + segOfPseg * SegUsageSize;
    if (suSlot + SegUsageSize > blockSize)
      throw new InvalidOperationException(
        $"Nilfs2: the log landed in segment {segOfPseg}, past the {blockSize / SegUsageSize} " +
        "segments one sufile block can describe.");
    BinaryPrimitives.WriteUInt64LittleEndian(su.AsSpan(suSlot), this._ctime);       // su_lastmod
    BinaryPrimitives.WriteUInt32LittleEndian(su.AsSpan(suSlot + 8), (uint)p.NBlocks);// su_nblocks
    BinaryPrimitives.WriteUInt32LittleEndian(su.AsSpan(suSlot + 12), 0x1);         // su_flags = DIRTY
    WriteBlock(img, p.PSufile, blockSize, su);

    // ── super root (last block of the pseg) ─────────────────────────────────
    var sr = Block(blockSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sr.AsSpan(4), SuperRootBytes); // sr_bytes
    BinaryPrimitives.WriteUInt64LittleEndian(sr.AsSpan(8), this._ctime);     // sr_nongc_ctime
    var datMap = new List<ulong>();
    foreach (var b in p.PDat) datMap.Add((ulong)b);
    // The address table's pointers are physical, and so are the leaves of its map.
    var datNodePointers = new ulong[p.PDatLeafPhys.Length];
    for (var i = 0; i < datNodePointers.Length; ++i) datNodePointers[i] = (ulong)p.PDatLeafPhys[i];
    this.WriteMetadataMap(sr.AsSpan(16), blockSize, img, datMap.ToArray(), p.PDatLeafPhys,
      datNodePointers, p.DatLevels, p.DatLevelStart);
    WriteInode(sr.AsSpan(16 + InodeSize), 1, 0, SIfreg, 1, [p.VCpfile]); // cpfile: virtual.
    WriteInode(sr.AsSpan(16 + InodeSize * 2), 1, 0, SIfreg, 1, [p.VSufile]); // sufile: virtual.
    var srSum = Nilfs2Superblock.Crc32Le(this._crcSeed, sr.AsSpan(4, SuperRootBytes - 4));
    BinaryPrimitives.WriteUInt32LittleEndian(sr.AsSpan(0), srSum); // sr_sum over [4..sr_bytes].
    WriteBlock(img, p.PSr, blockSize, sr);

    // ── segment summary (block 0 of the pseg) ───────────────────────────────
    WriteSegmentSummary(img, p, blockSize, segOfPseg, segBlocks);
  }

  private void WriteSegmentSummary(SparseBlockImage img, LogPlan p, int blockSize, int segOfPseg, int segBlocks) {
    using var ms = new MemoryStream();
    void Finfo(ulong ino, int nblk, int ndatablk) {
      Span<byte> b = stackalloc byte[24];
      BinaryPrimitives.WriteUInt64LittleEndian(b, ino);
      BinaryPrimitives.WriteUInt64LittleEndian(b[8..], 1); // fi_cno
      BinaryPrimitives.WriteUInt32LittleEndian(b[16..], (uint)nblk);
      BinaryPrimitives.WriteUInt32LittleEndian(b[20..], (uint)ndatablk);
      ms.Write(b);
    }
    void BinfoV(ulong vblk, ulong blkoff) {
      Span<byte> b = stackalloc byte[16];
      BinaryPrimitives.WriteUInt64LittleEndian(b, vblk);
      BinaryPrimitives.WriteUInt64LittleEndian(b[8..], blkoff);
      ms.Write(b);
    }
    void BinfoDat(ulong blkoff, byte level) {
      Span<byte> b = stackalloc byte[16];
      BinaryPrimitives.WriteUInt64LittleEndian(b, blkoff);
      b[8] = level;
      ms.Write(b);
    }

    var nfinfo = 0;
    // Order MUST match the physical payload order in PlanLog.
    foreach (var d in p.Dirs) {
      Finfo(d.Ino, d.Blocks + d.NodePhys.Length, d.Blocks);
      for (var i = 0; i < d.Blocks; ++i) BinfoV(d.Vblk[i], (ulong)i);
      for (var level = 0; level < d.Levels.Count; ++level)
        for (var i = 0; i < d.Levels[level].Count; ++i)
          BinfoV(d.NodeVblk[d.LevelStart[level] + i], (ulong)d.Levels[level][i].FirstKey);
      ++nfinfo;
    }
    foreach (var f in p.Files) {
      Finfo(f.Ino, f.NBlocks + f.LeafPhys.Length, f.NBlocks);
      for (var bi = 0; bi < f.NBlocks; ++bi) BinfoV(f.Vblk[bi], (ulong)bi);
      // What follows the data blocks is read as the tree's own blocks, each named
      // by the first key it covers.
      for (var level = 0; level < f.Levels.Count; ++level)
        for (var i = 0; i < f.Levels[level].Count; ++i)
          BinfoV(f.LeafVblk[f.LevelStart[level] + i], (ulong)f.Levels[level][i].FirstKey);
      ++nfinfo;
    }
    var ifileBlocks = 2 + p.VIfileIt.Length;
    Finfo(IfileIno, ifileBlocks + p.VIfileLeafVblk.Length, ifileBlocks);
    BinfoV(p.VIfileGd, 0); BinfoV(p.VIfileBm, 1);
    for (var i = 0; i < p.VIfileIt.Length; ++i) BinfoV(p.VIfileIt[i], (ulong)(2 + i));
    for (var level = 0; level < p.IfileLevels.Count; ++level)
      for (var i = 0; i < p.IfileLevels[level].Count; ++i)
        BinfoV(p.VIfileLeafVblk[p.IfileLevelStart[level] + i], (ulong)p.IfileLevels[level][i].FirstKey);
    ++nfinfo;

    var datBlocks2 = p.PDat.Length;
    Finfo(DatIno, datBlocks2 + p.PDatLeafPhys.Length, datBlocks2);
    for (var i = 0; i < datBlocks2; ++i) BinfoDat((ulong)i, 0);
    // A block of the address table's own map is named by the key it covers and by
    // how far up the tree it sits.
    for (var level = 0; level < p.DatLevels.Count; ++level)
      for (var i = 0; i < p.DatLevels[level].Count; ++i)
        BinfoDat((ulong)p.DatLevels[level][i].FirstKey, (byte)(level + 1));
    ++nfinfo;
    Finfo(CpfileIno, 1, 1); BinfoV(p.VCpfile, 0); ++nfinfo;
    Finfo(SufileIno, 1, 1); BinfoV(p.VSufile, 0); ++nfinfo;

    var summary = ms.ToArray();
    var sumbytes = SegsumHeaderBytes + summary.Length;

    var ss = new byte[p.NSummaryBlocks * blockSize];
    if (sumbytes > ss.Length)
      throw new InvalidOperationException(
        $"Nilfs2: the segment summary needs {sumbytes:N0} bytes but only "
        + $"{ss.Length:N0} were reserved for it.");

    BinaryPrimitives.WriteUInt32LittleEndian(ss.AsSpan(8), SegsumMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(ss.AsSpan(12), SegsumHeaderBytes); // ss_bytes
    BinaryPrimitives.WriteUInt16LittleEndian(ss.AsSpan(14), (ushort)(SsLogBgn | SsLogEnd | SsSr));
    BinaryPrimitives.WriteUInt64LittleEndian(ss.AsSpan(16), 0);                 // ss_seq
    BinaryPrimitives.WriteUInt64LittleEndian(ss.AsSpan(24), this._ctime);        // ss_create
    BinaryPrimitives.WriteUInt64LittleEndian(ss.AsSpan(32), (ulong)((segOfPseg + 1) * segBlocks)); // ss_next
    BinaryPrimitives.WriteUInt32LittleEndian(ss.AsSpan(40), (uint)p.NBlocks);   // ss_nblocks
    BinaryPrimitives.WriteUInt32LittleEndian(ss.AsSpan(44), (uint)nfinfo);      // ss_nfinfo
    BinaryPrimitives.WriteUInt32LittleEndian(ss.AsSpan(48), (uint)sumbytes);    // ss_sumbytes
    BinaryPrimitives.WriteUInt64LittleEndian(ss.AsSpan(56), 1);                 // ss_cno
    summary.CopyTo(ss.AsSpan(SegsumHeaderBytes));

    // ss_sumsum = crc32_le over [8 .. sumbytes].
    var sumsum = Nilfs2Superblock.Crc32Le(this._crcSeed, ss.AsSpan(8, sumbytes - 8));
    BinaryPrimitives.WriteUInt32LittleEndian(ss.AsSpan(4), sumsum);
    for (var i = 0; i < p.NSummaryBlocks; ++i)
      WriteBlock(img, p.PsegStart + i, blockSize, ss.AsSpan(i * blockSize, blockSize).ToArray());

    // ss_datasum = crc32_le over [pseg+4 .. pseg + nblocks*blockSize].
    var logBytes = img.Read((long)p.PsegStart * blockSize + 4, p.NBlocks * blockSize - 4);
    var datasum = Nilfs2Superblock.Crc32Le(this._crcSeed, logBytes);
    BinaryPrimitives.WriteUInt32LittleEndian(ss.AsSpan(0), datasum);
    for (var i = 0; i < p.NSummaryBlocks; ++i)
      WriteBlock(img, p.PsegStart + i, blockSize, ss.AsSpan(i * blockSize, blockSize).ToArray());
  }

  // ── helpers ─────────────────────────────────────────────────────────────

  private const byte FtReg = 1, FtDir = 2; // NILFS_FT_* low 3 bits.

  private static byte[] Block(int blockSize) => new byte[blockSize];

  private static void WriteBlock(SparseBlockImage img, int blk, int blockSize, byte[] data) =>
    img.Write((long)blk * blockSize, data.AsSpan(0, blockSize));

  /// <summary>Writes a <c>nilfs_inode</c> with a direct block map.</summary>
  private void WriteInode(Span<byte> dst, ulong blocks, ulong size, ushort mode,
      ushort links, ReadOnlySpan<ulong> bmapPtrs) {
    dst[..InodeSize].Clear();
    BinaryPrimitives.WriteUInt64LittleEndian(dst, blocks);          // i_blocks
    BinaryPrimitives.WriteUInt64LittleEndian(dst[8..], size);       // i_size
    BinaryPrimitives.WriteUInt64LittleEndian(dst[16..], this._ctime);// i_ctime
    BinaryPrimitives.WriteUInt64LittleEndian(dst[24..], this._ctime);// i_mtime
    BinaryPrimitives.WriteUInt16LittleEndian(dst[48..], mode);      // i_mode
    BinaryPrimitives.WriteUInt16LittleEndian(dst[50..], links);     // i_links_count
    // i_bmap[7] at +56: a direct map has bmap[0] = 0 (NILFS_BMAP_LARGE clear),
    // bmap[1+key] = pointer for that key.
    for (var k = 0; k < bmapPtrs.Length && k < 6; ++k)
      BinaryPrimitives.WriteUInt64LittleEndian(dst[(56 + (k + 1) * 8)..], bmapPtrs[k]);
  }

  /// <summary>
  /// Writes a b-tree root into the inode's map area: the header, then a key and a
  /// pointer for each leaf.
  /// </summary>
  /// <remarks>
  /// The root is what tells the two kinds of map apart. A map whose first word is
  /// zero reads as pointers written straight into the inode; one that sets the root
  /// flag and a level of at least one reads as a tree, and the kernel rejects any
  /// root that claims neither.
  /// </remarks>
  private static void WriteBtreeRoot(Span<byte> inode, ReadOnlySpan<ulong> leafPtrs, ReadOnlySpan<ulong> leafKeys,
                                     byte level = BtreeLevelRoot) {
    var root = inode[56..];
    root[..BmapBytes].Clear();
    root[0] = BtreeNodeRootFlag;
    root[1] = level;
    BinaryPrimitives.WriteUInt16LittleEndian(root[2..], (ushort)leafPtrs.Length);

    if (leafPtrs.Length > BtreeRootChildren)
      throw new InvalidOperationException(
        $"Nilfs2: a b-tree root holds {BtreeRootChildren} children, not {leafPtrs.Length}.");

    for (var i = 0; i < leafPtrs.Length; ++i) {
      BinaryPrimitives.WriteUInt64LittleEndian(root[(BtreeNodeHeaderBytes + i * 8)..], leafKeys[i]);
      BinaryPrimitives.WriteUInt64LittleEndian(
        root[(BtreeNodeHeaderBytes + BtreeRootChildren * 8 + i * 8)..], leafPtrs[i]);
    }
  }

  /// <summary>
  /// Maps a metadata file from its own inode: straight pointers while they fit,
  /// and a tree of one level once they do not.
  /// </summary>
  /// <remarks>
  /// The address table is the one file whose pointers are physical, since it is
  /// what turns virtual block numbers into physical ones and so cannot be read
  /// through itself. Everything else, the inode file included, is mapped by
  /// virtual blocks like any ordinary file.
  /// </remarks>
  /// <param name="blocks">What the file is made of, as its own map names them.</param>
  /// <param name="leafPhys">Where each leaf of the map goes on the disk.</param>
  /// <param name="leafPointers">
  /// How the root names those leaves — which is not where they are unless the
  /// file's pointers are physical. The address table's are; the inode file's are
  /// virtual, so its root names its leaves by virtual block, and naming them by
  /// their place on the disk instead makes a volume that will not mount.
  /// </param>
  private void WriteMetadataMap(Span<byte> inode, int blockSize, SparseBlockImage img,
                                ReadOnlySpan<ulong> blocks, int[] nodePhys, ulong[] nodePointers,
                                List<List<(long FirstKey, int Count)>> levels, int[] levelStart) {
    if (nodePhys.Length == 0) {
      WriteInode(inode, (ulong)blocks.Length, 0, SIfreg, 1, blocks);
      return;
    }

    WriteInode(inode, (ulong)(blocks.Length + nodePhys.Length), 0, SIfreg, 1, []);

    var perNode = BtreeLeafFill(blockSize);
    for (var level = 0; level < levels.Count; ++level) {
      var nodes = levels[level];
      for (var i = 0; i < nodes.Count; ++i) {
        var (firstKey, count) = nodes[i];
        var keys = new ulong[count];
        var pointers = new ulong[count];
        for (var c = 0; c < count; ++c)
          if (level == 0) {
            keys[c] = (ulong)(firstKey + c);
            pointers[c] = blocks[i * perNode + c];
          } else {
            keys[c] = (ulong)levels[level - 1][i * perNode + c].FirstKey;
            pointers[c] = nodePointers[levelStart[level - 1] + i * perNode + c];
          }

        WriteBlock(img, nodePhys[levelStart[level] + i], blockSize,
          BuildBtreeNode(blockSize, (byte)(level + 1), keys, pointers));
      }
    }

    var top = levels.Count - 1;
    var rootKeys = new ulong[levels[top].Count];
    var rootPtrs = new ulong[levels[top].Count];
    for (var i = 0; i < rootKeys.Length; ++i) {
      rootKeys[i] = (ulong)levels[top][i].FirstKey;
      rootPtrs[i] = nodePointers[levelStart[top] + i];
    }

    WriteBtreeRoot(inode, rootPtrs, rootKeys, (byte)(top + 2));
  }

  /// <summary>
  /// Writes one b-tree leaf: the blocks of the file it covers, by key and by the
  /// virtual block each key stands for.
  /// </summary>
  /// <summary>
  /// Writes every node of a map, leaves first: each names the blocks or the nodes
  /// below it, by key and by the virtual block that key stands for.
  /// </summary>
  private static void WriteBtreeLevels(SparseBlockImage img, int blockSize,
                                       ReadOnlySpan<ulong> dataVblk, ulong[] nodeVblk, int[] nodePhys,
                                       List<List<(long FirstKey, int Count)>> levels, int[] levelStart) {
    var perNode = BtreeLeafFill(blockSize);
    for (var level = 0; level < levels.Count; ++level) {
      var nodes = levels[level];
      for (var i = 0; i < nodes.Count; ++i) {
        var (firstKey, count) = nodes[i];
        var keys = new ulong[count];
        var pointers = new ulong[count];
        for (var c = 0; c < count; ++c)
          if (level == 0) {
            keys[c] = (ulong)(firstKey + c);
            pointers[c] = dataVblk[i * perNode + c];
          } else {
            keys[c] = (ulong)levels[level - 1][i * perNode + c].FirstKey;
            pointers[c] = nodeVblk[levelStart[level - 1] + i * perNode + c];
          }

        WriteBlock(img, nodePhys[levelStart[level] + i], blockSize,
          BuildBtreeNode(blockSize, (byte)(level + 1), keys, pointers));
      }
    }
  }

  private static byte[] BuildBtreeNode(int blockSize, byte level,
                                       ReadOnlySpan<ulong> keys, ReadOnlySpan<ulong> pointers) {
    var children = BtreeNodeChildren(blockSize);
    var node = Block(blockSize);
    node[0] = 0;                                    // not the root
    node[1] = level;
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(2), (ushort)pointers.Length);

    for (var i = 0; i < pointers.Length; ++i) {
      BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(BtreeNodeHeaderBytes + i * 8), keys[i]);
      BinaryPrimitives.WriteUInt64LittleEndian(
        node.AsSpan(BtreeNodeHeaderBytes + children * 8 + i * 8), pointers[i]);
    }

    return node;
  }

  private static byte[] BuildBtreeLeaf(int blockSize, ReadOnlySpan<ulong> vblocks, long firstKey) {
    var keys = new ulong[vblocks.Length];
    for (var i = 0; i < keys.Length; ++i) keys[i] = (ulong)(firstKey + i);
    return BuildBtreeNode(blockSize, BtreeLevelLeaf, keys, vblocks);
  }

  /// <summary>Writes a palloc group-descriptor block + bitmap block for group 0.</summary>
  /// <remarks>
  /// One group, whose bitmap is a single block and so accounts for eight entries
  /// per byte of it — thirty-two thousand-odd at 4 KiB. A file needing more would
  /// need a second group, with its own descriptor, bitmap and entries, which this
  /// writer does not lay out.
  /// </remarks>
  /// <summary>
  /// Fills in a palloc file's group descriptors and bitmaps for <paramref name="used" />
  /// entries taken from the front.
  /// </summary>
  private static void WritePallocGroups(byte[][] blocks, int blockSize, Palloc palloc, int used) {
    for (var group = 0; group < palloc.Groups(used); ++group) {
      var taken = Math.Min(palloc.EntriesPerGroup, used - group * palloc.EntriesPerGroup);
      var descriptor = blocks[palloc.DescriptorBlock(group)];
      var slot = group % palloc.GroupsPerDescBlock * 4;
      BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(slot),
        (uint)(palloc.EntriesPerGroup - taken));                          // pg_nfrees

      var bitmap = blocks[palloc.BitmapBlock(group)];
      for (var i = 0; i < taken; ++i) bitmap[i >> 3] |= (byte)(1 << (i & 7));
    }
  }

  private static void WritePallocMeta(SparseBlockImage img, int gdBlk, int bmBlk, int blockSize, int used) {
    if (used > blockSize * 8)
      throw new InvalidOperationException(
        $"Nilfs2: {used:N0} entries need more than the {blockSize * 8:N0} one allocation group's "
        + "bitmap accounts for, and this writer lays out a single group.");

    var gd = Block(blockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(gd.AsSpan(0), (uint)(blockSize * 8 - used)); // pg_nfrees.
    WriteBlock(img, gdBlk, blockSize, gd);

    var bm = Block(blockSize);
    for (var i = 0; i < used; ++i) bm[i >> 3] |= (byte)(1 << (i & 7));
    WriteBlock(img, bmBlk, blockSize, bm);
  }

  private static int DirRecLen(int nameLen) => (nameLen + 12 + 7) & ~7; // NILFS_DIR_REC_LEN.

  /// <summary>
  /// How many blocks a directory's entries take, no record straddling a boundary.
  /// </summary>
  private static int DirectoryBlocks(KDir dir, int blockSize) {
    var used = DirRecLen(1) + DirRecLen(2);   // "." and ".."
    var blocks = 1;
    foreach (var (_, name, _) in dir.Entries) {
      var need = DirRecLen(Encoding.UTF8.GetByteCount(name));
      if (need > blockSize)
        throw new InvalidOperationException($"Nilfs2: the name '{name}' does not fit a {blockSize}-byte block.");

      if (used + need > blockSize) { ++blocks; used = 0; }
      used += need;
    }

    return blocks;
  }

  private static int WriteDirent(Span<byte> dst, ulong ino, string name, byte fileType, int recLen) {
    var nb = Encoding.UTF8.GetBytes(name);
    BinaryPrimitives.WriteUInt64LittleEndian(dst, ino);
    BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], (ushort)recLen);
    dst[10] = (byte)nb.Length;
    dst[11] = fileType;
    nb.CopyTo(dst[12..]);
    return recLen;
  }
}
