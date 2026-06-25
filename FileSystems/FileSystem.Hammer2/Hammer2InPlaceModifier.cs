#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Hammer2;

/// <summary>
/// Genuine in-place (copy-on-write) add / replace / remove for HAMMER2 images
/// produced by <see cref="Hammer2Writer"/>. HAMMER2 is itself a copy-on-write
/// filesystem, so the honest minimal edit is a CoW append: every block that the
/// mutation does not logically rewrite — including all existing file data — is
/// left byte-identical at its original device offset; only the changed file's
/// blocks plus the short fixed-depth chain above it (labelled-PFS inode →
/// super-root inode → volume header) are written afresh and appended past the
/// current topology high-water.
/// </summary>
/// <remarks>
/// <para>The writer lays a shallow, fixed-depth chain:</para>
/// <list type="bullet">
///   <item>volume header slot #0 — <c>sroot_blockset[0]</c> references the
///     super-root inode and carries the per-sector iCRCs + whole-header iCRC;</item>
///   <item>super-root inode — its embedded blockset references the PFS-MASTER
///     inodes ("LOCAL" + the labelled PFS), each bref carrying an xxHash64 check
///     of the referenced inode;</item>
///   <item>labelled-PFS inode — its embedded 4-entry blockset holds INODE brefs
///     (key = inum) and DIRENT brefs (key = dirhash), spilling into a single
///     HAMMER2_BREF_TYPE_INDIRECT block when more than four entries are present;</item>
///   <item>file inode — embedded direct data (≤512 B) or one out-of-line DATA
///     block.</item>
/// </list>
/// <para>Because there is no freemap to maintain (newfs_hammer2 builds it lazily
/// and the writer emits none), new blocks are bump-allocated past the highest
/// device offset any reachable block currently occupies, naturally aligned to
/// their radix — exactly the writer's allocation discipline. The volume is sized
/// far above the topology (256 MB vs ~20 MB used) so appends fit without growing
/// the image, leaving the image length unchanged.</para>
/// <para>When the labelled-PFS blockset would need anything beyond a single
/// inline set or one indirect block (i.e. the existing image already spilled into
/// nested indirects, or the new entry count exceeds one 4 KB indirect's 32-bref
/// fanout per key-half), this modifier returns false and the caller falls back to
/// the verified rebuild path so the user still gets a correct image.</para>
/// </remarks>
internal static class Hammer2InPlaceModifier {
  private const ulong VolumeIdHbo = 0x48414d3205172011UL;
  private const ulong VolumeIdAbo = 0x11201705324d4148UL;
  private const int VolumeBytes = 65536;
  private const int NumVolhdrs = 4;
  private const int BlockrefBytes = 128;
  private const int SetCount = 4;
  private const int InodeBytes = 1024;
  private const int RadixForInode = 10;

  private const int CheckXxhash64 = 3;
  private const int CompNone = 0;
  private const int CompAutozero = 1;

  private const byte BrefTypeInode = 1;
  private const byte BrefTypeIndirect = 2;
  private const byte BrefTypeData = 3;
  private const byte BrefTypeDirent = 4;
  private const byte BrefFlagPfsroot = 0x01;

  private const byte ObjTypeRegfile = 2;
  private const byte OpflagDirectData = 0x01;

  private const int EmbeddedDataMax = 512;
  private const long FirstInum = 0x400;
  private const long PfsRootInum = 16;
  private const ushort InodeVersionOne = 1;

  private const int IndRadix = 12;                          // HAMMER2_IND_BYTES = 4 KB
  private const int IndFanout = (1 << IndRadix) / BlockrefBytes; // 32 brefs / indirect

  // ── public entry points ───────────────────────────────────────────────────

  public static void Add(
      Stream archive,
      IReadOnlyList<ArchiveInputInfo> inputs,
      Action<Stream, IReadOnlyList<ArchiveInputInfo>> rebuild) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(rebuild);

    var payloads = new List<(string Name, byte[] Data)>();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      payloads.Add((name, data));
    if (payloads.Count == 0)
      return;

