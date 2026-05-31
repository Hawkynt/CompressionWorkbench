#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Hpfs;

/// <summary>
/// Builds a minimal HPFS (OS/2 High Performance File System) image from scratch.
/// Layout:
///   LBA  0:       Boot sector (BPB + OEM ID)
///   LBA 16:       Superblock (8-byte magic + root fnode LBA + total sectors + bitmap start)
///   LBA 17:       Spare block (8-byte magic, minimal)
///   LBA 18:       Root fnode (magic + direct alloc pointing to root dir block)
///   LBA 20..23:   Root directory block (2048 bytes = 4 LBAs, with dir entries)
///   LBA 24:       Bitmap band 0 (allocation bitmap for the whole volume)
///   LBA 32+:      Per-directory fnodes + dir blocks, then file fnodes and data.
///
/// Directories are honoured: a name passed to <see cref="AddFile"/> may contain
/// '/' (or '\') separators; each path segment becomes a real HPFS directory
/// (an fnode with the directory flag, referenced by a directory-flagged dirent
/// in the parent's dirent block, with its own dirent block).
///
/// Limitations: a single 2 KiB dirent block per directory (no dirent-block
/// B-tree spill), direct allocation only (no AllocSec B-tree), single bitmap
/// band. Practical ceiling is roughly 60 small-named entries per directory.
/// </summary>
internal sealed class HpfsWriter {

  private readonly List<(string Name, byte[] Data)> _files = [];

  internal const int LbaSize = 512;
  internal const int DirBlockLbas = 4; // 2048 bytes per dir block
  internal const int DirBlockSize = LbaSize * DirBlockLbas;

  // Dirent flag bits.
  private const ushort DirentFlagSpecial = 0x0001; // end-of-block sentinel / ".."
  private const ushort DirentFlagDirectory = 0x0008;

  // Fixed layout LBAs
  private const uint BootLba = 0;
  private const uint SuperblockLba = 16;
  private const uint SpareBlockLba = 17;
  private const uint RootFnodeLba = 18;
  private const uint RootDirLba = 20; // 4 LBAs = 2048 bytes
  private const uint BitmapLba = 24;  // 1 LBA for allocation bitmap
  private const uint FirstAllocLba = 32;

  // Magics
  private static readonly byte[] SuperblockMagic = [0xF9, 0x95, 0xE8, 0xF9, 0xFA, 0x53, 0xE9, 0xF9];
  private static readonly byte[] SpareBlockMagic = [0xF9, 0x11, 0xDC, 0x39, 0xFA, 0x93, 0xB8, 0xF9];
  private static readonly byte[] FnodeMagic = [0xF7, 0xE4, 0x0A, 0xAE];
  private static readonly byte[] DirBlockMagic = [0x77, 0xE4, 0x0A, 0xAE];

  /// <summary>A node in the directory tree assembled before layout.</summary>
  private sealed class TreeNode {
    public required string Name;
    public bool IsDirectory;
    public byte[] Data = [];

    // Children of a directory, keyed by lower-cased segment name (HPFS is
    // case-insensitive but case-preserving; dirents are sorted by name).
    public readonly SortedDictionary<string, TreeNode> Children =
      new(StringComparer.OrdinalIgnoreCase);

    // Filled in during the layout pass.
    public uint FnodeLba;       // fnode for this entry (file or directory)
    public uint DirBlockLba;    // directory's own dirent block (directories only)
    public uint DataLba;        // first data LBA (files only)
    public uint DataLenLbas;    // data length in LBAs (files only)
  }

