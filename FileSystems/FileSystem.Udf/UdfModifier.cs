#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Udf;

/// <summary>
/// In-place UDF (ECMA-167) modifier — true random-access editing without
/// rebuilding the whole image. Targeted at images produced by <see cref="UdfWriter"/>:
/// 2 KiB sectors, partition starting at LBA 257, short allocation descriptors,
/// flat root directory, plain ECMA-167 (no Metadata partition, no VAT).
///
/// <para>What it touches per Add:
/// <list type="bullet">
///   <item>One sector for the new file's File Entry (allocated at the partition tail).</item>
///   <item>N sectors for the new file's data (also at the tail).</item>
///   <item>The root directory's FID extent — appended into trailing sector padding,
///         or extended via a second short_ad on the root File Entry.</item>
///   <item>The root File Entry sector — info length and L_AD updated, tag re-CRC'd.</item>
///   <item>The Partition Descriptor sector — partition length grown.</item>
/// </list></para>
///
/// <para>Remove uses the FID Characteristics "deleted" flag (bit 2 = 0x04) per
/// ECMA-167 §14.4.3, which is the canonical UDF tombstone. The dead FID's
/// identifier bytes are zeroed and its tag is re-CRC'd; the file's FE sector
/// and data extent are zero-wiped. The root extent is never compacted (preserving
/// existing pointers and offsets).</para>
///
/// <para>If the layout doesn't fit — e.g. the FID extent is at the very end of
/// the image so its growth area is occupied — the modifier falls back to
/// allocating fresh sectors at the partition tail and adding a second short_ad
/// to the root FE.</para>
/// </summary>
public static class UdfModifier {
  private const int SectorSize = 2048;
  private const int AvdpLba = 256;
  private const int FsdTagId = 256;
  private const int FeTagId = 261;
  private const int ExtendedFeTagId = 266;
  private const int FidTagId = 257;
  private const int PdTagId = 5;
  private const int LvdTagId = 6;

