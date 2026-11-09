#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Ufs;

/// <summary>
/// Formats an empty UFS2 volume the way FreeBSD's <c>newfs -O2</c> lays one out with its
/// default 16 KiB blocks and 2 KiB fragments: superblock at <c>SBLOCK_UFS2</c> (65536),
/// a backup at <c>cgbase + fs_sblkno</c> in every cylinder group, cylinder-group headers
/// with the UFS2 field set (<c>cg_niblk</c>, <c>cg_initediblk</c>, 64-bit <c>cg_time</c>),
/// the <c>fs_cs</c> summary in the first data fragment of group 0, the root directory
/// and the <c>.snap</c> directory newfs creates. Files are then added by
/// <see cref="UfsModifier"/>, so a created volume and an edited one follow the same rules.
/// <para>
/// Geometry follows newfs: four groups for a small volume (more once a group's bitmaps
/// would outgrow its header block), <c>fpg = roundup(size / ncg, frag) + frag</c>, one
/// inode per 8 KiB of data rounded to whole inode blocks, and the metadata fragments
/// <c>sblkno = 40</c>, <c>cblkno = 48</c>, <c>iblkno = 56</c>.
/// </para>
/// <para>References: FreeBSD <c>sys/ufs/ffs/fs.h</c> (<c>struct fs</c>, <c>struct cg</c>),
/// <c>sys/ufs/ufs/dinode.h</c>; field values compared against volumes FreeBSD 8.2's
/// <c>newfs -O2</c> wrote.</para>
/// </summary>
public static class Ufs2Formatter {
  public const int SuperblockOffset = 65536;
  public const int BlockSize = 16384;
  public const int FragSize = 2048;
  private const int Frag = BlockSize / FragSize;      // 8
  private const int InodeSize = 256;
  private const int InodesPerBlock = BlockSize / InodeSize; // 64
  private const int SblkNo = 40, CblkNo = 48, IblkNo = 56;
  private const int ContigSumSize = 8;
  private const int CgHeaderSize = 168;              // sizeof(struct cg) up to cg_space
  private const int MaxFragsPerGroup = 8 * 8192;     // keeps the free-fragment map well inside one block
  private const int Density = 4 * FragSize;          // bytes of data per inode
  public const long MinimumBytes = 8L * 1024 * 1024;

  private static long RoundUp(long a, long b) => (a + b - 1) / b * b;
  private static long HowMany(long a, long b) => (a + b - 1) / b;

  /// <summary>
  /// Formats <paramref name="output"/> as an empty UFS2 volume of <paramref name="sizeBytes"/> bytes,
  /// every multi-byte field little-endian or, with <paramref name="bigEndian"/>, big-endian
  /// (as NetBSD's <c>newfs -B be</c> writes it).
  /// </summary>
  public static void Format(Stream output, long sizeBytes, string volumeLabel = "", DateTimeOffset? now = null, bool bigEndian = false) {
    var e = new UfsByteOrder(bigEndian);
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite) throw new ArgumentException("UFS2: formatting needs a seekable, writable stream.", nameof(output));
    if (sizeBytes < MinimumBytes) throw new ArgumentOutOfRangeException(nameof(sizeBytes), $"UFS2: volumes start at {MinimumBytes} bytes.");
    var label = volumeLabel ?? "";
    if (Encoding.ASCII.GetByteCount(label) > 31) throw new ArgumentException("UFS2: the volume label holds at most 31 bytes.", nameof(volumeLabel));
    var time = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();

    var size = sizeBytes / FragSize;                                      // fs_size, in fragments
    var ncg = Math.Max(4, (int)HowMany(size, MaxFragsPerGroup));
    var fpg = RoundUp(HowMany(size, ncg), Frag) + Frag;
    while (ncg > 1 && (ncg - 1) * fpg >= size) --ncg;
    var ipg = (int)RoundUp(HowMany(fpg * FragSize, Density), InodesPerBlock);
    var dblkno = IblkNo + ipg * InodeSize / FragSize;
    var lastNdblk = size - (long)(ncg - 1) * fpg;
    if (lastNdblk < dblkno + Frag) throw new ArgumentOutOfRangeException(nameof(sizeBytes), "UFS2: the last cylinder group would hold no data.");

    var iusedoff = CgHeaderSize;
    var freeoff = iusedoff + (int)HowMany(ipg, 8);
    var clustersumoff = (int)RoundUp(freeoff + HowMany(fpg, 8) - 4, 4);
    var clusteroff = clustersumoff + (ContigSumSize + 1) * 4;
    var nextfreeoff = clusteroff + (int)HowMany(fpg / Frag, 8);
    var cgsize = (int)RoundUp(nextfreeoff, FragSize);
    if (cgsize > BlockSize) throw new InvalidOperationException("UFS2: cylinder-group header outgrew its block.");
    var cssize = (int)RoundUp(ncg * 16, FragSize);
    var csFrags = cssize / FragSize;
    var csaddr = (long)dblkno;                                            // first data fragment of group 0
    var rootFrag = RoundUp(csaddr + csFrags, Frag);                       // next block: root directory
    var snapFrag = rootFrag + Frag;                                       // then .snap
    var dsize = size - SblkNo - (long)ncg * (dblkno - SblkNo) - csFrags;

