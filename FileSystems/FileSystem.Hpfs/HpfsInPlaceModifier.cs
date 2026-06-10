#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Hpfs;

/// <summary>
/// True in-place R/W modifier for OS/2 HPFS images. Unlike the rebuild-based
/// path (which rewrites the entire image on every Add/Remove), this mutator
/// flips bitmap bits at known offsets, writes fresh FNODE + data sectors into
/// the previously-free sector pool, and shifts dirent slots inside the root
/// DIRBLK in-place. Sectors not touched by the mutation keep their original
/// bytes byte-identical — verifiable with a sector-by-sector diff.
/// </summary>
/// <remarks>
/// <para><b>Scope (root-DIRBLK only).</b> The MVP implementation mutates the
/// root directory's dirent block only. Files are added at the root; nested
/// subdirectory mutation throws <see cref="NotSupportedException"/>. DIRBLK
/// B-tree split/merge across multiple dirent blocks is also deferred: if the
/// root DIRBLK runs out of dirent space the call throws
/// <see cref="InvalidOperationException"/> — callers can fall back to the
/// rebuild path in that case. This still covers the load-bearing
/// "add/remove a handful of files at the root" workflow without rewriting
/// 99% of the image.</para>
///
/// <para><b>Bitmap semantics.</b> The HPFS allocation bitmap at LBA 24 carries
/// one bit per sector: <c>1 = free</c>, <c>0 = used</c>. Allocation flips a
/// free bit to used; freeing flips a used bit back to free. The bitmap is
/// modified by direct byte-level masks so the rest of the bitmap sector is
/// byte-identical.</para>
///
/// <para><b>Dirent shift semantics.</b> A new dirent is inserted at the
/// correct sorted position by shifting later dirents (including the
/// end-of-block sentinel) forward by the new record length; a removed dirent
/// is excised by shifting later dirents back by the removed record length.
/// The DIRBLK header (first 0x14 bytes) is left untouched in both directions,
/// matching how OS/2 lays out dirent blocks.</para>
/// </remarks>
internal static class HpfsInPlaceModifier {

  private const int LbaSize = 512;
  private const int DirBlockLbas = 4; // 2048 bytes
  private const int DirBlockSize = LbaSize * DirBlockLbas;
  private const int DirentAreaOffset = 0x14;
  private const int DirentHeaderLen = 32;
  private const int FnodeAllocEntryOffset = 0xC4;

  private const ushort DirentFlagSpecial = 0x0001;
  private const ushort DirentFlagBtreeDown = 0x0004;
  private const ushort DirentFlagDirectory = 0x0008;

  private const uint SuperblockLba = 16;
  private const uint BitmapLba = 24;

  private static readonly byte[] FnodeMagic = [0xF7, 0xE4, 0x0A, 0xAE];
  private static readonly byte[] DirBlockMagic = [0x77, 0xE4, 0x0A, 0xAE];

  // ── Public API ──────────────────────────────────────────────────────────

  /// <summary>
  /// Adds the supplied files to the root directory of the HPFS image at
  /// <paramref name="image"/>. Subdirectory paths in
  /// <see cref="ArchiveInputInfo.ArchiveName"/> are not supported and throw
  /// <see cref="NotSupportedException"/>. An input whose name matches an
  /// existing root-level file replaces that file: when the new content fits
  /// in the file's current allocation the data sectors are rewritten in
  /// place; otherwise fresh sectors are allocated, the old ones are freed,
  /// and the FNODE allocation entry is updated.
  /// </summary>
  public static void Add(Stream image, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);