  /// <summary>
  /// Adds a file to the image. The name may contain '/' or '\' separators; each
  /// segment becomes a real HPFS directory and the file lands at the nested path.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (string.IsNullOrEmpty(Path.GetFileName(name.Replace('\\', '/'))))
      throw new ArgumentException("File name must not be empty.", nameof(name));
    _files.Add((name, data));
  }

  /// <summary>Builds the HPFS image and returns the raw bytes.</summary>
  public byte[] Build() {
    var root = BuildTree();

    // Layout pass: assign LBAs depth-first. For each directory we reserve an
    // fnode (1 LBA) and a dirent block (DirBlockLbas). For each file we reserve
    // an fnode (1 LBA) and its data (rounded up to whole LBAs). The root's fnode
    // and dirent block sit at their fixed LBAs; everything else flows from
    // FirstAllocLba.
    var nextLba = FirstAllocLba;
    root.FnodeLba = RootFnodeLba;
    root.DirBlockLba = RootDirLba;
    AssignLbas(root, ref nextLba, isRoot: true);

    var totalLbas = Math.Max(nextLba, 128u); // minimum 64 KB image
    var image = new byte[(long)totalLbas * LbaSize];

    WriteBootSector(image);
    WriteSuperblock(image, totalLbas);
    WriteSpareBlock(image);
    WriteBitmap(image, nextLba);

    // Emit the whole tree (fnodes, dir blocks, file data).
    WriteNode(image, root, parentFnodeLba: RootFnodeLba);

    return image;
  }

  /// <summary>Writes the image to a stream.</summary>
  public void WriteTo(Stream output) {
    var data = Build();
    output.Write(data, 0, data.Length);
  }

  /// <summary>Assembles the flat file list into a directory tree.</summary>
  private TreeNode BuildTree() {
    var root = new TreeNode { Name = "", IsDirectory = true };

    foreach (var (rawName, data) in _files) {
      var normalized = rawName.Replace('\\', '/').Trim('/');
      var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segments.Length == 0) continue;

      var cursor = root;
      for (var i = 0; i < segments.Length - 1; i++) {
        var seg = segments[i];
        if (!cursor.Children.TryGetValue(seg, out var child)) {
          child = new TreeNode { Name = seg, IsDirectory = true };
          cursor.Children[seg] = child;
        }
        child.IsDirectory = true;
        cursor = child;
      }

      var leaf = segments[^1];
      // Last writer wins on a name clash; ignore a file colliding with a dir.
      cursor.Children[leaf] = new TreeNode { Name = leaf, IsDirectory = false, Data = data };
    }

    return root;
  }

  /// <summary>Depth-first LBA assignment for the whole tree.</summary>
  private void AssignLbas(TreeNode node, ref uint nextLba, bool isRoot) {
    foreach (var child in node.Children.Values) {
      child.FnodeLba = nextLba++;
      if (child.IsDirectory) {
        child.DirBlockLba = nextLba;
        nextLba += DirBlockLbas;
        AssignLbas(child, ref nextLba, isRoot: false);
      } else {
        var dataLbas = (uint)((child.Data.Length + LbaSize - 1) / LbaSize);
        child.DataLenLbas = dataLbas;
        child.DataLba = nextLba;
        nextLba += dataLbas;
      }
    }
  }

  /// <summary>Emits the fnode, dirent block (for directories) and data (for files)
  /// of <paramref name="node"/> and recurses into its children.</summary>
  private void WriteNode(byte[] image, TreeNode node, uint parentFnodeLba) {
    if (node.IsDirectory) {
      WriteDirFnode(image, node.FnodeLba, node.DirBlockLba, parentFnodeLba);
      WriteDirBlock(image, node);
      foreach (var child in node.Children.Values)
        WriteNode(image, child, parentFnodeLba: node.FnodeLba);
    } else {
      WriteFileFnode(image, node.FnodeLba, node.DataLba, node.DataLenLbas, parentFnodeLba);
      if (node.Data.Length > 0)
        Buffer.BlockCopy(node.Data, 0, image, (int)(node.DataLba * LbaSize), node.Data.Length);
    }
  }

  private static void WriteBootSector(byte[] image) {
    // OEM ID at offset 3: "IBM 20.0" is a classic HPFS identifier
    Encoding.ASCII.GetBytes("IBM 20.0").CopyTo(image.AsSpan(3, 8));
    // Bytes per sector at offset 11 (u16 LE)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(11, 2), LbaSize);
    // Boot signature at offset 510
    image[510] = 0x55;
    image[511] = 0xAA;
  }

  private void WriteSuperblock(byte[] image, uint totalSectors) {
    var off = (int)(SuperblockLba * LbaSize);

    // 8-byte magic
    SuperblockMagic.CopyTo(image.AsSpan(off, 8));

    // Version at offset 8 (u8): 2 = HPFS
    image[off + 8] = 2;

    // Functional version at offset 9: 2
    image[off + 9] = 2;

    // Root fnode LBA at offset 12
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 12, 4), RootFnodeLba);

    // Total sectors at offset 16
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 16, 4), totalSectors);

    // Number of bad sectors at offset 20: 0
    // Bitmap start LBA at offset 24
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 24, 4), BitmapLba);

    // Spare block LBA at offset 28
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 28, 4), SpareBlockLba);
  }

  private static void WriteSpareBlock(byte[] image) {
    var off = (int)(SpareBlockLba * LbaSize);
    // 8-byte spare block magic
    SpareBlockMagic.CopyTo(image.AsSpan(off, 8));
    // Rest is zeroed (no hot-fix entries, no dirty flags)
  }

  /// <summary>Writes a directory fnode whose first direct-allocation entry points
  /// at the directory's dirent block.</summary>
  private static void WriteDirFnode(byte[] image, uint fnodeLba, uint dirBlockLba, uint parentFnodeLba) {
    var off = (int)(fnodeLba * LbaSize);

    FnodeMagic.CopyTo(image.AsSpan(off, 4));

    // Parent-directory fnode LBA at offset 0x0C (used for ".." resolution).
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x0C, 4), parentFnodeLba);

    // Flag this fnode as a directory at offset 0x20 (bit 0). Real HPFS keeps a
    // directory flag in the fnode; we mirror it so readers can corroborate the
    // dirent's directory bit.
    image[off + 0x20] = 0x01;

    // AllocSec header at 0xC0: height=0 (direct list, already zeroed).
    // First direct-allocation entry at 0xC4: points at the dirent block.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 0, 4), 0);            // logical offset
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 4, 4), DirBlockLbas); // length in sectors
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 8, 4), dirBlockLba);  // physical LBA
  }

  /// <summary>Writes a directory's 2 KiB dirent block: one sorted dirent per child
  /// followed by the end-of-block sentinel.</summary>
  private void WriteDirBlock(byte[] image, TreeNode dir) {
    var off = (int)(dir.DirBlockLba * LbaSize);

    DirBlockMagic.CopyTo(image.AsSpan(off, 4));

    // Dirents start at offset 0x14 (20) into the block. Children are already
    // sorted by name via the SortedDictionary.
    var cursor = off + 0x14;
    var blockEnd = off + DirBlockSize;

    foreach (var child in dir.Children.Values) {
      var nameBytes = Encoding.Latin1.GetBytes(child.Name);
      if (nameBytes.Length > 254) nameBytes = nameBytes[..254];

      // Record layout:
      //   0: u16 recLen
      //   2: u16 flags (bit 3 = directory)
      //   4: u32 fnodeLba
      //  12: u32 fileSize (0 for directories)
      //  30: u8 nameLen
      //  31: name bytes
      var recLen = 32 + nameBytes.Length;
      if ((recLen & 3) != 0) recLen = (recLen + 3) & ~3;

      if (cursor + recLen + 32 > blockEnd)
        break; // No room for this entry + sentinel; stop adding.

      var flags = child.IsDirectory ? DirentFlagDirectory : (ushort)0;

      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cursor, 2), (ushort)recLen);
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cursor + 2, 2), flags);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cursor + 4, 4), child.FnodeLba);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cursor + 12, 4),
        child.IsDirectory ? 0u : (uint)child.Data.Length);
      image[cursor + 30] = (byte)nameBytes.Length;
      nameBytes.CopyTo(image.AsSpan(cursor + 31, nameBytes.Length));

      cursor += recLen;
    }

    // End-of-block sentinel dirent.
    if (cursor + 32 <= blockEnd) {
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cursor, 2), 32); // min record length
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cursor + 2, 2), DirentFlagSpecial);
    }
  }

  private static void WriteFileFnode(byte[] image, uint fnodeLba, uint dataLba, uint dataLenLbas, uint parentFnodeLba) {
    var off = (int)(fnodeLba * LbaSize);

    FnodeMagic.CopyTo(image.AsSpan(off, 4));

    // Parent-directory fnode LBA at offset 0x0C.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x0C, 4), parentFnodeLba);

    // AllocSec header at 0xC0: height=0 (direct list, already zeroed).
    // First direct-allocation entry at 0xC4.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 0, 4), 0);           // logical offset
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 4, 4), dataLenLbas); // length
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 8, 4), dataLba);     // physical LBA
  }

  private static void WriteBitmap(byte[] image, uint usedLbas) {
    var off = (int)(BitmapLba * LbaSize);
    // HPFS bitmap: 1 bit per sector, bit=1 means FREE, bit=0 means USED.
    // Fill the entire LBA with 0xFF (all free) then clear bits for used sectors.
    for (var i = off; i < off + LbaSize; i++)
      image[i] = 0xFF;

    // Mark used sectors (bits 0..usedLbas-1) as allocated (bit=0)
    for (var i = 0u; i < usedLbas && i < LbaSize * 8; i++) {
      var byteIdx = (int)(i / 8);
      var bitIdx = (int)(i % 8);
      image[off + byteIdx] &= (byte)~(1 << bitIdx); // Clear bit = used
    }
  }
}