    output.SetLength(0);
    output.SetLength(size * FragSize);

    // ── cylinder groups ───────────────────────────────────────────────────
    var cs = new (int Ndir, int Nbfree, int Nifree, int Nffree)[ncg];
    var initedib = Math.Min(ipg, 2 * InodesPerBlock);
    for (var cg = 0; cg < ncg; ++cg) {
      var cgbase = (long)cg * fpg;
      var ndblk = (int)(cg == ncg - 1 ? lastNdblk : fpg);
      var used = new bool[ndblk];
      for (var f = cg == 0 ? 0 : SblkNo; f < dblkno; ++f) used[f] = true;
      if (cg == 0) {
        for (var f = 0; f < csFrags; ++f) used[dblkno + f] = true;
        used[rootFrag] = true;
        used[snapFrag] = true;
      }
      var hdr = new byte[cgsize];
      var h = hdr.AsSpan();
      e.I32(h[4..], 0x090255);         // cg_magic
      e.I32(h[12..], cg);              // cg_cgx
      e.I32(h[20..], ndblk);           // cg_ndblk
      e.I32(h[92..], iusedoff);
      e.I32(h[96..], freeoff);
      e.I32(h[100..], nextfreeoff);
      e.I32(h[104..], clustersumoff);
      e.I32(h[108..], clusteroff);
      e.I32(h[112..], ndblk / Frag);   // cg_nclusterblks
      e.I32(h[116..], ipg);            // cg_niblk
      e.I32(h[120..], initedib);       // cg_initediblk
      e.I64(h[136..], time);           // cg_time

      var usedInodes = cg == 0 ? 4 : 0;                                  // 0, 1 reserved; 2 root; 3 .snap
      for (var i = 0; i < usedInodes; ++i) hdr[iusedoff + i / 8] |= (byte)(1 << (i % 8));
      for (var f = 0; f < ndblk; ++f) if (!used[f]) hdr[freeoff + f / 8] |= (byte)(1 << (f % 8));

      int nbfree = 0, nffree = 0, run = 0;
      var frsum = new int[Frag];
      var sums = new int[ContigSumSize + 1];
      for (var b = 0; b + Frag <= ndblk; b += Frag) {
        var free = 0;
        for (var f = 0; f < Frag; ++f) if (!used[b + f]) ++free;
        if (free == Frag) {
          ++nbfree; ++run;
          hdr[clusteroff + b / Frag / 8] |= (byte)(1 << (b / Frag % 8));
          continue;
        }
        if (run > 0) { sums[Math.Min(run, ContigSumSize)]++; run = 0; }
        nffree += free;
        var r = 0;
        for (var f = 0; f <= Frag; ++f) {
          if (f < Frag && !used[b + f]) { ++r; continue; }
          if (r > 0) frsum[r]++;
          r = 0;
        }
      }
      if (run > 0) sums[Math.Min(run, ContigSumSize)]++;
      for (var f = ndblk - ndblk % Frag; f < ndblk; ++f) if (!used[f]) ++nffree;
      for (var i = 1; i < Frag; ++i) e.I32(h[(52 + i * 4)..], frsum[i]);
      for (var i = 1; i <= ContigSumSize; ++i) e.I32(h[(clustersumoff + i * 4)..], sums[i]);
      cs[cg] = (cg == 0 ? 2 : 0, nbfree, ipg - usedInodes, nffree);
      e.I32(h[24..], cs[cg].Ndir);
      e.I32(h[28..], cs[cg].Nbfree);
      e.I32(h[32..], cs[cg].Nifree);
      e.I32(h[36..], cs[cg].Nffree);
      Write(output, (cgbase + CblkNo) * FragSize, hdr);
    }

    // ── fs_cs summary ─────────────────────────────────────────────────────
    var csBytes = new byte[cssize];
    for (var cg = 0; cg < ncg; ++cg) {
      e.I32(csBytes.AsSpan(cg * 16), cs[cg].Ndir);
      e.I32(csBytes.AsSpan(cg * 16 + 4), cs[cg].Nbfree);
      e.I32(csBytes.AsSpan(cg * 16 + 8), cs[cg].Nifree);
      e.I32(csBytes.AsSpan(cg * 16 + 12), cs[cg].Nffree);
    }
    Write(output, csaddr * FragSize, csBytes);

