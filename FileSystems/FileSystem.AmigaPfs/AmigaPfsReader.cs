#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.AmigaPfs;

/// <summary>
/// Reader for Amiga Professional File System (PFS3 / PFS3aio) — a high
/// performance Amiga filesystem authored by Michiel Pelt &amp; Toni Wilen.
///
/// On-disk layout (BIG-endian, 512-byte blocks on floppy, configurable on HD):
///   Block 0..1   boot block — first 4 bytes are the disk signature:
///                "PFS\x02" (older PFS2), "PFS\x03" (PFS3) or "PFSa" (PFS3aio).
///                Bytes 2-5 of byte 0..3 of block 0 may also carry "muFS"
///                (multi-user fs variant). For this reader we accept the
///                3 standard PFS signatures.
///
///   Root block   typically block 80 on a floppy (located via the rootblock
///                pointer in the bootblock). The root block carries:
///                  +0   ID            4 bytes "PFS\x02"/"PFS\x03"/"PFSa"
///                  +12  rblkcluster   u16
///                  +14  blocknr       u32
///                  +18  datestamp     u32
///                  +22  options       u32
///                  +26  diskname      32 bytes (null-padded ASCII)
///                  +60  rootinfo      (anode pointers)
///                Subsequent fields point to "anode blocks" and "dirblocks".
///
/// PFS uses a tree of "anodes" — each 4-byte allocation entry pointing to a
/// next block in a file or to the next anode in the chain. A directory is a
/// linked list of "dirblocks", each containing variable-length entries with
/// the filename, anode number, file size, and protection bits.
///
/// This Stage 1 reader walks the bootblock + root block, identifies the
/// first dirblock chain, and extracts simple file entries that fit in a
/// single block reference (no fragmented file traversal across multiple
/// anodes).  Real-world PFS3 multi-block files require full anode-tree
/// traversal which is deferred to Stage 2.
///
/// Spec source: https://github.com/tonioni/AmigaPFS — public PFS3aio reference
/// implementation; Michiel Pelt's original PFS Technical Note (1995).
/// </summary>
public sealed class AmigaPfsReader : IDisposable {

  private readonly ImageAccessor _accessor;
  private readonly List<AmigaPfsEntry> _entries = [];

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<AmigaPfsEntry> Entries => this._entries;

  /// <summary>4-byte signature found in the boot block: "PFS\x02", "PFS\x03" or "PFSa".</summary>
  public string Signature { get; private set; } = "";

  /// <summary>Block size, default 512 bytes for floppy. Override by passing into the reader.</summary>
  public int BlockSize { get; private set; } = 512;

  /// <summary>Disk name from the root block.</summary>
  public string DiskName { get; private set; } = "";

    /// <summary>
  /// Initializes a new instance of <see cref="AmigaPfsReader"/>.
  /// </summary>
public AmigaPfsReader(Stream stream, int blockSize = 512) {
    ArgumentNullException.ThrowIfNull(stream);
    if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be a positive power of two.");
    this.BlockSize = blockSize;
    // Blocks are pulled on demand: a PFS volume's metadata is a handful of
    // blocks however large the payload area behind it grows.
    this._accessor = new ImageAccessor(stream);
    this.Parse();
  }

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._accessor.Length;

  private void Parse() {
    if (this._accessor.Length < this.BlockSize * 2L)
      throw new InvalidDataException("AmigaPFS: image too small for boot block.");
    // Check signature in boot block (block 0).
    var boot = this._accessor.Read(0, Math.Min(this.BlockSize, 64));
    var sig = Encoding.ASCII.GetString(boot, 0, 4);
    if (sig is not ("PFS\x02" or "PFS\x03" or "PFSa"))
      throw new InvalidDataException($"AmigaPFS: invalid boot signature '{sig}' (expected PFS\\x02/PFS\\x03/PFSa).");
    this.Signature = sig;

    // The boot block points to the root block at byte offset 8 (u32 BE).
    var rootBlockNum = BinaryPrimitives.ReadUInt32BigEndian(boot.AsSpan(8));
    if (rootBlockNum == 0) rootBlockNum = 80; // floppy default
    var rootOffset = (long)rootBlockNum * this.BlockSize;
    if (rootOffset + this.BlockSize > this._accessor.Length) return;
    var rootBlock = this._accessor.Read(rootOffset, this.BlockSize);

    // Disk name at +26 (32 bytes) within the root block — try to read it.
    var diskName = ReadBcplString(rootBlock, 26, 32);
    this.DiskName = diskName;

    // First dirblock pointer at +60 (u32 BE).
    var firstDirBlock = BinaryPrimitives.ReadUInt32BigEndian(rootBlock.AsSpan(60));
    if (firstDirBlock == 0) return;

    this.WalkDirectoryChain(firstDirBlock, "");
  }

