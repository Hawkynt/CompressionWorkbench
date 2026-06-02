#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.AmigaPfs;

/// <summary>
/// Writer for Amiga Professional File System (PFS3) images.
/// Emits the same on-disk shape <see cref="AmigaPfsReader"/> parses:
/// <list type="bullet">
///   <item>Block 0: boot block — signature "PFS\x03" at +0, root-block pointer (u32 BE) at +8.</item>
///   <item>Root block (default block 80, or as configured): BCPL volume label at +26 (32 bytes max),
///         first dirblock pointer (u32 BE) at +60.</item>
///   <item>Directory block(s): u16 BE id 0xC4 at +0, u32 BE next-chain pointer at +12,
///         u32 BE parent at +16, variable-length entries starting at +20. Each entry carries
///         length, type (bit 7 = directory), 32-bit anode number, 32-bit file size,
///         packed date/time, BCPL filename, and a (zero-length) trailing comment.</item>
///   <item>File data blocks: contiguous run of blocks starting at the file's "anode number".
///         The Stage 1 reader treats <c>anode * BlockSize</c> as the start of the file's
///         payload and reads exactly <c>Size</c> bytes — i.e. anode is used as a direct
///         block pointer, not as an index into an anode-table. The writer matches that
///         convention by allocating each file's payload as a single contiguous extent.</item>
/// </list>
///
/// PFS3aio (Toni Wilen's reference implementation) and Michiel Pelt's original 1995
/// PFS technical note describe far richer on-disk structures (anode tables, root-info
/// pointers to anode/dir B-trees, bitmap blocks, deldir, rblkcluster groups). The
/// Stage 1 reader explicitly does not walk those structures, so the writer's output
/// is intentionally a Stage 1 skeleton: signature + root block + linear dirblock
/// chain + contiguous file extents. It is sufficient for self-round-trip with the
/// matching reader and for descriptors that exercise the WORM <c>Create</c> path.
/// It is <b>not</b> mountable in FS-UAE / WinUAE — full anode/bitmap emission would
/// be required for emulator parity and is deferred to a Stage 2 promotion.
/// </summary>
public sealed class AmigaPfsWriter {

  /// <summary>Default block size (512 bytes — matches floppy and the reader default).</summary>
  public const int DefaultBlockSize = 512;

  /// <summary>Conventional root-block location on a PFS floppy (block 80).</summary>
  private const uint DefaultRootBlock = 80;

  /// <summary>PFS3 directory-block ID written at offset 0 of every dirblock.</summary>
  private const ushort DirBlockId = 0xC4;

  /// <summary>Maximum BCPL filename length the dirblock entry can encode (single-byte length prefix).</summary>
  private const int MaxFilenameLength = 200;

  /// <summary>Maximum BCPL volume-name length at root +26 (one byte for the length prefix plus up to 31 chars).</summary>
  private const int MaxDiskNameLength = 31;

  private readonly int _blockSize;
  private readonly uint _rootBlock;
  private readonly List<(string Name, byte[] Data, DateTime? ModTime, bool IsDirectory)> _entries = [];

  /// <summary>
  /// Creates a writer that produces a PFS3 image with the given block size and
  /// root-block location. Both arguments default to the floppy convention used
  /// by <see cref="AmigaPfsReader"/>.
  /// </summary>
  public AmigaPfsWriter(int blockSize = DefaultBlockSize, uint rootBlock = DefaultRootBlock) {
    if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be a positive power of two.");
    if (rootBlock < 2)
      throw new ArgumentOutOfRangeException(nameof(rootBlock), "Root block must come after the boot block (>= 2).");
    this._blockSize = blockSize;
    this._rootBlock = rootBlock;
  }