    // ── root and .snap ────────────────────────────────────────────────────
    var inodeTable = (long)IblkNo * FragSize;
    Write(output, inodeTable + 2 * InodeSize, DirectoryInode(e, 0x41ED, 3, 0, rootFrag, time));   // 040755
    Write(output, inodeTable + 3 * InodeSize, DirectoryInode(e, 0x41FD, 2, 5, snapFrag, time));   // 040775, gid operator
    Write(output, rootFrag * FragSize, DirectoryChunk(e, (2, "."), (2, ".."), (3, ".snap")));
    Write(output, snapFrag * FragSize, DirectoryChunk(e, (3, "."), (2, "..")));

    // ── superblock and its backups ────────────────────────────────────────
    var sb = new byte[1376];
    var s = sb.AsSpan();
    void I32(int o, int v) => e.I32(sb.AsSpan(o), v);
    void I64(int o, long v) => e.I64(sb.AsSpan(o), v);
    I32(8, SblkNo); I32(12, CblkNo); I32(16, IblkNo); I32(20, dblkno);
    I32(44, ncg); I32(48, BlockSize); I32(52, FragSize); I32(56, Frag);
    I32(60, 8);                                    // fs_minfree
    I32(72, ~(BlockSize - 1)); I32(76, ~(FragSize - 1));
    I32(80, 14); I32(84, 11);                      // bshift, fshift
    I32(88, 8);                                    // fs_maxcontig
    I32(92, 2048);                                 // fs_maxbpg
    I32(96, 3); I32(100, 2);                       // fragshift, fsbtodb
    I32(104, 2048);                                // fs_sbsize
    I32(116, BlockSize / 8);                       // fs_nindir
    I32(120, InodesPerBlock);
    I32(144, (int)time); I32(148, Random.Shared.Next());   // fs_id
    I32(156, cssize); I32(160, cgsize);
    I32(184, ipg); I32(188, (int)fpg);
    sb[209] = 1;                                   // fs_clean
    sb[211] = 0x80;                                // fs_old_flags: FS_FLAGS_UPDATED
    Encoding.ASCII.GetBytes(label, s.Slice(680, 32));
    I32(860, BlockSize);                           // fs_maxbsize
    I64(1000, SuperblockOffset);                   // fs_sblockloc
    I64(1008, cs.Sum(c => (long)c.Ndir));
    I64(1016, cs.Sum(c => (long)c.Nbfree));
    I64(1024, cs.Sum(c => (long)c.Nifree));
    I64(1032, cs.Sum(c => (long)c.Nffree));
    I64(1072, time);                               // fs_time
    I64(1080, size); I64(1088, dsize); I64(1096, csaddr);
    I32(1196, 16384); I32(1200, 64);               // fs_avgfilesize, fs_avgfpdir
    I32(1316, ContigSumSize);
    I32(1320, 120);                                // fs_maxsymlinklen
    long maxFile = (long)BlockSize * 12 - 1, span = BlockSize;
    for (var i = 0; i < 3; ++i) { span *= BlockSize / 8; maxFile += span; }
    I64(1328, maxFile);
    I64(1336, BlockSize - 1); I64(1344, FragSize - 1); // fs_qbmask, fs_qfmask
    I32(1372, unchecked((int)0x19540119));
    Write(output, SuperblockOffset, sb);
    for (var cg = 0; cg < ncg; ++cg) Write(output, ((long)cg * fpg + SblkNo) * FragSize, sb);
    output.Flush();
  }

  private static void Write(Stream output, long offset, ReadOnlySpan<byte> data) {
    output.Position = offset;
    output.Write(data);
  }

  private static byte[] DirectoryInode(UfsByteOrder e, ushort mode, short links, uint gid, long frag, long time) {
    var inode = new byte[InodeSize];
    e.U16(inode, mode);
    e.I16(inode.AsSpan(2), links);
    e.U32(inode.AsSpan(8), gid);
    e.I64(inode.AsSpan(16), 512);              // di_size: one DIRBLKSIZ chunk
    e.I64(inode.AsSpan(24), FragSize / 512);   // di_blocks
    for (var o = 32; o <= 56; o += 8) e.I64(inode.AsSpan(o), time); // a/m/c/birth time
    e.I64(inode.AsSpan(112), frag);            // di_db[0]
    return inode;
  }

  private static byte[] DirectoryChunk(UfsByteOrder e, params (int Ino, string Name)[] entries) {
    var chunk = new byte[512];
    var pos = 0;
    for (var i = 0; i < entries.Length; ++i) {
      var (ino, name) = entries[i];
      var reclen = i == entries.Length - 1 ? 512 - pos : (8 + name.Length + 1 + 3) & ~3;
      e.U32(chunk.AsSpan(pos), (uint)ino);
      e.U16(chunk.AsSpan(pos + 4), (ushort)reclen);
      chunk[pos + 6] = 4;                                                         // DT_DIR
      chunk[pos + 7] = (byte)name.Length;
      Encoding.ASCII.GetBytes(name, chunk.AsSpan(pos + 8));
      pos += reclen;
    }
    return chunk;
  }
}
