#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Xfs;

/// <summary>
/// Writes a minimal XFS v5 filesystem image with short-form root directory and
/// extent-based file data. All multi-byte integer fields are big-endian per XFS spec,
/// except <c>sb_crc</c> and <c>di_crc</c> which are little-endian. v5 metadata blocks
/// carry CRC-32C checksums computed with the <c>crc</c> field zeroed during hashing.
/// Roundtrips through <see cref="XfsReader"/>.
/// </summary>
public sealed class XfsWriter {
  private const int BlockSize = 4096;
  private const int SectorSize = 512;
  private const int InodeSize = 256;     // v3 dinode minimum (also common default).
  private const int InodesPerBlock = BlockSize / InodeSize; // 16
  private const uint XfsMagic = 0x58465342; // "XFSB"
  private const ushort InodeMagic = 0x494E; // "IN"
  private const int InodeBlock = 4;      // block containing root dir + file inodes
  private const int DataStartBlock = 5;
  private const ulong RootIno = 64;      // inode 64 = block 4, offset 0
  private const int AgBlocks = 4096;     // AG size in blocks
  private const byte BlockLog = 12;      // log2(4096)
  private const byte SectorLog = 9;      // log2(512)
  private const byte InodeLog = 8;       // log2(256)
  private const byte InoPbLog = 4;       // log2(InodesPerBlock)
  private const byte AgBlkLog = 12;      // log2(AgBlocks)
  private const ushort XfsSbVersion5 = 5;

  // Offset of sb_crc (v5 superblock) — little-endian.
  private const int SbCrcOffset = 224;

  // Offset of di_crc inside a v3 dinode — little-endian.
  private const int DiCrcOffset = 100;