  /// <summary>Adds a regular file. The leaf name is taken from the last path segment.</summary>
  public void AddFile(string name, byte[] data, DateTime? modTime = null) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._entries.Add((name, data, modTime, false));
  }

  /// <summary>
  /// Adds an explicit directory marker. Empty leaf directories show up in the
  /// dirblock chain; the reader currently surfaces only top-level entries, so
  /// nested paths are flattened into <c>parent/child</c> filenames at the root
  /// dirblock for round-trip parity with the Stage 1 reader.
  /// </summary>
  public void AddDirectory(string name, DateTime? modTime = null) {
    ArgumentNullException.ThrowIfNull(name);
    this._entries.Add((name, [], modTime, true));
  }

  /// <summary>
  /// Builds and returns the complete PFS3 image. The image size is rounded up
  /// to a whole number of blocks; the last file's payload may push the image
  /// past the conventional 880 KB DD floppy. The reader does not enforce a
  /// fixed image size.
  /// </summary>
  public byte[] Build(string diskName = "DISK") {
    ArgumentNullException.ThrowIfNull(diskName);

    // ── Pass 1: plan the layout. We need to know how many dirblocks the
    // entries will consume so we can pick contiguous data extents past them.
    // Dirblock entries are variable-length; the simplest workable layout is
    //   block 0        boot block
    //   block 1        (padding — reserved, not used by Stage 1 reader)
    //   block _rootBlock  root block
    //   block _rootBlock+1 .. +K   dirblock chain (K chosen so all entries fit)
    //   block _rootBlock+K+1 ..    per-file contiguous data extents
    var dirEntries = this._entries
      .Select(e => new DirEntry(e.Name, e.Data, e.ModTime, e.IsDirectory))
      .ToList();

    // Bucket entries into dirblocks. Each dirblock has (_blockSize - 20) bytes
    // available for entries plus the terminating 0-length sentinel byte.
    var dirblocks = PackEntriesIntoDirBlocks(dirEntries, this._blockSize - 20 - 1);

    // ── Allocate block numbers.
    var firstDirBlock = this._rootBlock + 1u;
    var nextFreeBlock = firstDirBlock + (uint)dirblocks.Count;

    // Each file gets a contiguous run of blocks. AnodeNumber := starting block.
    foreach (var batch in dirblocks)
      foreach (var entry in batch) {
        if (entry.IsDirectory) {
          entry.AnodeNumber = 0; // unused — directories surfaced as names only
          continue;
        }
        if (entry.Data.Length == 0) {
          // Zero-byte files still need a valid block reference so anode != 0,
          // otherwise the reader's chain-terminator check skips them.
          entry.AnodeNumber = nextFreeBlock++;
          continue;
        }
        var blocks = (entry.Data.Length + this._blockSize - 1) / this._blockSize;
        entry.AnodeNumber = nextFreeBlock;
        nextFreeBlock += (uint)blocks;
      }

    var imageSize = (int)nextFreeBlock * this._blockSize;
    if (imageSize < (int)(this._rootBlock + 1) * this._blockSize)
      imageSize = (int)(this._rootBlock + 1) * this._blockSize;
    var image = new byte[imageSize];

    // ── Boot block: signature + root-block pointer.
    image[0] = (byte)'P';
    image[1] = (byte)'F';
    image[2] = (byte)'S';
    image[3] = 0x03;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8), this._rootBlock);

    // ── Root block: BCPL disk name + first-dirblock pointer.
    var rb = (int)(this._rootBlock * this._blockSize);
    if (rb + this._blockSize > image.Length)
      throw new InvalidOperationException("AmigaPFS: image too small for root block — increase block size or reduce content.");
    WriteBcplString(image, rb + 26, diskName, MaxDiskNameLength);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(rb + 60), firstDirBlock);

    // Identity stamp so external readers can recognize the volume even when
    // they don't honour the boot-block signature.
    image[rb + 0] = (byte)'P';
    image[rb + 1] = (byte)'F';
    image[rb + 2] = (byte)'S';
    image[rb + 3] = 0x03;

    // ── Dirblock chain: emit dirblocks back-to-back, each pointing to the
    // next via the +12 chain pointer.
    for (var i = 0; i < dirblocks.Count; i++) {
      var blockNum = firstDirBlock + (uint)i;
      var off = (int)(blockNum * this._blockSize);
      var nextChain = i + 1 < dirblocks.Count ? firstDirBlock + (uint)(i + 1) : 0u;
      WriteDirBlock(image, off, dirblocks[i], nextChain, this._rootBlock);
    }

    // ── File data: write each file's payload at AnodeNumber * BlockSize.
    foreach (var batch in dirblocks)
      foreach (var entry in batch) {
        if (entry.IsDirectory || entry.Data.Length == 0)
          continue;
        var off = (int)(entry.AnodeNumber * this._blockSize);
        if (off + entry.Data.Length > image.Length)
          throw new InvalidOperationException(
            $"AmigaPFS: file '{entry.LeafName}' extent ({entry.Data.Length} bytes at offset {off}) exceeds image size.");
        entry.Data.CopyTo(image.AsSpan(off));
      }

    return image;
  }

  /// <summary>
  /// Packs <paramref name="entries"/> into one or more dirblocks where every
  /// entry's serialized length fits the <paramref name="payloadBudget"/> per
  /// block. A directory split is performed when adding the next entry would
  /// overflow the per-block budget.
  /// </summary>
  private static List<List<DirEntry>> PackEntriesIntoDirBlocks(List<DirEntry> entries, int payloadBudget) {
    var dirblocks = new List<List<DirEntry>>();
    var current = new List<DirEntry>();
    var used = 0;
    foreach (var entry in entries) {
      var len = entry.SerializedLength;
      if (len > payloadBudget)
        throw new InvalidOperationException(
          $"AmigaPFS: entry '{entry.LeafName}' takes {len} bytes which exceeds the per-dirblock budget of {payloadBudget}.");
      if (used + len > payloadBudget) {
        dirblocks.Add(current);
        current = [];
        used = 0;
      }
      current.Add(entry);
      used += len;
    }
    if (current.Count > 0 || dirblocks.Count == 0)
      dirblocks.Add(current);
    return dirblocks;
  }

  private static void WriteDirBlock(byte[] image, int off, List<DirEntry> entries, uint nextChain, uint parent) {
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + 0), DirBlockId);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 4), ToAmigaDatestamp(DateTime.UtcNow));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 12), nextChain);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 16), parent);

    var cursor = off + 20;
    foreach (var entry in entries) {
      var len = entry.SerializedLength;
      image[cursor + 0] = (byte)len;
      image[cursor + 1] = entry.TypeByte;
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(cursor + 2), entry.AnodeNumber);
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(cursor + 6), (uint)entry.Data.Length);
      var (date, time1, time2) = ToAmigaDateTime(entry.ModTime ?? DateTime.UtcNow);
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(cursor + 10), date);
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(cursor + 12), time1);
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(cursor + 14), time2);
      var nameBytes = entry.LeafNameBytes;
      image[cursor + 16] = (byte)nameBytes.Length;
      nameBytes.CopyTo(image.AsSpan(cursor + 17));
      image[cursor + 17 + nameBytes.Length] = 0; // zero-length trailing comment
      cursor += len;
    }
    // Sentinel: leave the next entry's length byte as 0 (image is zero-initialised).
  }

  private static void WriteBcplString(byte[] image, int offset, string s, int maxLen) {
    var bytes = Encoding.ASCII.GetBytes(s);
    if (bytes.Length > maxLen) bytes = bytes.AsSpan(0, maxLen).ToArray();
    image[offset] = (byte)bytes.Length;
    bytes.CopyTo(image.AsSpan(offset + 1));
  }

  /// <summary>Encodes a UTC <paramref name="dt"/> as the PFS3 packed date+two-times triple.</summary>
  private static (ushort date, ushort time1, ushort time2) ToAmigaDateTime(DateTime dt) {
    var epoch = new DateTime(1978, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var local = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
    if (local < epoch) local = epoch;
    var days = (local - epoch).Days;
    var secsInDay = (int)((local - epoch).TotalSeconds - days * 86400.0);
    var mins = secsInDay / 60;
    var ticks = secsInDay % 60 * 50; // AmigaDOS clock-ticks (50/sec)
    return ((ushort)(days & 0xFFFF), (ushort)(mins & 0xFFFF), (ushort)(ticks & 0xFFFF));
  }

  /// <summary>Packs a UTC <paramref name="dt"/> into the 32-bit datestamp the dirblock header carries at +4.</summary>
  private static uint ToAmigaDatestamp(DateTime dt) {
    var epoch = new DateTime(1978, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var local = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
    if (local < epoch) local = epoch;
    return (uint)(local - epoch).TotalSeconds;
  }

  /// <summary>Per-entry layout cache: serialized length is computed once and reused
  /// when packing into dirblocks and when emitting the on-disk bytes.</summary>
  private sealed class DirEntry {
    public string FullName { get; }
    public byte[] Data { get; }
    public DateTime? ModTime { get; }
    public bool IsDirectory { get; }
    public string LeafName { get; }
    public byte[] LeafNameBytes { get; }
    public uint AnodeNumber { get; set; }

    public DirEntry(string fullName, byte[] data, DateTime? modTime, bool isDirectory) {
      this.FullName = fullName.Replace('\\', '/').TrimStart('/');
      this.Data = data;
      this.ModTime = modTime;
      this.IsDirectory = isDirectory;
      // Stage 1 reader flattens nested paths into the root dirblock by keeping
      // the full slash-separated name as the entry leaf — round-trip parity
      // requires the same treatment on the writer side.
      this.LeafName = this.FullName;
      var nameBytes = Encoding.ASCII.GetBytes(this.LeafName);
      if (nameBytes.Length > MaxFilenameLength)
        nameBytes = nameBytes.AsSpan(0, MaxFilenameLength).ToArray();
      this.LeafNameBytes = nameBytes;
    }

    /// <summary>Entry on-disk length, matching the reader's parse: 17 header bytes + name + 1 comment-length byte.</summary>
    public int SerializedLength => 17 + this.LeafNameBytes.Length + 1;

    public byte TypeByte => this.IsDirectory ? (byte)0x80 : (byte)0x20;
  }
}