    var ctx = LoadContext(image);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = input.ArchiveName;
      if (name.Contains('/') || name.Contains('\\'))
        throw new NotSupportedException(
          "HpfsInPlaceModifier: subdirectory mutation is deferred — only root-level Add is supported. " +
          "Use the rebuild path for nested paths.");
      var content = input.ReadContent();
      AddOrReplace(ctx, name, content);
    }
    WriteBack(image, ctx);
  }

  /// <summary>
  /// Removes <paramref name="entryNames"/> from the root directory in place:
  /// shifts later dirents back over the removed slot, zeroes the freed FNODE
  /// + data sectors, and flips their bitmap bits to free. Names not found are
  /// silently skipped (matching the rebuild path's tolerance).
  /// </summary>
  public static void Remove(Stream image, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryNames);

    var ctx = LoadContext(image);
    foreach (var name in entryNames) {
      if (name.Contains('/') || name.Contains('\\'))
        throw new NotSupportedException(
          "HpfsInPlaceModifier: subdirectory mutation is deferred — only root-level Remove is supported.");
      RemoveOne(ctx, name);
    }
    WriteBack(image, ctx);
  }

  /// <summary>
  /// Convenience wrapper for the common "replace a single root-level file"
  /// flow. When the new content fits in the file's current allocation the
  /// data sectors are rewritten in place (no bitmap churn); otherwise the
  /// file is freed and re-allocated.
  /// </summary>
  public static void Replace(Stream image, string name, byte[] newData) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(newData);
    var ctx = LoadContext(image);
    AddOrReplace(ctx, name, newData);
    WriteBack(image, ctx);
  }

  // ── Context (working buffer over the whole image) ───────────────────────

  private sealed class Ctx {
    public required byte[] Buf;
    public uint RootFnodeLba;
    public uint RootDirBlockLba;
    public uint BitmapLbaCached;
    public int TotalLbas;
  }

  private static Ctx LoadContext(Stream image) {
    if (image.CanSeek) image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var buf = ms.ToArray();

    var sbOff = (int)SuperblockLba * LbaSize;
    if (buf.Length < sbOff + LbaSize)
      throw new InvalidDataException("HPFS: image too small for superblock.");

    var rootFnodeLba = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(sbOff + 12, 4));
    var bitmapLba = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(sbOff + 24, 4));
    if (bitmapLba == 0) bitmapLba = BitmapLba;

    var rootFnodeOff = (int)rootFnodeLba * LbaSize;
    if (rootFnodeOff + LbaSize > buf.Length)
      throw new InvalidDataException("HPFS: root fnode out of range.");
    var rootDirBlockLba = BinaryPrimitives.ReadUInt32LittleEndian(
      buf.AsSpan(rootFnodeOff + FnodeAllocEntryOffset + 8, 4));

    return new Ctx {
      Buf = buf,
      RootFnodeLba = rootFnodeLba,
      RootDirBlockLba = rootDirBlockLba,
      BitmapLbaCached = bitmapLba,
      TotalLbas = buf.Length / LbaSize,
    };
  }

  private static void WriteBack(Stream image, Ctx ctx) {
    image.Position = 0;
    image.Write(ctx.Buf, 0, ctx.Buf.Length);
    image.SetLength(ctx.Buf.Length);
  }

  // ── Add / Replace ───────────────────────────────────────────────────────

  private static void AddOrReplace(Ctx ctx, string name, byte[] content) {
    var existing = FindRootDirent(ctx, name);
    if (existing.Found) {
      ReplaceExisting(ctx, existing, content);
      return;
    }

    // Truly new entry — allocate fresh data + fnode sectors, then insert dirent.
    var dataLbas = (uint)((content.Length + LbaSize - 1) / LbaSize);
    if (dataLbas == 0) dataLbas = 0; // empty file: no data sectors

    var fnodeLba = AllocateFreeLba(ctx);
    uint dataLba = 0;
    if (dataLbas > 0)
      dataLba = AllocateContiguousFreeLbas(ctx, dataLbas);

    WriteFileFnode(ctx, fnodeLba, dataLba, dataLbas, ctx.RootFnodeLba);
    if (dataLbas > 0)
      Buffer.BlockCopy(content, 0, ctx.Buf, (int)(dataLba * LbaSize), content.Length);

    InsertRootDirent(ctx, name, fnodeLba, (uint)content.Length);
  }

  private readonly struct ExistingDirent {
    public bool Found { get; init; }
    public int DirentOffset { get; init; }
    public int RecLen { get; init; }
    public uint FnodeLba { get; init; }
    public bool IsDirectory { get; init; }
  }

  private static ExistingDirent FindRootDirent(Ctx ctx, string name) {
    var dirOff = (int)ctx.RootDirBlockLba * LbaSize;
    if (dirOff + DirBlockSize > ctx.Buf.Length) return default;
    if (!HasMagic(ctx.Buf, dirOff, DirBlockMagic)) return default;

    var cursor = dirOff + DirentAreaOffset;
    var blockEnd = dirOff + DirBlockSize;
    var safety = 0;
    while (cursor < blockEnd && safety++ < 4096) {
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buf.AsSpan(cursor, 2));
      if (recLen < DirentHeaderLen || cursor + recLen > blockEnd) break;
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buf.AsSpan(cursor + 2, 2));
      var isSpecial = (flags & DirentFlagSpecial) != 0;
      if (isSpecial && ctx.Buf[cursor + 30] == 0) break; // end sentinel

      if ((flags & DirentFlagBtreeDown) != 0)
        throw new InvalidOperationException(
          "HpfsInPlaceModifier: root DIRBLK is a B-tree (overflow leaves present). " +
          "Multi-block DIRBLK mutation is deferred — use the rebuild path.");

      var nameLen = ctx.Buf[cursor + 30];
      if (nameLen > 0 && cursor + 31 + nameLen <= blockEnd) {
        var entryName = Encoding.Latin1.GetString(ctx.Buf, cursor + 31, nameLen);
        if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase)) {
          var fnodeLba = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Buf.AsSpan(cursor + 4, 4));
          return new ExistingDirent {
            Found = true,
            DirentOffset = cursor,
            RecLen = recLen,
            FnodeLba = fnodeLba,
            IsDirectory = (flags & DirentFlagDirectory) != 0,
          };
        }
      }
      cursor += recLen;
    }
    return default;
  }

  private static void ReplaceExisting(Ctx ctx, ExistingDirent ex, byte[] newContent) {
    if (ex.IsDirectory)
      throw new InvalidOperationException(
        "HpfsInPlaceModifier: cannot replace a directory entry with file content.");

    var fnodeOff = (int)ex.FnodeLba * LbaSize;
    if (fnodeOff + LbaSize > ctx.Buf.Length)
      throw new InvalidDataException("HPFS: file fnode out of range.");

    var oldLengthLbas = BinaryPrimitives.ReadUInt32LittleEndian(
      ctx.Buf.AsSpan(fnodeOff + FnodeAllocEntryOffset + 4, 4));
    var oldDataLba = BinaryPrimitives.ReadUInt32LittleEndian(
      ctx.Buf.AsSpan(fnodeOff + FnodeAllocEntryOffset + 8, 4));

    var newLengthLbas = (uint)((newContent.Length + LbaSize - 1) / LbaSize);

    if (newLengthLbas <= oldLengthLbas) {
      // Fits in current allocation: rewrite data sectors in place, zero the tail,
      // update FNODE length-in-sectors hint + sizes, and patch the dirent's
      // logical size field.
      if (oldDataLba != 0 && oldLengthLbas > 0) {
        var dataOff = (int)oldDataLba * LbaSize;
        // Zero the whole allocated region first so old slack past newContent is wiped.
        ctx.Buf.AsSpan(dataOff, (int)(oldLengthLbas * LbaSize)).Clear();
        if (newContent.Length > 0)
          Buffer.BlockCopy(newContent, 0, ctx.Buf, dataOff, newContent.Length);
      } else if (newContent.Length > 0) {
        // Previously empty file gaining bytes — allocate fresh sectors.
        var freshLba = AllocateContiguousFreeLbas(ctx, newLengthLbas);
        Buffer.BlockCopy(newContent, 0, ctx.Buf, (int)(freshLba * LbaSize), newContent.Length);
        WriteFileFnode(ctx, ex.FnodeLba, freshLba, newLengthLbas, ctx.RootFnodeLba);
      }
      // FNODE length-in-sectors stays at the old (still-valid) value so we
      // don't lose the allocation footprint; only the dirent size changes.
      BinaryPrimitives.WriteUInt32LittleEndian(
        ctx.Buf.AsSpan(ex.DirentOffset + 12, 4), (uint)newContent.Length);
      return;
    }

    // Doesn't fit: free old data sectors + old FNODE, allocate fresh.
    if (oldDataLba != 0 && oldLengthLbas > 0) {
      FreeLbas(ctx, oldDataLba, oldLengthLbas);
      ctx.Buf.AsSpan((int)oldDataLba * LbaSize, (int)(oldLengthLbas * LbaSize)).Clear();
    }
    var newDataLba = AllocateContiguousFreeLbas(ctx, newLengthLbas);
    Buffer.BlockCopy(newContent, 0, ctx.Buf, (int)(newDataLba * LbaSize), newContent.Length);

    WriteFileFnode(ctx, ex.FnodeLba, newDataLba, newLengthLbas, ctx.RootFnodeLba);
    BinaryPrimitives.WriteUInt32LittleEndian(
      ctx.Buf.AsSpan(ex.DirentOffset + 12, 4), (uint)newContent.Length);
  }

  // ── Dirent insertion / removal ──────────────────────────────────────────

  private static void InsertRootDirent(Ctx ctx, string name, uint fnodeLba, uint fileSize) {
    var dirOff = (int)ctx.RootDirBlockLba * LbaSize;
    if (dirOff + DirBlockSize > ctx.Buf.Length)
      throw new InvalidDataException("HPFS: root DIRBLK out of range.");
    if (!HasMagic(ctx.Buf, dirOff, DirBlockMagic))
      throw new InvalidDataException("HPFS: root DIRBLK magic missing.");

    var nameBytes = Encoding.Latin1.GetBytes(name);
    if (nameBytes.Length > 254) nameBytes = nameBytes[..254];

    var newRecLen = AlignedRecordLen(nameBytes.Length, withDownPointer: false);

    // Walk dirents to find the insertion point (sorted, case-insensitive) and
    // the offset of the end sentinel. Everything from the insertion point up
    // to (and including) the end sentinel must shift right by newRecLen.
    var cursor = dirOff + DirentAreaOffset;
    var blockEnd = dirOff + DirBlockSize;
    var insertAt = -1;
    var sentinelOff = -1;
    var safety = 0;
    while (cursor < blockEnd && safety++ < 4096) {
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buf.AsSpan(cursor, 2));
      if (recLen < DirentHeaderLen || cursor + recLen > blockEnd)
        throw new InvalidDataException("HPFS: malformed dirent in root DIRBLK.");
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buf.AsSpan(cursor + 2, 2));
      var isSpecial = (flags & DirentFlagSpecial) != 0;
      if (isSpecial && ctx.Buf[cursor + 30] == 0) { sentinelOff = cursor; break; }

      if ((flags & DirentFlagBtreeDown) != 0)
        throw new InvalidOperationException(
          "HpfsInPlaceModifier: root DIRBLK is a B-tree — multi-block mutation deferred.");

      if (insertAt < 0) {
        var nameLen = ctx.Buf[cursor + 30];
        if (nameLen > 0 && cursor + 31 + nameLen <= blockEnd) {
          var existing = Encoding.Latin1.GetString(ctx.Buf, cursor + 31, nameLen);
          if (string.Compare(name, existing, StringComparison.OrdinalIgnoreCase) < 0)
            insertAt = cursor;
        }
      }
      cursor += recLen;
    }
    if (sentinelOff < 0)
      throw new InvalidDataException("HPFS: end-of-block sentinel not found in root DIRBLK.");
    if (insertAt < 0) insertAt = sentinelOff; // append before sentinel

    // Capacity check: the new record + the existing sentinel record must fit.
    var sentinelRecLen = BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buf.AsSpan(sentinelOff, 2));
    if (sentinelOff + sentinelRecLen + newRecLen > blockEnd)
      throw new InvalidOperationException(
        "HpfsInPlaceModifier: root DIRBLK has no space for another dirent. " +
        "Multi-block DIRBLK B-tree split is deferred — use the rebuild path.");

    // Shift the tail (insertAt..sentinelOff+sentinelRecLen) right by newRecLen.
    var tailLen = (sentinelOff + sentinelRecLen) - insertAt;
    if (tailLen > 0)
      Buffer.BlockCopy(ctx.Buf, insertAt, ctx.Buf, insertAt + newRecLen, tailLen);

    // Zero the freshly-vacated slot before writing into it.
    ctx.Buf.AsSpan(insertAt, newRecLen).Clear();
    WriteDirent(ctx.Buf, insertAt, nameBytes, fnodeLba, fileSize, isDirectory: false);
  }

  private static void RemoveOne(Ctx ctx, string name) {
    var ex = FindRootDirent(ctx, name);
    if (!ex.Found) return; // silent tolerance
    if (ex.IsDirectory)
      throw new InvalidOperationException(
        "HpfsInPlaceModifier: removing a directory is not supported in the MVP scope.");

    // Free data sectors + zero them.
    var fnodeOff = (int)ex.FnodeLba * LbaSize;
    if (fnodeOff + LbaSize <= ctx.Buf.Length) {
      var dataLengthLbas = BinaryPrimitives.ReadUInt32LittleEndian(
        ctx.Buf.AsSpan(fnodeOff + FnodeAllocEntryOffset + 4, 4));
      var dataLba = BinaryPrimitives.ReadUInt32LittleEndian(
        ctx.Buf.AsSpan(fnodeOff + FnodeAllocEntryOffset + 8, 4));
      if (dataLba != 0 && dataLengthLbas > 0) {
        FreeLbas(ctx, dataLba, dataLengthLbas);
        var dataOff = (int)dataLba * LbaSize;
        var dataLen = (int)(dataLengthLbas * LbaSize);
        if (dataOff + dataLen <= ctx.Buf.Length)
          ctx.Buf.AsSpan(dataOff, dataLen).Clear();
      }
    }
    // Free the FNODE sector + zero it.
    FreeLbas(ctx, ex.FnodeLba, 1);
    if (fnodeOff + LbaSize <= ctx.Buf.Length)
      ctx.Buf.AsSpan(fnodeOff, LbaSize).Clear();

    // Excise the dirent: shift everything from (dirent+recLen) up to and
    // including the end sentinel back by recLen.
    var dirOff = (int)ctx.RootDirBlockLba * LbaSize;
    var blockEnd = dirOff + DirBlockSize;

    // Find the end sentinel by walking dirents from the cursor right after
    // the removed one.
    var scan = ex.DirentOffset + ex.RecLen;
    var sentinelEnd = scan;
    var safety = 0;
    while (scan < blockEnd && safety++ < 4096) {
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buf.AsSpan(scan, 2));
      if (recLen < DirentHeaderLen || scan + recLen > blockEnd) break;
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buf.AsSpan(scan + 2, 2));
      var isSpecial = (flags & DirentFlagSpecial) != 0;
      sentinelEnd = scan + recLen;
      scan += recLen;
      if (isSpecial && ctx.Buf[scan - recLen + 30] == 0) break;
    }

    var shiftSrc = ex.DirentOffset + ex.RecLen;
    var shiftLen = sentinelEnd - shiftSrc;
    if (shiftLen > 0)
      Buffer.BlockCopy(ctx.Buf, shiftSrc, ctx.Buf, ex.DirentOffset, shiftLen);
    // Zero the freshly-vacated tail so the removed dirent's bytes leave no trace.
    var vacated = ex.DirentOffset + shiftLen;
    var vacatedLen = (sentinelEnd - vacated);
    if (vacatedLen > 0)
      ctx.Buf.AsSpan(vacated, vacatedLen).Clear();
  }

  // ── Dirent encoding helpers ─────────────────────────────────────────────

  private static int AlignedRecordLen(int nameLen, bool withDownPointer) {
    var len = DirentHeaderLen + nameLen + (withDownPointer ? 4 : 0);
    if ((len & 3) != 0) len = (len + 3) & ~3;
    return len;
  }

  private static void WriteDirent(byte[] buf, int cursor, byte[] nameBytes, uint fnodeLba, uint fileSize, bool isDirectory) {
    var recLen = AlignedRecordLen(nameBytes.Length, withDownPointer: false);
    var flags = (ushort)(isDirectory ? DirentFlagDirectory : 0);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), (ushort)recLen);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor + 2, 2), flags);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor + 4, 4), fnodeLba);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor + 12, 4), isDirectory ? 0u : fileSize);
    buf[cursor + 30] = (byte)nameBytes.Length;
    nameBytes.CopyTo(buf.AsSpan(cursor + 31, nameBytes.Length));
  }

  // ── FNODE writer (file) ─────────────────────────────────────────────────

  private static void WriteFileFnode(Ctx ctx, uint fnodeLba, uint dataLba, uint dataLenLbas, uint parentFnodeLba) {
    var off = (int)fnodeLba * LbaSize;
    if (off + LbaSize > ctx.Buf.Length)
      throw new InvalidDataException("HPFS: fnode LBA out of range.");
    // Zero the whole FNODE sector first so leftover bytes don't leak.
    ctx.Buf.AsSpan(off, LbaSize).Clear();
    FnodeMagic.CopyTo(ctx.Buf.AsSpan(off, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(ctx.Buf.AsSpan(off + 0x0C, 4), parentFnodeLba);
    // AllocSec header at 0xC0 (height = 0 → direct list); already zeroed.
    BinaryPrimitives.WriteUInt32LittleEndian(ctx.Buf.AsSpan(off + FnodeAllocEntryOffset + 0, 4), 0u);
    BinaryPrimitives.WriteUInt32LittleEndian(ctx.Buf.AsSpan(off + FnodeAllocEntryOffset + 4, 4), dataLenLbas);
    BinaryPrimitives.WriteUInt32LittleEndian(ctx.Buf.AsSpan(off + FnodeAllocEntryOffset + 8, 4), dataLba);
  }

  // ── Bitmap-driven allocation ────────────────────────────────────────────

  /// <summary>
  /// Allocates a single free sector by scanning the bitmap for a 1 bit,
  /// flipping it to 0 (used). The bitmap covers 4096 bits per LBA, which is
  /// enough for test-sized images; for production-sized images the bitmap
  /// would span multiple bands but the writer's current geometry stops at
  /// band 0, matching the reader's assumption.
  /// </summary>
  private static uint AllocateFreeLba(Ctx ctx) {
    var allocated = AllocateContiguousFreeLbas(ctx, 1);
    return allocated;
  }

  /// <summary>
  /// Allocates a contiguous run of <paramref name="count"/> free sectors by
  /// scanning the bitmap for the first hole large enough. Returns the LBA of
  /// the first sector and flips all bits in the run to used. Throws when no
  /// hole of the requested size exists in band 0 — the caller can fall back
  /// to the rebuild path in that case.
  /// </summary>
  private static uint AllocateContiguousFreeLbas(Ctx ctx, uint count) {
    if (count == 0) return 0;
    var bitmapOff = (int)ctx.BitmapLbaCached * LbaSize;
    if (bitmapOff + LbaSize > ctx.Buf.Length)
      throw new InvalidDataException("HPFS: bitmap out of range.");

    // Search the bitmap for `count` consecutive free bits, but never return an
    // LBA past the end of the image (the bitmap is set up by the writer for
    // the whole image, so an LBA reported free in the bitmap is also backed by
    // image bytes).
    var totalLbas = ctx.TotalLbas;
    var run = 0;
    var runStart = -1;
    for (var lba = 0; lba < totalLbas; lba++) {
      if (IsBitmapBitFree(ctx, lba)) {
        if (run == 0) runStart = lba;
        run++;
        if (run == (int)count) {
          for (var i = 0; i < (int)count; i++)
            MarkBitmapUsed(ctx, runStart + i);
          return (uint)runStart;
        }
      } else {
        run = 0;
        runStart = -1;
      }
    }

    throw new InvalidOperationException(
      $"HpfsInPlaceModifier: no contiguous run of {count} free sectors in the bitmap. " +
      "Free more space (Remove first) or use the rebuild path.");
  }

  private static void FreeLbas(Ctx ctx, uint startLba, uint count) {
    for (var i = 0u; i < count; i++)
      MarkBitmapFree(ctx, (int)(startLba + i));
  }

  private static bool IsBitmapBitFree(Ctx ctx, int lba) {
    // Each bitmap LBA covers LbaSize * 8 = 4096 sector bits; band 0 only.
    if (lba < 0 || lba >= LbaSize * 8) return false;
    var bitmapOff = (int)ctx.BitmapLbaCached * LbaSize;
    var byteIdx = lba / 8;
    var bitIdx = lba % 8;
    return (ctx.Buf[bitmapOff + byteIdx] & (1 << bitIdx)) != 0;
  }

  private static void MarkBitmapUsed(Ctx ctx, int lba) {
    if (lba < 0 || lba >= LbaSize * 8) return;
    var bitmapOff = (int)ctx.BitmapLbaCached * LbaSize;
    var byteIdx = lba / 8;
    var bitIdx = lba % 8;
    ctx.Buf[bitmapOff + byteIdx] &= (byte)~(1 << bitIdx);
  }

  private static void MarkBitmapFree(Ctx ctx, int lba) {
    if (lba < 0 || lba >= LbaSize * 8) return;
    var bitmapOff = (int)ctx.BitmapLbaCached * LbaSize;
    var byteIdx = lba / 8;
    var bitIdx = lba % 8;
    ctx.Buf[bitmapOff + byteIdx] |= (byte)(1 << bitIdx);
  }

  private static bool HasMagic(byte[] buf, int off, byte[] magic) {
    if (off < 0 || off + magic.Length > buf.Length) return false;
    for (var i = 0; i < magic.Length; i++)
      if (buf[off + i] != magic[i]) return false;
    return true;
  }
}