  private readonly List<(string name, byte[] data)> _files = [];
  private static readonly Guid VolumeUuid = new("7fb1c7a0-b71b-4f34-9d8a-5c7f6a2e11d3");

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (this._files.Count >= InodesPerBlock - 1)
      throw new InvalidOperationException($"XfsWriter supports at most {InodesPerBlock - 1} files.");
    var leaf = Path.GetFileName(name);
    if (leaf.Length > 250) leaf = leaf[..250];
    this._files.Add((leaf, data));
  }

  public void WriteTo(Stream output) {
    var fileDataBlocks = new int[this._files.Count];
    var fileBlockCounts = new int[this._files.Count];
    var nextBlock = DataStartBlock;
    for (var i = 0; i < this._files.Count; i++) {
      fileDataBlocks[i] = nextBlock;
      fileBlockCounts[i] = Math.Max(1, (this._files[i].data.Length + BlockSize - 1) / BlockSize);
      nextBlock += fileBlockCounts[i];
    }
    var totalBlocks = nextBlock;
    var image = new byte[totalBlocks * BlockSize];

    // ── v5 Superblock (block 0, big-endian fields; sb_crc is little-endian) ──
    WriteSuperblock(image, (ulong)totalBlocks);

    // ── Root directory inode (v3 dinode at block 4, offset 0) ──
    var rootOff = InodeBlock * BlockSize;
    WriteInodeCoreV3(image, rootOff, RootIno, mode: 0x41ED /* S_IFDIR | 0755 */, format: 1);
    // Short-form dir data at inode + 176
    var dirOff = rootOff + 176;
    image[dirOff] = (byte)this._files.Count; // count (4-byte inodes)
    image[dirOff + 1] = 0; // i8count = 0
    // parent inode (4 bytes BE) at dirOff+2 — root is its own parent
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(dirOff + 2), (uint)RootIno);
    var entryPos = dirOff + 6;
    for (var i = 0; i < this._files.Count; i++) {
      var childIno = (uint)(RootIno + 1 + (uint)i);
      var nameBytes = Encoding.UTF8.GetBytes(this._files[i].name);
      var nameLen = Math.Min(nameBytes.Length, 250);
      image[entryPos] = (byte)nameLen;
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(entryPos + 1), 0); // offset (unused for sf)
      nameBytes.AsSpan(0, nameLen).CopyTo(image.AsSpan(entryPos + 3));
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(entryPos + 3 + nameLen), childIno);
      entryPos += 3 + nameLen + 4;
    }
    var dirSize = entryPos - dirOff;
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(rootOff + 56), (ulong)dirSize); // di_size at offset 56

    // ── File inodes (inode 65, 66, ... at block 4, offset 256, 512, ...) ──
    for (var i = 0; i < this._files.Count; i++) {
      var ioff = InodeBlock * BlockSize + (1 + i) * InodeSize;
      WriteInodeCoreV3(image, ioff, RootIno + 1 + (ulong)i, mode: 0x81A4 /* S_IFREG | 0644 */, format: 2);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 56), (ulong)this._files[i].data.Length); // di_size
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(ioff + 76), 1); // di_nextents = 1

      // Encode one BMV extent at ioff+176 (16 bytes): logical=0, startBlock, blockCount
      var startBlock = (ulong)fileDataBlocks[i];
      var blockCount = (ulong)fileBlockCounts[i];
      var hi = (startBlock >> 43) & 0x1FF; // high 9 bits of start block
      var lo = (startBlock << 21) | (blockCount & 0x1FFFFF);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 176), hi);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 184), lo);

      // Write file data
      this._files[i].data.CopyTo(image, fileDataBlocks[i] * BlockSize);
    }

    // ── CRC backfill (must happen AFTER all other fields are final) ──
    // Superblock CRC is computed over the entire sector (512 bytes) with sb_crc zeroed.
    BackfillCrc(image.AsSpan(0, SectorSize), SbCrcOffset);
    // Each inode's di_crc is computed over the entire InodeSize (256 bytes) with di_crc zeroed.
    BackfillCrc(image.AsSpan(rootOff, InodeSize), DiCrcOffset);
    for (var i = 0; i < this._files.Count; i++) {
      var ioff = InodeBlock * BlockSize + (1 + i) * InodeSize;
      BackfillCrc(image.AsSpan(ioff, InodeSize), DiCrcOffset);
    }

    output.Write(image);
  }

  private static void WriteSuperblock(byte[] image, ulong totalBlocks) {
    var sb = image.AsSpan();

    // v4 core fields at canonical offsets.
    BinaryPrimitives.WriteUInt32BigEndian(sb[0..], XfsMagic);
    BinaryPrimitives.WriteUInt32BigEndian(sb[4..], BlockSize);
    BinaryPrimitives.WriteUInt64BigEndian(sb[8..], totalBlocks);        // sb_dblocks
    // sb_rblocks(16), sb_rextents(24) = 0

    // sb_uuid at offset 32 (16 bytes)
    VolumeUuid.ToByteArray().CopyTo(sb[32..]);

    BinaryPrimitives.WriteUInt64BigEndian(sb[48..], 0);                 // sb_logstart (external log = 0)
    BinaryPrimitives.WriteUInt64BigEndian(sb[56..], RootIno);           // sb_rootino
    BinaryPrimitives.WriteUInt64BigEndian(sb[64..], 0);                 // sb_rbmino (unused)
    BinaryPrimitives.WriteUInt64BigEndian(sb[72..], 0);                 // sb_rsumino (unused)
    BinaryPrimitives.WriteUInt32BigEndian(sb[80..], 0);                 // sb_rextsize
    BinaryPrimitives.WriteUInt32BigEndian(sb[84..], AgBlocks);          // sb_agblocks
    BinaryPrimitives.WriteUInt32BigEndian(sb[88..], 1);                 // sb_agcount
    BinaryPrimitives.WriteUInt32BigEndian(sb[92..], 0);                 // sb_rbmblocks
    BinaryPrimitives.WriteUInt32BigEndian(sb[96..], 0);                 // sb_logblocks
    BinaryPrimitives.WriteUInt16BigEndian(sb[100..], XfsSbVersion5);    // sb_versionnum = 5
    BinaryPrimitives.WriteUInt16BigEndian(sb[102..], SectorSize);       // sb_sectsize
    BinaryPrimitives.WriteUInt16BigEndian(sb[104..], InodeSize);        // sb_inodesize
    BinaryPrimitives.WriteUInt16BigEndian(sb[106..], InodesPerBlock);   // sb_inopblock
    // sb_fname[12] at offset 108 = zero

    sb[120] = BlockLog;   // sb_blocklog
    sb[121] = SectorLog;  // sb_sectlog
    sb[122] = InodeLog;   // sb_inodelog
    sb[123] = InoPbLog;   // sb_inopblog
    sb[124] = AgBlkLog;   // sb_agblklog
    sb[125] = 0;          // sb_rextslog
    sb[126] = 0;          // sb_inprogress
    sb[127] = 25;         // sb_imax_pct (default 25)

    BinaryPrimitives.WriteUInt64BigEndian(sb[128..], 0);                // sb_icount
    BinaryPrimitives.WriteUInt64BigEndian(sb[136..], 0);                // sb_ifree
    BinaryPrimitives.WriteUInt64BigEndian(sb[144..], 0);                // sb_fdblocks
    BinaryPrimitives.WriteUInt64BigEndian(sb[152..], 0);                // sb_frextents
    BinaryPrimitives.WriteUInt64BigEndian(sb[160..], 0);                // sb_uquotino
    BinaryPrimitives.WriteUInt64BigEndian(sb[168..], 0);                // sb_gquotino
    BinaryPrimitives.WriteUInt16BigEndian(sb[176..], 0);                // sb_qflags
    sb[178] = 0;                                                        // sb_flags
    sb[179] = 0;                                                        // sb_shared_vn
    BinaryPrimitives.WriteUInt32BigEndian(sb[180..], 0);                // sb_inoalignmt
    BinaryPrimitives.WriteUInt32BigEndian(sb[184..], 0);                // sb_unit
    BinaryPrimitives.WriteUInt32BigEndian(sb[188..], 0);                // sb_width
    sb[192] = 0;                                                        // sb_dirblklog
    sb[193] = 0;                                                        // sb_logsectlog
    BinaryPrimitives.WriteUInt16BigEndian(sb[194..], 0);                // sb_logsectsize
    BinaryPrimitives.WriteUInt32BigEndian(sb[196..], 0);                // sb_logsunit
    BinaryPrimitives.WriteUInt32BigEndian(sb[200..], 0);                // sb_features2
    BinaryPrimitives.WriteUInt32BigEndian(sb[204..], 0);                // sb_bad_features2

    // v5 additions start here.
    BinaryPrimitives.WriteUInt32BigEndian(sb[208..], 0);                // sb_features_compat
    BinaryPrimitives.WriteUInt32BigEndian(sb[212..], 0);                // sb_features_ro_compat
    BinaryPrimitives.WriteUInt32BigEndian(sb[216..], 0);                // sb_features_incompat
    BinaryPrimitives.WriteUInt32BigEndian(sb[220..], 0);                // sb_features_log_incompat
    // sb_crc at offset 224 — left zero for now; computed after the whole block is ready.
    BinaryPrimitives.WriteUInt32BigEndian(sb[228..], 0);                // sb_spino_align
    BinaryPrimitives.WriteUInt64BigEndian(sb[232..], 0);                // sb_pquotino
    BinaryPrimitives.WriteUInt64BigEndian(sb[240..], 1);                // sb_lsn (any non-zero seed)
    VolumeUuid.ToByteArray().CopyTo(sb[248..]);                         // sb_meta_uuid (same as sb_uuid when meta_uuid flag not set)
    BinaryPrimitives.WriteUInt64BigEndian(sb[264..], 0);                // sb_rrmapino (unused)
  }

  private static void WriteInodeCoreV3(byte[] image, int ioff, ulong inodeNumber, ushort mode, byte format) {
    var di = image.AsSpan(ioff);
    BinaryPrimitives.WriteUInt16BigEndian(di[0..], InodeMagic);         // di_magic
    BinaryPrimitives.WriteUInt16BigEndian(di[2..], mode);               // di_mode
    di[4] = 3;                                                          // di_version (v3 = CRC-enabled)
    di[5] = format;                                                     // di_format
    BinaryPrimitives.WriteUInt16BigEndian(di[6..], 0);                  // di_onlink
    BinaryPrimitives.WriteUInt32BigEndian(di[8..], 0);                  // di_uid
    BinaryPrimitives.WriteUInt32BigEndian(di[12..], 0);                 // di_gid
    BinaryPrimitives.WriteUInt32BigEndian(di[16..], 1);                 // di_nlink
    BinaryPrimitives.WriteUInt16BigEndian(di[20..], 0);                 // di_projid_lo
    BinaryPrimitives.WriteUInt16BigEndian(di[22..], 0);                 // di_projid_hi
    // di_pad[6] at 24..29 zero
    BinaryPrimitives.WriteUInt16BigEndian(di[30..], 0);                 // di_flushiter
    // timestamps at 32/40/48 left zero
    BinaryPrimitives.WriteUInt64BigEndian(di[56..], 0);                 // di_size (caller will overwrite)
    BinaryPrimitives.WriteUInt64BigEndian(di[64..], 0);                 // di_nblocks
    BinaryPrimitives.WriteUInt32BigEndian(di[72..], 0);                 // di_extsize
    BinaryPrimitives.WriteUInt32BigEndian(di[76..], 0);                 // di_nextents (caller may overwrite)
    BinaryPrimitives.WriteUInt16BigEndian(di[80..], 0);                 // di_anextents
    di[82] = 0;                                                         // di_forkoff
    di[83] = 0;                                                         // di_aformat
    BinaryPrimitives.WriteUInt32BigEndian(di[84..], 0);                 // di_dmevmask
    BinaryPrimitives.WriteUInt16BigEndian(di[88..], 0);                 // di_dmstate
    BinaryPrimitives.WriteUInt16BigEndian(di[90..], 0);                 // di_flags
    BinaryPrimitives.WriteUInt32BigEndian(di[92..], 0);                 // di_gen

    // v3 tail (offset 96..175).
    BinaryPrimitives.WriteUInt32BigEndian(di[96..], 0xFFFFFFFFu);       // di_next_unlinked = NULLAGINO
    // di_crc at offset 100 (little-endian) — left zero; backfilled later.
    BinaryPrimitives.WriteUInt64BigEndian(di[104..], 0);                // di_changecount
    BinaryPrimitives.WriteUInt64BigEndian(di[112..], 0);                // di_lsn
    BinaryPrimitives.WriteUInt64BigEndian(di[120..], 0);                // di_flags2
    BinaryPrimitives.WriteUInt32BigEndian(di[128..], 0);                // di_cowextsize
    // di_pad2[12] at 132..143 zero
    // di_crtime at 144..151 zero
    BinaryPrimitives.WriteUInt64BigEndian(di[152..], inodeNumber);      // di_ino
    VolumeUuid.ToByteArray().CopyTo(di[160..].Slice(0, 16));            // di_uuid
  }

  /// <summary>
  /// Backfills the CRC-32C of <paramref name="block"/> into the 4-byte field at
  /// <paramref name="crcFieldOffset"/>. The field is zeroed during hashing and written
  /// little-endian afterwards (matches on-disk XFS v5 layout for both <c>sb_crc</c> and <c>di_crc</c>).
  /// </summary>
  internal static void BackfillCrc(Span<byte> block, int crcFieldOffset) {
    // Zero the CRC field during hashing.
    block[crcFieldOffset] = 0;
    block[crcFieldOffset + 1] = 0;
    block[crcFieldOffset + 2] = 0;
    block[crcFieldOffset + 3] = 0;
    var crc = Crc32.Compute(block, Crc32.Castagnoli);
    BinaryPrimitives.WriteUInt32LittleEndian(block[crcFieldOffset..], crc);
  }
}