  private void WalkDirectoryChain(uint firstBlock, string path) {
    var blockNum = firstBlock;
    var seen = new HashSet<uint>();
    while (blockNum != 0 && seen.Add(blockNum)) {
      var off = (long)blockNum * this.BlockSize;
      if (off + this.BlockSize > this._accessor.Length) break;
      var dirBlock = this._accessor.Read(off, this.BlockSize).AsSpan();
      // dirblock header (PFS3aio dirblock_t):
      //   +0  id       u16 (=0xC4)
      //   +2  not_used u16
      //   +4  datestamp u32
      //   +8  not_used u32
      //   +12 anodenr  u32 (chain to next dirblock)
      //   +16 parent   u32
      //   +20..       entries — variable size
      var id = BinaryPrimitives.ReadUInt16BigEndian(dirBlock);
      if (id != 0xC4 && id != 0xCC) break; // PFS3 dirblock IDs
      var nextChain = BinaryPrimitives.ReadUInt32BigEndian(dirBlock.Slice(12));
      // Parse entries starting at +20.
      var entryOff = 20;
      while (entryOff < this.BlockSize) {
        var entryLen = dirBlock[entryOff];
        if (entryLen == 0) break;
        if (entryOff + entryLen > this.BlockSize) break;
        // Entry structure (variable-length, big-endian):
        //   +0   next   u8  entry length (incl. this byte)
        //   +1   type   u8  bit 7 = directory, bits 0-6 = protection
        //   +2   anode  u32 anode number for file/dir start
        //   +6   fsize  u32 file size
        //   +10  date   u16
        //   +12  time1  u16
        //   +14  time2  u16
        //   +16  nameLen u8
        //   +17  name   nameLen bytes
        //   +17+nameLen  comment len u8 + comment bytes
        if (entryLen < 17) { entryOff += entryLen; continue; }
        var type = dirBlock[entryOff + 1];
        var isDir = (type & 0x80) != 0;
        var anode = BinaryPrimitives.ReadUInt32BigEndian(dirBlock.Slice(entryOff + 2));
        var size = BinaryPrimitives.ReadUInt32BigEndian(dirBlock.Slice(entryOff + 6));
        var nameLen = dirBlock[entryOff + 16];
        if (entryOff + 17 + nameLen > this.BlockSize) { entryOff += entryLen; continue; }
        var name = Encoding.ASCII.GetString(dirBlock.Slice(entryOff + 17, nameLen));
        if (!string.IsNullOrEmpty(name) && name is not "." and not "..") {
          var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
          this._entries.Add(new AmigaPfsEntry {
            Name = fullPath,
            Size = isDir ? 0 : size,
            AnodeNumber = anode,
            IsDirectory = isDir,
          });
        }
        entryOff += entryLen;
      }
      blockNum = nextChain;
    }
  }

  private static string ReadBcplString(byte[] data, int offset, int maxLen) {
    if (offset >= data.Length) return "";
    var nameLen = data[offset];
    if (nameLen == 0 || nameLen > maxLen - 1) return "";
    var end = Math.Min(offset + 1 + nameLen, data.Length);
    return Encoding.ASCII.GetString(data, offset + 1, end - offset - 1);
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(AmigaPfsEntry entry) {
    var (offset, take) = this.Locate(entry);
    if (take <= 0) return [];
    if (take > Array.MaxLength)
      throw new IOException(
        $"AmigaPFS: '{entry.Name}' is {take:N0} bytes, past the array limit; use ExtractTo.");
    return this._accessor.Read(offset, (int)take);
  }

  /// <summary>Copies <paramref name="entry" />'s bytes into <paramref name="destination" />.</summary>
  public long ExtractTo(AmigaPfsEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(destination);
    var (offset, take) = this.Locate(entry);
    if (take <= 0) return 0;
    this._accessor.CopyTo(offset, destination, take);
    return take;
  }

  /// <summary>
  /// Resolves an entry to its byte range. Stage 1 treats the anode number as a
  /// direct block number: real PFS3 anodes index an anode table, which this
  /// reader does not walk, and the writer lays every file out to match.
  /// </summary>
  public (long Offset, long Length) Locate(AmigaPfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return (0, 0);
    var offset = (long)entry.AnodeNumber * this.BlockSize;
    if (offset < 0 || offset >= this._accessor.Length) return (0, 0);
    return (offset, Math.Min(entry.Size, this._accessor.Length - offset));
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() => this._accessor.Dispose();
}