  // ── Public API ────────────────────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces) a file at the root of a UDF image. Allocates fresh
  /// FE + data sectors at the partition tail and appends a FID to the root
  /// directory's data extent. Replace semantics: any existing entry with the
  /// same name is removed first (deleted FID + zeroed data).
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!image.CanSeek || !image.CanRead || !image.CanWrite)
      throw new ArgumentException("Image stream must be readable, writable, and seekable.", nameof(image));

    // Replace semantics
    RemoveFile(image, name, wipeData: true);

    var ctx = ReadContext(image);

    // Compute new file's allocation: FE sector + data sectors, all at partition tail.
    var dataSectors = data.Length == 0 ? 1 : (data.Length + SectorSize - 1) / SectorSize;
    var feLbn = ctx.HighWaterLbn;
    var dataLbn = feLbn + 1;
    var newHighWater = dataLbn + dataSectors;

    // Build the new FID record.
    var fid = BuildFid(flags: 0x00, icbLbn: feLbn, name);

    // Write file data (zero-padded to sector boundary).
    var dataAbs = (ctx.PartitionStart + dataLbn) * (long)SectorSize;
    EnsureLength(image, dataAbs + dataSectors * (long)SectorSize);
    image.Position = dataAbs;
    image.Write(data);
    var dataPad = dataSectors * SectorSize - data.Length;
    if (dataPad > 0) image.Write(new byte[dataPad]);

    // Write file File Entry sector.
    var feSector = BuildFileFe(feLbn, data.Length, dataLbn);
    WriteSector(image, ctx.PartitionStart + feLbn, feSector);

    // Append the FID to the root directory's data extent.
    AppendFidToRoot(image, ctx, fid, ref newHighWater);

    // Grow the partition descriptor and image to cover the new allocations.
    UpdatePartitionLength(image, ctx, newHighWater);
  }

  /// <summary>
  /// Removes a named file from the root of a UDF image. The FID's deleted flag
  /// (bit 2 of Characteristics) is set, its identifier bytes are zeroed, and
  /// its tag is re-CRC'd. By default the file's FE and data extents are
  /// zero-wiped. Returns <c>true</c> when an entry was removed.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    if (!image.CanSeek || !image.CanRead || !image.CanWrite)
      throw new ArgumentException("Image stream must be readable, writable, and seekable.", nameof(image));

    Context ctx;
    try {
      ctx = ReadContext(image);
    } catch (InvalidDataException) {
      return false;
    }

    var fidExtent = ReadRootFidExtent(image, ctx);
    if (fidExtent == null) return false;

    var hit = LocateFid(fidExtent.Bytes, name);
    if (hit == null) return false;

    // Mark deleted + zero identifier bytes + recompute tag.
    var fidBuf = fidExtent.Bytes;
    fidBuf[hit.Offset + 18] |= 0x04;                    // characteristics: deleted
    var lIu = BinaryPrimitives.ReadUInt16LittleEndian(fidBuf.AsSpan(hit.Offset + 36));
    var idLen = fidBuf[hit.Offset + 19];
    if (idLen > 0) {
      var nameStart = hit.Offset + 38 + lIu;
      Array.Clear(fidBuf, nameStart, idLen);
    }
    FinalizeTag(fidBuf, hit.Offset, hit.Length - 16);

    // Persist the modified FID extent back to its source sectors.
    WriteFidExtent(image, ctx, fidExtent);

    // Wipe the file's FE sector + data extent.
    if (wipeData) {
      WipeFileEntry(image, ctx, hit.IcbLbn);
    }

    return true;
  }

  // ── Context parsing ───────────────────────────────────────────────────────

  private sealed record Context(
    int PartitionStart,
    int PartitionLengthSectors,
    int PdSectorLba,
    int RootFeLbn,
    int HighWaterLbn,
    long ImageSize);

  private static Context ReadContext(Stream image) {
    if (image.Length < 257L * SectorSize)
      throw new InvalidDataException("UDF: image too small.");

    // AVDP at LBA 256
    var avdp = ReadSector(image, AvdpLba);
    if (BinaryPrimitives.ReadUInt16LittleEndian(avdp) != 2)
      throw new InvalidDataException("UDF: invalid AVDP tag.");
    var mainVdsLoc = (int)BinaryPrimitives.ReadUInt32LittleEndian(avdp.AsSpan(20));
    var mainVdsLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(avdp.AsSpan(16));

    // Walk VDS for PD and LVD
    int partStart = 0, partLen = 0, pdSectorLba = 0;
    int fsdLbn = 0;
    var vdsSectors = mainVdsLen / SectorSize;
    for (var i = 0; i < vdsSectors && i < 64; i++) {
      var sectorLba = mainVdsLoc + i;
      if ((long)(sectorLba + 1) * SectorSize > image.Length) break;
      var sec = ReadSector(image, sectorLba);
      var tag = BinaryPrimitives.ReadUInt16LittleEndian(sec);
      if (tag == PdTagId) {
        partStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(sec.AsSpan(188));
        partLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(sec.AsSpan(192));
        pdSectorLba = sectorLba;
      } else if (tag == LvdTagId) {
        fsdLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(sec.AsSpan(252));
      } else if (tag == 8) {
        break;
      }
    }

    if (pdSectorLba == 0)
      throw new InvalidDataException("UDF: partition descriptor not found.");

    // FSD → root ICB LBN
    var fsd = ReadSector(image, partStart + fsdLbn);
    if (BinaryPrimitives.ReadUInt16LittleEndian(fsd) != FsdTagId)
      throw new InvalidDataException("UDF: invalid FSD tag.");
    var rootLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(fsd.AsSpan(404));

    // High-water mark = partStart + partLen (in absolute sectors), but expressed
    // as a partition LBN it's just `partLen`. We treat it as the next free LBN.
    return new Context(
      PartitionStart: partStart,
      PartitionLengthSectors: partLen,
      PdSectorLba: pdSectorLba,
      RootFeLbn: rootLbn,
      HighWaterLbn: partLen,
      ImageSize: image.Length);
  }

  // ── Root FID extent I/O ───────────────────────────────────────────────────

  /// <summary>
  /// Encapsulates the root directory's FID data: the contiguous bytes (logical
  /// length only — no padding), plus the list of (lbn, length) extents from
  /// the root FE's allocation descriptors. Long-extent representations are
  /// flattened to a single byte buffer for ease of mutation.
  /// </summary>
  private sealed class FidExtent {
    public required byte[] Bytes;
    public required List<(int Lbn, int Length)> Extents;
    public required int RootFeLbn;
    public required byte[] RootFeSector;
    public required int LEa;
    public required int FeAdStart;     // offset in RootFeSector where ADs begin
    public required int FeAdAreaSize;  // available bytes in FE sector for ADs
    public required int InfoLengthOffset;
    public required int LAdOffset;
    public required int FeBodyLength;  // for FinalizeTag
  }

  private static FidExtent? ReadRootFidExtent(Stream image, Context ctx) {
    var feSec = ReadSector(image, ctx.PartitionStart + ctx.RootFeLbn);
    var tag = BinaryPrimitives.ReadUInt16LittleEndian(feSec);
    if (tag != FeTagId && tag != ExtendedFeTagId) return null;
    var icbFlags = BinaryPrimitives.ReadUInt16LittleEndian(feSec.AsSpan(34));
    var adType = icbFlags & 0x07;
    if (adType != 0) return null; // we only handle short_ad here

    long infoLength;
    int lEa, lAd, adStart, infoLengthOffset, lAdOffset, feHeaderSize;
    if (tag == FeTagId) {
      infoLengthOffset = 56;
      infoLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(feSec.AsSpan(56));
      lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSec.AsSpan(168));
      lAdOffset = 172;
      lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSec.AsSpan(172));
      feHeaderSize = 176;
      adStart = feHeaderSize + lEa;
    } else {
      infoLengthOffset = 56;
      infoLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(feSec.AsSpan(56));
      lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSec.AsSpan(208));
      lAdOffset = 212;
      lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSec.AsSpan(212));
      feHeaderSize = 216;
      adStart = feHeaderSize + lEa;
    }

    // Walk allocation descriptors (short_ad = 8 bytes each).
    var extents = new List<(int Lbn, int Length)>();
    var totalRead = 0L;
    var pos = adStart;
    var end = adStart + lAd;
    while (pos + 8 <= end && totalRead < infoLength) {
      var extLenRaw = BinaryPrimitives.ReadUInt32LittleEndian(feSec.AsSpan(pos));
      var extLen = (int)(extLenRaw & 0x3FFFFFFFu);
      var extLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSec.AsSpan(pos + 4));
      if (extLen == 0) break;
      extents.Add((extLbn, extLen));
      totalRead += extLen;
      pos += 8;
    }

    // Read all extent bytes into one contiguous buffer (logical length).
    var bytes = new byte[(int)infoLength];
    var written = 0;
    foreach (var (lbn, len) in extents) {
      var copy = Math.Min(len, bytes.Length - written);
      if (copy <= 0) break;
      var abs = (long)(ctx.PartitionStart + lbn) * SectorSize;
      image.Position = abs;
      image.ReadExactly(bytes, written, copy);
      written += copy;
    }

    return new FidExtent {
      Bytes = bytes,
      Extents = extents,
      RootFeLbn = ctx.RootFeLbn,
      RootFeSector = feSec,
      LEa = lEa,
      FeAdStart = adStart,
      FeAdAreaSize = SectorSize - adStart,
      InfoLengthOffset = infoLengthOffset,
      LAdOffset = lAdOffset,
      FeBodyLength = (feHeaderSize - 16) + lEa + lAd, // updated on commit
    };
  }

  /// <summary>
  /// Writes the FID extent buffer back to the source extents (in order). Used
  /// after a Remove: the buffer's logical length is unchanged, only some FID
  /// records were marked deleted and re-CRC'd. Caller must have already
  /// patched the bytes in <see cref="FidExtent.Bytes"/>.
  /// </summary>
  private static void WriteFidExtent(Stream image, Context ctx, FidExtent ext) {
    var written = 0;
    foreach (var (lbn, len) in ext.Extents) {
      var copy = Math.Min(len, ext.Bytes.Length - written);
      if (copy <= 0) break;
      var abs = (long)(ctx.PartitionStart + lbn) * SectorSize;
      image.Position = abs;
      image.Write(ext.Bytes, written, copy);
      written += copy;
    }
  }

  // ── FID lookup ────────────────────────────────────────────────────────────

  private sealed record FidHit(int Offset, int Length, int IcbLbn);

  private static FidHit? LocateFid(byte[] fidBytes, string targetName) {
    var pos = 0;
    while (pos + 38 <= fidBytes.Length) {
      var tag = BinaryPrimitives.ReadUInt16LittleEndian(fidBytes.AsSpan(pos));
      if (tag != FidTagId) break;
      var lIu = BinaryPrimitives.ReadUInt16LittleEndian(fidBytes.AsSpan(pos + 36));
      var idLen = fidBytes[pos + 19];
      var fidLen = 38 + lIu + idLen;
      fidLen = (fidLen + 3) & ~3;
      if (pos + fidLen > fidBytes.Length) break;

      var flags = fidBytes[pos + 18];
      var isParent = (flags & 0x08) != 0;
      var isDeleted = (flags & 0x04) != 0;

      if (!isParent && !isDeleted && idLen > 0) {
        var nameStart = pos + 38 + lIu;
        var entryName = DecodeCs0(fidBytes, nameStart, idLen).TrimEnd('\0');
        if (string.Equals(entryName, targetName, StringComparison.Ordinal) ||
            string.Equals(entryName, targetName, StringComparison.OrdinalIgnoreCase)) {
          var icbLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(fidBytes.AsSpan(pos + 24));
          return new FidHit(pos, fidLen, icbLbn);
        }
      }
      pos += fidLen;
    }
    return null;
  }

  private static string DecodeCs0(byte[] buf, int offset, int len) {
    if (len <= 0) return "";
    var compressionId = buf[offset];
    if (compressionId == 8 && len > 1)
      return Encoding.UTF8.GetString(buf, offset + 1, len - 1);
    if (compressionId == 16 && len > 1)
      return Encoding.BigEndianUnicode.GetString(buf, offset + 1, len - 1);
    return Encoding.ASCII.GetString(buf, offset, len);
  }

  // ── Root FID growth (Add path) ────────────────────────────────────────────

  /// <summary>
  /// Appends a new FID record to the root directory's data extent. Tries to
  /// fit it in the trailing zero-pad of the last source sector first; falls
  /// back to allocating a fresh sector at the partition tail and adding a
  /// second short_ad to the root File Entry.
  /// </summary>
  private static void AppendFidToRoot(Stream image, Context ctx, byte[] fid, ref int highWaterLbn) {
    var ext = ReadRootFidExtent(image, ctx)
      ?? throw new InvalidDataException("UDF: root directory uses unsupported AD type or layout.");

    var oldLength = ext.Bytes.Length;
    var newLength = oldLength + fid.Length;

    // Compute capacity in the existing extents — total extent capacity
    // (sum of extent lengths rounded up to sector boundary).
    var capacity = 0L;
    foreach (var (_, len) in ext.Extents) {
      var sectors = (len + SectorSize - 1) / SectorSize;
      capacity += sectors * SectorSize;
    }

    if (newLength <= capacity) {
      // Fits inside the trailing pad of the last extent's last sector.
      WriteFidIntoExistingExtent(image, ctx, ext, fid);
      UpdateRootFeLength(image, ctx, ext, newLength, addExtent: null);
    } else {
      // Need to grow: allocate a fresh sector at partition tail, add a
      // second (or third…) short_ad to the root FE, and write the FID there.
      // First, write any spillover into the existing tail (if some bytes still fit).
      var tailRoom = (int)(capacity - oldLength);
      var spilled = Math.Min(tailRoom, fid.Length);
      if (spilled > 0) {
        // Fill the tail of the last extent with the first `spilled` bytes.
        WriteFidPartialToExistingTail(image, ctx, ext, fid, 0, spilled);
      }

      var remaining = fid.Length - spilled;
      var newSectors = (remaining + SectorSize - 1) / SectorSize;
      var newLbn = highWaterLbn;
      var newAbs = (long)(ctx.PartitionStart + newLbn) * SectorSize;
      EnsureLength(image, newAbs + (long)newSectors * SectorSize);
      image.Position = newAbs;
      var pad = new byte[newSectors * SectorSize];
      Buffer.BlockCopy(fid, spilled, pad, 0, remaining);
      image.Write(pad);
      highWaterLbn += newSectors;

      UpdateRootFeLength(image, ctx, ext, newLength,
        addExtent: (newLbn, remaining));
    }
  }

  private static void WriteFidIntoExistingExtent(Stream image, Context ctx, FidExtent ext, byte[] fid) {
    // Last extent absorbs the new FID — bytes past its logical length but
    // within its sector capacity.
    var (lbn, len) = ext.Extents[^1];
    var sectors = (len + SectorSize - 1) / SectorSize;
    var capInLast = sectors * SectorSize;
    var consumedInLast = ext.Bytes.Length;
    // Account for how many bytes go to extents BEFORE the last one
    var precedingTotal = 0;
    for (var i = 0; i < ext.Extents.Count - 1; i++) precedingTotal += ext.Extents[i].Length;
    var lastExtentUsed = consumedInLast - precedingTotal;
    if (lastExtentUsed < 0) lastExtentUsed = 0;
    var freeInLast = capInLast - lastExtentUsed;
    if (fid.Length > freeInLast)
      throw new InvalidOperationException("UDF: internal error — FID fit calculation mismatch.");

    var abs = (long)(ctx.PartitionStart + lbn) * SectorSize + lastExtentUsed;
    image.Position = abs;
    image.Write(fid);
  }

  private static void WriteFidPartialToExistingTail(Stream image, Context ctx, FidExtent ext,
                                                    byte[] fid, int srcOffset, int count) {
    var (lbn, len) = ext.Extents[^1];
    var sectors = (len + SectorSize - 1) / SectorSize;
    var capInLast = sectors * SectorSize;
    var precedingTotal = 0;
    for (var i = 0; i < ext.Extents.Count - 1; i++) precedingTotal += ext.Extents[i].Length;
    var lastExtentUsed = ext.Bytes.Length - precedingTotal;
    if (lastExtentUsed < 0) lastExtentUsed = 0;
    if (lastExtentUsed + count > capInLast)
      throw new InvalidOperationException("UDF: tail-fill calculation overflow.");
    var abs = (long)(ctx.PartitionStart + lbn) * SectorSize + lastExtentUsed;
    image.Position = abs;
    image.Write(fid, srcOffset, count);
  }

  /// <summary>
  /// Updates the root File Entry's info length, the last extent's length, and
  /// (if <paramref name="addExtent"/> is non-null) appends a new short_ad,
  /// growing L_AD by 8. Re-CRCs and re-checksums the FE tag and writes it back.
  /// </summary>
  private static void UpdateRootFeLength(Stream image, Context ctx, FidExtent ext,
                                         long newInfoLength, (int Lbn, int Length)? addExtent) {
    var fe = ext.RootFeSector;

    // info length
    BinaryPrimitives.WriteUInt64LittleEndian(fe.AsSpan(ext.InfoLengthOffset), (ulong)newInfoLength);

    // Recompute extent lengths: existing extents stay, except the last may
    // grow up to its full sector capacity (when we filled tail).
    var totalExtents = new List<(int Lbn, int Length)>(ext.Extents);
    if (totalExtents.Count > 0) {
      var (lbn, len) = totalExtents[^1];
      var sectors = (len + SectorSize - 1) / SectorSize;
      var cap = sectors * SectorSize;
      // New length of last extent = min(remaining info bytes that go to it, cap).
      var precedingTotal = 0;
      for (var i = 0; i < totalExtents.Count - 1; i++) precedingTotal += totalExtents[i].Length;
      var remaining = newInfoLength - precedingTotal;
      if (addExtent.HasValue) {
        // We're going to add a new extent: the last existing one is filled to cap.
        totalExtents[^1] = (lbn, cap);
      } else {
        var newLastLen = (int)Math.Min(remaining, cap);
        if (newLastLen < 0) newLastLen = 0;
        totalExtents[^1] = (lbn, newLastLen);
      }
    }

    if (addExtent.HasValue) totalExtents.Add(addExtent.Value);

    // Rewrite ADs in the FE sector.
    var newLAd = totalExtents.Count * 8;
    if (ext.FeAdStart + newLAd > SectorSize)
      throw new InvalidOperationException("UDF: too many root extents to fit in File Entry sector.");

    BinaryPrimitives.WriteUInt32LittleEndian(fe.AsSpan(ext.LAdOffset), (uint)newLAd);

    // Clear the old AD area (only what's needed).
    var oldArea = Math.Max(newLAd, ext.FeAdAreaSize);
    Array.Clear(fe, ext.FeAdStart, Math.Min(oldArea, SectorSize - ext.FeAdStart));

    var p = ext.FeAdStart;
    foreach (var (lbn, len) in totalExtents) {
      BinaryPrimitives.WriteUInt32LittleEndian(fe.AsSpan(p), (uint)len);
      BinaryPrimitives.WriteUInt32LittleEndian(fe.AsSpan(p + 4), (uint)lbn);
      p += 8;
    }

    // Re-CRC: body length = (feHeaderSize - 16) + L_EA + L_AD
    var feHeaderSize = ext.FeAdStart - ext.LEa;
    var bodyLength = (feHeaderSize - 16) + ext.LEa + newLAd;
    FinalizeTag(fe, 0, bodyLength);

    WriteSector(image, ctx.PartitionStart + ext.RootFeLbn, fe);
  }

  // ── File Entry construction (Add path) ────────────────────────────────────

  private static byte[] BuildFileFe(int lbn, int fileSize, int dataLbn) {
    var buf = new byte[SectorSize];
    WriteTag(buf, 0, FeTagId, (uint)lbn);
    buf[27] = 5; // file type = regular file
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34), 0); // adType=0 (short)
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(56), (ulong)fileSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(168), 0);  // L_EA
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(172), 8);  // L_AD = 8 (one short_ad)
    var allocLen = Math.Max(fileSize, SectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(176), (uint)allocLen);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(180), (uint)dataLbn);
    FinalizeTag(buf, 0, (176 - 16) + 0 + 8); // body covers FE header + L_EA + L_AD
    return buf;
  }

  // ── FID construction ──────────────────────────────────────────────────────

  private static byte[] BuildFid(byte flags, int icbLbn, string name) {
    var nameBytes = name.Length == 0 ? [] : EncodeCs0(name);
    var fidLen = 38 + nameBytes.Length;
    var padded = (fidLen + 3) & ~3;
    var buf = new byte[padded];
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), FidTagId);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 2); // descriptor version
    buf[18] = flags;
    buf[19] = (byte)nameBytes.Length;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), (uint)SectorSize); // ICB ext length
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), (uint)icbLbn);
    nameBytes.CopyTo(buf, 38);
    FinalizeTag(buf, 0, padded - 16);
    return buf;
  }

  private static byte[] EncodeCs0(string name) {
    var utf8 = Encoding.UTF8.GetBytes(name);
    var result = new byte[1 + utf8.Length];
    result[0] = 8; // CS0 = UTF-8
    utf8.CopyTo(result, 1);
    return result;
  }

  // ── Wipe (Remove path) ────────────────────────────────────────────────────

  private static void WipeFileEntry(Stream image, Context ctx, int feLbn) {
    var feAbs = (long)(ctx.PartitionStart + feLbn) * SectorSize;
    if (feAbs + SectorSize > image.Length) return;

    image.Position = feAbs;
    var fe = new byte[SectorSize];
    image.ReadExactly(fe);
    var tag = BinaryPrimitives.ReadUInt16LittleEndian(fe);
    if (tag != FeTagId && tag != ExtendedFeTagId) {
      // Not a recognizable FE — wipe its sector defensively and stop.
      WriteSector(image, ctx.PartitionStart + feLbn, new byte[SectorSize]);
      return;
    }

    var icbFlags = BinaryPrimitives.ReadUInt16LittleEndian(fe.AsSpan(34));
    var adType = icbFlags & 0x07;
    var infoLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(fe.AsSpan(56));

    int lEa, lAd, adStart;
    if (tag == FeTagId) {
      lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(fe.AsSpan(168));
      lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(fe.AsSpan(172));
      adStart = 176 + lEa;
    } else {
      lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(fe.AsSpan(208));
      lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(fe.AsSpan(212));
      adStart = 216 + lEa;
    }

    if (adType == 0 || adType == 1) {
      var stride = adType == 0 ? 8 : 16;
      var p = adStart;
      var end = adStart + lAd;
      var remaining = infoLength;
      while (p + stride <= end && remaining > 0) {
        var extLen = (int)(BinaryPrimitives.ReadUInt32LittleEndian(fe.AsSpan(p)) & 0x3FFFFFFFu);
        var extLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(fe.AsSpan(p + 4));
        if (extLen == 0) break;
        var sectors = (extLen + SectorSize - 1) / SectorSize;
        var startAbs = (long)(ctx.PartitionStart + extLbn) * SectorSize;
        WipeRange(image, startAbs, sectors * (long)SectorSize);
        remaining -= extLen;
        p += stride;
      }
    }

    // Wipe the FE sector itself last.
    WriteSector(image, ctx.PartitionStart + feLbn, new byte[SectorSize]);
  }

  private static void WipeRange(Stream image, long start, long count) {
    if (start >= image.Length) return;
    var capped = Math.Min(count, image.Length - start);
    image.Position = start;
    var zeros = new byte[Math.Min(SectorSize, capped)];
    var remaining = capped;
    while (remaining > 0) {
      var chunk = (int)Math.Min(zeros.Length, remaining);
      image.Write(zeros, 0, chunk);
      remaining -= chunk;
    }
  }

  // ── Partition descriptor update ───────────────────────────────────────────

  private static void UpdatePartitionLength(Stream image, Context ctx, int newPartLenSectors) {
    if (newPartLenSectors <= ctx.PartitionLengthSectors) return;

    var pd = ReadSector(image, ctx.PdSectorLba);
    BinaryPrimitives.WriteUInt32LittleEndian(pd.AsSpan(192), (uint)newPartLenSectors);
    FinalizeTag(pd, 0, 496); // PdBodySize
    WriteSector(image, ctx.PdSectorLba, pd);

    EnsureLength(image, (long)(ctx.PartitionStart + newPartLenSectors) * SectorSize);
  }

  // ── Tag helpers (mirror UdfWriter) ────────────────────────────────────────

  private static void WriteTag(byte[] buf, int off, ushort tagId, uint tagLocation) {
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off), tagId);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 2), 2); // descriptor version
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 12), tagLocation);
  }

  private static void FinalizeTag(byte[] buf, int tagOffset, int bodyLength) {
    var bodyStart = tagOffset + 16;
    if (bodyStart + bodyLength > buf.Length) bodyLength = buf.Length - bodyStart;
    if (bodyLength < 0) bodyLength = 0;
    var crc = Crc16Ccitt.Compute(buf.AsSpan(bodyStart, bodyLength));
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tagOffset + 8), crc);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tagOffset + 10), (ushort)bodyLength);
    buf[tagOffset + 4] = 0;
    byte sum = 0;
    for (var i = 0; i < 16; i++) {
      if (i == 4) continue;
      sum = (byte)(sum + buf[tagOffset + i]);
    }
    buf[tagOffset + 4] = sum;
  }

  // ── Stream helpers ────────────────────────────────────────────────────────

  private static byte[] ReadSector(Stream image, int lba) {
    var buf = new byte[SectorSize];
    image.Position = (long)lba * SectorSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteSector(Stream image, int lba, byte[] data) {
    if (data.Length != SectorSize)
      throw new ArgumentException("sector data must be 2048 bytes", nameof(data));
    image.Position = (long)lba * SectorSize;
    image.Write(data);
  }

  private static void EnsureLength(Stream image, long required) {
    if (image.Length < required) image.SetLength(required);
  }
}