    var image = ReadAll(archive);
    if (!TryMutate(ref image, add: payloads, remove: null)) {
      rebuild(archive, inputs);
      return;
    }
    Commit(archive, image);
  }

  public static void Remove(
      Stream archive,
      string[] entryNames,
      Action<Stream, string[]> rebuild) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    ArgumentNullException.ThrowIfNull(rebuild);

    if (entryNames.Length == 0)
      return;

    var image = ReadAll(archive);
    if (!TryMutate(ref image, add: null, remove: entryNames)) {
      rebuild(archive, entryNames);
      return;
    }
    Commit(archive, image);
  }

  private static byte[] ReadAll(Stream archive) {
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    return ms.ToArray();
  }

  private static void Commit(Stream archive, byte[] image) {
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  // ── the CoW mutation ──────────────────────────────────────────────────────

  // An existing root child: either a file (inode + its dirent) or, defensively,
  // a stray entry we keep verbatim. We model the directory as a list of files.
  private sealed class FileEntry {
    public required string Name;
    public required ulong Inum;
    public required byte[] Inode;     // full 1024-byte inode block
    public required byte[] Content;   // materialised file bytes
    public long OnDiskOff;            // device offset of the inode block (0 = freshly appended)
  }

  private static bool TryMutate(ref byte[] image,
      List<(string Name, byte[] Data)>? add, string[]? remove) {
    if (!TryLocate(image, out var loc))
      return false;

    // Read every existing file under the labelled PFS root.
    if (!TryReadRootFiles(image, loc, out var files))
      return false;

    // Apply the requested mutation against the working file list.
    if (add != null) {
      foreach (var (name, data) in add) {
        if (name.Contains('/') || name.Contains('\\'))
          return false;                                   // root-only scope
        var idx = files.FindIndex(f =>
          string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
          files.RemoveAt(idx);                            // replace by name
      }
    }
    if (remove != null) {
      var skip = new HashSet<string>(remove, StringComparer.OrdinalIgnoreCase);
      files.RemoveAll(f => skip.Contains(f.Name));
    }

    // The append allocator starts past every reachable block's device offset.
    var bump = loc.TopologyHighWater;
    var builder = new List<(long Off, byte[] Block)>();

    long Allocate(byte[] block, int radix) {
      var size = 1L << radix;
      bump = AlignUp(bump, size);
      var off = bump;
      bump += size;
      builder.Add((off, block));
      return EncodeDataOff(off, radix);
    }

    // Highest inum currently in use (so new files get fresh numbers).
    var maxInum = files.Count == 0 ? (ulong)(FirstInum - 1)
      : files.Max(f => f.Inum);
    maxInum = Math.Max(maxInum, (ulong)(FirstInum - 1));

    // Materialise newly-added files: build a fresh REGFILE inode (+ data block)
    // and append it. Existing files keep their original inode blocks untouched.
    if (add != null) {
      foreach (var (name, data) in add) {
        var inum = ++maxInum;
        var content = data ?? [];
        var inode = BuildRegFileInode(inum, content, Allocate);
        files.Add(new FileEntry { Name = name, Inum = inum, Inode = inode, Content = content });
        // The newly-built inode block is appended; its bref check is computed below.
      }
    }

    // Rebuild the labelled-PFS root blockset from the working file list.
    var brefs = new List<(ulong Key, byte[] Bref)>();
    foreach (var f in files) {
      // Locate / place the inode block. Existing files already sit on disk at a
      // known offset; new files were just appended.
      long inodeDataOff;
      var inodeCheck = XxCheck(f.Inode);
      if (f.OnDiskOff != 0) {
        // Existing file — its inode block stays byte-identical at its offset.
        inodeDataOff = EncodeDataOff(f.OnDiskOff, RadixForInode);
      } else {
        // Newly added file — append its inode block now (its data block, if any,
        // was already appended when the inode was built).
        inodeDataOff = Allocate(f.Inode, RadixForInode);
      }

      var inodeBref = new byte[BlockrefBytes];
      WriteBlockref(inodeBref, BrefTypeInode, CheckXxhash64, CompNone, flags: 0,
        key: f.Inum, vradix: 0, dataOff: inodeDataOff, check: inodeCheck,
        mirrorTid: loc.NewMirrorTid);
      brefs.Add((f.Inum, inodeBref));

      var (dKey, dBref) = BuildDirentBref(f.Name, f.Inum, loc.NewMirrorTid);
      brefs.Add((dKey, dBref));
    }

    // Lay the brefs into the new labelled-PFS inode's blockset (inline or one
    // indirect). Refuse (→ rebuild) when the layout would need nested indirects.
    var newPfsInode = (byte[])loc.LabelledInode.Clone();
    Array.Clear(newPfsInode, 0x200, SetCount * BlockrefBytes);
    if (!TryLayoutBlockset(newPfsInode.AsSpan(0x200, SetCount * BlockrefBytes), brefs,
        loc.NewMirrorTid, Allocate))
      return false;

    // Append the new labelled-PFS inode.
    var pfsOff = Allocate(newPfsInode, RadixForInode);
    var pfsCheck = XxCheck(newPfsInode);

    // Build the new super-root inode: clone the existing one, swap the labelled
    // PFS's bref to point at the new PFS inode (offset + check), keep LOCAL's
    // bref byte-identical. The labelled PFS keeps its name_key so its bref key is
    // unchanged — only data_off + check + mirror_tid move.
    var newSroot = (byte[])loc.SuperRootInode.Clone();
    var patched = false;
    for (var i = 0; i < SetCount; ++i) {
      var bp = 0x200 + i * BlockrefBytes;
      if (newSroot[bp] != BrefTypeInode)
        continue;
      var off = DecodeOffset(BinaryPrimitives.ReadInt64LittleEndian(newSroot.AsSpan(bp + 32, 8)));
      if (off != loc.LabelledInodeOff)
        continue;
      WriteBlockref(newSroot.AsSpan(bp, BlockrefBytes), BrefTypeInode,
        CheckXxhash64, CompNone, flags: BrefFlagPfsroot,
        key: BinaryPrimitives.ReadUInt64LittleEndian(newSroot.AsSpan(bp + 8, 8)),
        vradix: RadixForInode, dataOff: pfsOff, check: pfsCheck,
        mirrorTid: loc.NewMirrorTid);
      patched = true;
      break;
    }
    if (!patched)
      return false;

    var srootOff = Allocate(newSroot, RadixForInode);
    var srootCheck = XxCheck(newSroot);

    // Everything fits within the existing volume? (We never grow the image.)
    if (bump > image.LongLength)
      return false;

    // Commit appended blocks into the image buffer.
    foreach (var (off, block) in builder) {
      if (off + block.Length > image.LongLength)
        return false;
      block.CopyTo(image.AsSpan((int)off, block.Length));
    }

    // Patch the volume header's sroot bref + bump mirror_tid + refresh CRCs.
    PatchVolumeHeader(image, loc.VolumeHeaderOff, srootOff, srootCheck, loc.NewMirrorTid);
    return true;
  }

  // ── locate the fixed-depth chain ──────────────────────────────────────────

  private sealed class Location {
    public required int VolumeHeaderOff;
    public required byte[] SuperRootInode;
    public required long SuperRootInodeOff;
    public required byte[] LabelledInode;
    public required long LabelledInodeOff;
    public required long TopologyHighWater;
    public required ulong NewMirrorTid;
  }

  private static bool TryLocate(byte[] image, out Location loc) {
    loc = null!;
    var vh = SelectVolumeHeader(image, out var mirrorTid);
    if (vh < 0)
      return false;

    var srootBrefOff = vh + 0x200;
    if (image[srootBrefOff] != BrefTypeInode)
      return false;
    var srootOff = DecodeOffset(BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(srootBrefOff + 32, 8)));
    if (!TryReadBlock(image, srootOff, InodeBytes, out var sroot))
      return false;

    // Find the labelled (non-LOCAL) PFS inode in the super-root blockset.
    long labelledOff = 0;
    byte[]? labelled = null;
    for (var i = 0; i < SetCount; ++i) {
      var bp = 0x200 + i * BlockrefBytes;
      if (sroot[bp] != BrefTypeInode)
        continue;
      var off = DecodeOffset(BinaryPrimitives.ReadInt64LittleEndian(sroot.AsSpan(bp + 32, 8)));
      if (!TryReadBlock(image, off, InodeBytes, out var inode))
        return false;
      var name = ReadInodeName(inode);
      if (string.Equals(name, "LOCAL", StringComparison.Ordinal))
        continue;
      labelledOff = off;
      labelled = inode;
    }
    if (labelled == null)
      return false;

    // Compute the topology high-water: the highest (offset + block-size) over
    // every block reachable from the super-root, so appends never overlap.
    var hwm = 0L;
    void Note(long off, int radix) => hwm = Math.Max(hwm, off + (1L << radix));

    Note(srootOff, RadixForInode);
    for (var i = 0; i < SetCount; ++i) {
      var bp = 0x200 + i * BlockrefBytes;
      if (sroot[bp] != BrefTypeInode)
        continue;
      var off = DecodeOffset(BinaryPrimitives.ReadInt64LittleEndian(sroot.AsSpan(bp + 32, 8)));
      var rad = RadixOf(BinaryPrimitives.ReadInt64LittleEndian(sroot.AsSpan(bp + 32, 8)));
      Note(off, rad == 0 ? RadixForInode : rad);
      if (!TryReadBlock(image, off, InodeBytes, out var pfsInode))
        return false;
      if (!NoteSubtree(image, pfsInode, Note))
        return false;
    }

    loc = new Location {
      VolumeHeaderOff = vh,
      SuperRootInode = sroot,
      SuperRootInodeOff = srootOff,
      LabelledInode = labelled,
      LabelledInodeOff = labelledOff,
      TopologyHighWater = hwm,
      NewMirrorTid = mirrorTid + 1,
    };
    return true;
  }

  // Walk a PFS-root blockset (inline + INDIRECT), noting every referenced block's
  // device extent and the file inodes' data/indirect blocks. Returns false on any
  // structural surprise (so the caller falls back to rebuild).
  private static bool NoteSubtree(byte[] image, byte[] inode, Action<long, int> note) {
    foreach (var (type, _, dataOff, _) in EnumerateBlockset(image, inode, 0x200, SetCount * BlockrefBytes)) {
      var off = DecodeOffset(dataOff);
      var rad = RadixOf(dataOff);
      switch (type) {
        case BrefTypeInode:
          note(off, rad == 0 ? RadixForInode : rad);
          if (!TryReadBlock(image, off, InodeBytes, out var child))
            return false;
          // File inodes may carry an out-of-line DATA block.
          foreach (var (t2, _, d2, _) in EnumerateBlockset(image, child, 0x200, SetCount * BlockrefBytes))
            if (t2 == BrefTypeData)
              note(DecodeOffset(d2), RadixOf(d2));
          break;
        case BrefTypeData:
          note(off, rad);
          break;
        case BrefTypeDirent:
          if (dataOff != 0)
            note(off, rad);     // long-name dirent with an out-of-line name block
          break;
      }
    }
    return true;
  }

  // ── read the existing files under the labelled PFS root ───────────────────

  private static bool TryReadRootFiles(byte[] image, Location loc, out List<FileEntry> files) {
    files = [];
    var inodesByInum = new Dictionary<ulong, (byte[] Inode, long Off)>();
    var dirents = new List<(string Name, ulong Inum)>();

    foreach (var (type, _, dataOff, raw) in
             EnumerateBlockset(image, loc.LabelledInode, 0x200, SetCount * BlockrefBytes)) {
      switch (type) {
        case BrefTypeInode: {
          var off = DecodeOffset(dataOff);
          if (!TryReadBlock(image, off, InodeBytes, out var inode))
            return false;
          var inum = BinaryPrimitives.ReadUInt64LittleEndian(inode.AsSpan(0x58, 8));
          inodesByInum[inum] = (inode, off);
          break;
        }
        case BrefTypeDirent: {
          var (name, inum) = ReadDirent(image, dataOff, raw);
          if (name.Length > 0)
            dirents.Add((name, inum));
          break;
        }
      }
    }

    foreach (var (name, inum) in dirents) {
      if (!inodesByInum.TryGetValue(inum, out var hit))
        continue;
      if (hit.Inode[0x50] != ObjTypeRegfile)
        return false;                       // only flat regular files in scope
      var content = ReadFileData(image, hit.Inode);
      if (content == null)
        return false;
      files.Add(new FileEntry {
        Name = name, Inum = inum, Inode = hit.Inode, Content = content, OnDiskOff = hit.Off,
      });
    }
    return true;
  }

  private static byte[]? ReadFileData(byte[] image, byte[] inode) {
    var size = BinaryPrimitives.ReadInt64LittleEndian(inode.AsSpan(0x60, 8));
    if (size <= 0)
      return [];
    if ((inode[0x51] & OpflagDirectData) != 0) {
      var n = (int)Math.Min(size, InodeBytes - 0x200);
      return inode.AsSpan(0x200, n).ToArray();
    }
    var data = new byte[size];
    foreach (var (type, _, dataOff, raw) in EnumerateBlockset(image, inode, 0x200, SetCount * BlockrefBytes)) {
      if (type != BrefTypeData)
        continue;
      var bs = 1L << RadixOf(dataOff);
      if (!TryReadBlock(image, DecodeOffset(dataOff), (int)bs, out var block))
        return null;
      var logical = (long)BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(8, 8));
      if (logical >= size)
        continue;
      var copy = (int)Math.Min(bs, size - logical);
      block.AsSpan(0, copy).CopyTo(data.AsSpan((int)logical, copy));
    }
    return data;
  }

  // ── inode / dirent / blockref builders (mirror Hammer2Writer) ─────────────

  private static byte[] BuildRegFileInode(ulong inum, byte[] data, Func<byte[], int, long> allocate) {
    var inode = new byte[InodeBytes];
    var now = NowMicros();
    var name = "0x" + inum.ToString("x16");
    var nameBytes = Encoding.ASCII.GetBytes(name);

    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x00, 2), InodeVersionOne);
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x10, 8), now);
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x18, 8), now);
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x28, 8), now);
    inode[0x50] = ObjTypeRegfile;
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(0x54, 4), 0x1A4);
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x58, 8), inum);
    BinaryPrimitives.WriteInt64LittleEndian(inode.AsSpan(0x60, 8), data.LongLength);
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x68, 8), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x70, 8), (ulong)PfsRootInum);
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x78, 8), inum);
    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x80, 2), (ushort)nameBytes.Length);
    inode[0x83] = CompNone;
    inode[0x85] = CheckXxhash64;
    nameBytes.CopyTo(inode.AsSpan(0x100));

    if (data.Length <= EmbeddedDataMax) {
      inode[0x51] = OpflagDirectData;
      data.CopyTo(inode.AsSpan(0x200, data.Length));
    } else {
      var radix = DataRadix(data.Length);
      var block = new byte[1 << radix];
      data.CopyTo(block.AsSpan(0, data.Length));
      var blockOff = allocate(block, radix);
      WriteBlockref(inode.AsSpan(0x200, BlockrefBytes), BrefTypeData,
        CheckXxhash64, CompNone, flags: 0, key: 0, vradix: radix,
        dataOff: blockOff, check: XxCheck(block), mirrorTid: 0);
    }
    return inode;
  }

  private static (ulong Key, byte[] Bref) BuildDirentBref(string fileName, ulong inum, ulong mirrorTid) {
    var nameBytes = Encoding.ASCII.GetBytes(fileName);
    var key = Hammer2Crc.DirHash(nameBytes);
    var br = new byte[BlockrefBytes];
    br[0] = BrefTypeDirent;
    br[1] = (byte)(((CheckXxhash64 & 15) << 4) | (CompNone & 15));
    br[2] = 0xFF;
    BinaryPrimitives.WriteUInt64LittleEndian(br.AsSpan(8, 8), key);
    BinaryPrimitives.WriteUInt64LittleEndian(br.AsSpan(16, 8), mirrorTid);
    BinaryPrimitives.WriteUInt64LittleEndian(br.AsSpan(0x30, 8), inum);
    BinaryPrimitives.WriteUInt16LittleEndian(br.AsSpan(0x38, 2), (ushort)nameBytes.Length);
    br[0x3A] = ObjTypeRegfile;
    nameBytes.CopyTo(br.AsSpan(0x40, Math.Min(nameBytes.Length, 64)));
    return (key, br);
  }

  // Lay brefs inline (≤4) or into one indirect, mirroring the writer's split at
  // bit 63. Returns false when a single inline set / single indirect can't hold
  // them (nested indirects needed) so the caller rebuilds.
  private static bool TryLayoutBlockset(Span<byte> blockset,
      List<(ulong Key, byte[] Bref)> brefs, ulong mirrorTid, Func<byte[], int, long> allocate) {
    var sorted = brefs.OrderBy(b => b.Key).ToList();
    if (sorted.Count <= SetCount) {
      for (var i = 0; i < sorted.Count; ++i)
        sorted[i].Bref.CopyTo(blockset.Slice(i * BlockrefBytes, BlockrefBytes));
      return true;
    }

    // The writer splits the [0, 2^64) keyspace at bit 63 into a low half (inode
    // brefs, keys < 2^63) and a high half (dirent brefs, keys ≥ 2^63), each a
    // keybits=63 indirect. We reproduce that, refusing anything that would need a
    // deeper split (more than IndFanout entries on either side).
    var mid = 1UL << 63;
    var lower = sorted.Where(e => e.Key < mid).ToList();
    var upper = sorted.Where(e => e.Key >= mid).ToList();
    if (lower.Count > IndFanout || upper.Count > IndFanout)
      return false;

    var children = new List<byte[]>();
    if (lower.Count > 0)
      children.Add(BuildIndirect(lower, keyStart: 0, keyBits: 63, mirrorTid, allocate));
    if (upper.Count > 0)
      children.Add(BuildIndirect(upper, keyStart: mid, keyBits: 63, mirrorTid, allocate));
    if (children.Count > SetCount)
      return false;
    for (var i = 0; i < children.Count; ++i)
      children[i].CopyTo(blockset.Slice(i * BlockrefBytes, BlockrefBytes));
    return true;
  }

  private static byte[] BuildIndirect(List<(ulong Key, byte[] Bref)> entries,
      ulong keyStart, int keyBits, ulong mirrorTid, Func<byte[], int, long> allocate) {
    var block = new byte[1 << IndRadix];
    for (var i = 0; i < entries.Count; ++i)
      entries[i].Bref.CopyTo(block.AsSpan(i * BlockrefBytes, BlockrefBytes));
    var off = allocate(block, IndRadix);
    var bref = new byte[BlockrefBytes];
    WriteBlockref(bref, BrefTypeIndirect, CheckXxhash64, CompNone, flags: 0,
      key: keyStart, vradix: IndRadix, dataOff: off, check: XxCheck(block), mirrorTid);
    bref[3] = (byte)keyBits;
    BinaryPrimitives.WriteUInt16LittleEndian(bref.AsSpan(6, 2),
      (ushort)Math.Min(entries.Count, ushort.MaxValue));
    return bref;
  }

  private static void WriteBlockref(Span<byte> br, byte type, int checkAlgo, int compAlgo,
      byte flags, ulong key, int vradix, long dataOff, ReadOnlySpan<byte> check, ulong mirrorTid) {
    br.Clear();
    br[0] = type;
    br[1] = (byte)(((checkAlgo & 15) << 4) | (compAlgo & 15));
    br[2] = 0xFF;
    br[3] = 0;
    br[4] = (byte)vradix;
    br[5] = flags;
    BinaryPrimitives.WriteUInt16LittleEndian(br.Slice(6, 2), 0);
    BinaryPrimitives.WriteUInt64LittleEndian(br.Slice(8, 8), key);
    BinaryPrimitives.WriteUInt64LittleEndian(br.Slice(16, 8), mirrorTid);
    BinaryPrimitives.WriteUInt64LittleEndian(br.Slice(24, 8), mirrorTid);
    BinaryPrimitives.WriteInt64LittleEndian(br.Slice(32, 8), dataOff);
    BinaryPrimitives.WriteUInt64LittleEndian(br.Slice(40, 8), 0);
    check.CopyTo(br.Slice(64, check.Length));
  }

  // ── volume header CRC refresh ─────────────────────────────────────────────

  private static void PatchVolumeHeader(byte[] image, int vh, long srootOff, byte[] srootCheck, ulong mirrorTid) {
    // sroot_blockset[0] @0x200: keep CompAutozero (as newfs/writer), key 0, vradix 10.
    WriteBlockref(image.AsSpan(vh + 0x200, BlockrefBytes), BrefTypeInode,
      CheckXxhash64, CompAutozero, flags: 0, key: 0, vradix: RadixForInode,
      dataOff: EncodeDataOff(srootOff, RadixForInode), check: srootCheck, mirrorTid);

    // mirror_tid @0x78, freemap_tid @0x90.
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(vh + 0x78, 8), mirrorTid);

    // Recompute the three iCRCs exactly as the writer does.
    var span = image.AsSpan(vh, VolumeBytes);
    var sect1 = Hammer2Crc.Iscsi32(span.Slice(512, 512));
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x1E0 + 6 * 4, 4), sect1);
    var sect0 = Hammer2Crc.Iscsi32(span.Slice(0, 512 - 4));
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x1E0 + 7 * 4, 4), sect0);
    var vhc = Hammer2Crc.Iscsi32(span.Slice(0, VolumeBytes - 4));
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(VolumeBytes - 4, 4), vhc);
  }

  // ── blockset enumeration (inline + INDIRECT), read-side ───────────────────

  private static IEnumerable<(byte Type, int CompAlgo, long DataOff, byte[] Raw)>
      EnumerateBlockset(byte[] image, byte[] buffer, int offset, int length, int depth = 0) {
    if (depth > 16)
      yield break;
    var n = length / BlockrefBytes;
    for (var i = 0; i < n; ++i) {
      var pos = offset + i * BlockrefBytes;
      var type = buffer[pos];
      if (type == 0)
        continue;
      var compAlgo = buffer[pos + 1] & 15;
      var dataOff = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(pos + 32, 8));
      if (type != BrefTypeIndirect) {
        yield return (type, compAlgo, dataOff, buffer.AsSpan(pos, BlockrefBytes).ToArray());
        continue;
      }
      var bs = 1 << RadixOf(dataOff);
      if (!TryReadBlock(image, DecodeOffset(dataOff), bs, out var block))
        continue;
      foreach (var b in EnumerateBlockset(image, block, 0, block.Length, depth + 1))
        yield return b;
    }
  }

  private static (string Name, ulong Inum) ReadDirent(byte[] image, long dataOff, byte[] raw) {
    var inum = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0x30, 8));
    var namLen = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x38, 2));
    if (namLen == 0)
      return ("", 0);
    byte[] nameBytes;
    if (dataOff != 0 && namLen > 64) {
      var bs = Math.Max(1 << RadixOf(dataOff), (int)namLen);
      if (!TryReadBlock(image, DecodeOffset(dataOff), bs, out var block))
        return ("", 0);
      nameBytes = block.AsSpan(0, namLen).ToArray();
    } else {
      nameBytes = raw.AsSpan(0x40, Math.Min((int)namLen, 64)).ToArray();
    }
    return (Encoding.ASCII.GetString(nameBytes), inum);
  }

  // ── primitives ────────────────────────────────────────────────────────────

  private static int SelectVolumeHeader(byte[] image, out ulong mirrorTid) {
    var best = -1;
    ulong bestTid = 0;
    mirrorTid = 0;
    for (var slot = 0; slot < NumVolhdrs; ++slot) {
      var off = slot * VolumeBytes;
      if (off + VolumeBytes > image.LongLength)
        break;
      var magic = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(off, 8));
      if (magic != VolumeIdHbo && magic != VolumeIdAbo)
        continue;
      var tid = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(off + 0x78, 8));
      if (best < 0 || tid >= bestTid) {
        best = off;
        bestTid = tid;
      }
    }
    mirrorTid = bestTid;
    return best;
  }

  private static bool TryReadBlock(byte[] image, long off, int size, out byte[] block) {
    block = [];
    if (off < 0 || off + size > image.LongLength)
      return false;
    block = image.AsSpan((int)off, size).ToArray();
    return true;
  }

  private static string ReadInodeName(byte[] inode) {
    var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(0x80, 2));
    if (nameLen == 0 || 0x100 + nameLen > inode.Length)
      return "";
    return Encoding.ASCII.GetString(inode, 0x100, Math.Min((int)nameLen, InodeBytes - 0x100));
  }

  private static long DecodeOffset(long dataOff) => dataOff & ~0x3FL;
  private static int RadixOf(long dataOff) => (int)(dataOff & 0x3F);
  private static long EncodeDataOff(long off, int radix) => (off & ~0x3FL) | (long)(uint)(radix & 0x3F);

  private static int DataRadix(long bytes) {
    var radix = 6;
    while ((1L << radix) < bytes)
      ++radix;
    return radix;
  }

  private static byte[] XxCheck(byte[] data) {
    var h = Hammer2Crc.XxHash64(data, Hammer2Crc.Hammer2Seed);
    var b = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(b, h);
    return b;
  }

  private static long AlignUp(long v, long align) => (v + align - 1) & ~(align - 1);

  private static ulong NowMicros() =>
    (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L);
}
