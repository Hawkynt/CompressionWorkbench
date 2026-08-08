#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.OpenVms;

/// <summary>
/// Emits a fresh OpenVMS Files-11 (ODS-2) volume to the
/// <see cref="OpenVmsLayout"/> geometry. The resulting image carries:
/// <list type="bullet">
///   <item>A real Files-11 home block at LBN 1 with "DECFILE11A " at byte
///         0x1E8, structure level 0x0202, cluster size 1, owner UIC
///         [1,1], BITMAP.SYS LBN field set, max-files field set, plus the
///         CWB-OVMS-WB layout marker at byte 132 so our reader / modifier
///         recognise the geometry.</item>
///   <item>BITMAP.SYS at LBN 2..17 with metadata LBNs pre-marked allocated.</item>
///   <item>INDEXF.SYS at LBN 18..273 (256 reserved File-IDs); File-IDs 1
///         (INDEXF.SYS), 2 (BITMAP.SYS) and 4 (000000.DIR) are populated
///         with retrieval pointers covering their own LBNs.</item>
///   <item>000000.DIR at LBN 274 containing one directory entry per caller-supplied file.</item>
///   <item>Caller files laid out contiguously starting at LBN 275.</item>
/// </list>
/// <para>
/// Honest scope: this volume is NOT OpenVMS-mountable — the FH ident-area
/// metadata, the FILECHAR / RECATTR fields, and the home-block
/// HM2$W_CHECKSUM1/CHECKSUM2 surfaces are emitted as zeros. What it IS:
/// a layout our reader and in-place modifier can round-trip end-to-end.
/// </para>
/// </summary>
public sealed class OpenVmsWriter {

  /// <summary>Builds the volume image in memory. Throws <see cref="IOException"/> when inputs don't fit.</summary>
  public byte[] Build(IReadOnlyList<(string Name, byte[] Data)> files, string volumeLabel = "SCRATCH") {
    ArgumentNullException.ThrowIfNull(files);
    var payloads = new List<(string Name, FilePayload Payload)>(files.Count);
    foreach (var (name, data) in files)
      payloads.Add((name, FilePayload.FromBytes(data ?? [])));

    var plan = PlanVolume(payloads, volumeLabel);
    if (plan.VolumeBytes > Array.MaxLength)
      throw new IOException(
        $"OpenVMS: a {plan.VolumeBytes:N0}-byte volume exceeds the array limit; use BuildTo(Stream).");

    var image = new byte[plan.VolumeBytes];
    plan.Prefix.CopyTo(image.AsSpan());
    using var target = new MemoryStream(image, writable: true);
    plan.Payloads.FlushTo(target);
    return image;
  }

  /// <summary>
  /// Emits the volume to <paramref name="output"/>: the fixed metadata prefix
  /// first, then every file copied into the run the allocator gave it. Only the
  /// prefix and one copy buffer are ever resident, so the volume is bounded by
  /// the target's capacity rather than by <see cref="Array.MaxLength"/>.
  /// </summary>
  public void BuildTo(Stream output, IReadOnlyList<(string Name, FilePayload Payload)> files, string volumeLabel = "SCRATCH") {
    ArgumentNullException.ThrowIfNull(output);
    var plan = PlanVolume(files, volumeLabel);
    var basePosition = output.CanSeek ? output.Position : 0;
    output.Write(plan.Prefix);
    output.Flush();
    if (output.CanSeek) output.SetLength(basePosition + plan.VolumeBytes);
    plan.Payloads.FlushTo(output, basePosition);
    if (output.CanSeek) output.Position = basePosition + plan.VolumeBytes;
  }

  /// <summary>A laid-out volume: the metadata prefix, the total size, and where each payload goes.</summary>
  private sealed record VolumePlan(byte[] Prefix, long VolumeBytes, DeferredPayloads Payloads);

  private static VolumePlan PlanVolume(IReadOnlyList<(string Name, FilePayload Payload)> files, string volumeLabel) {
    ArgumentNullException.ThrowIfNull(files);
    ArgumentNullException.ThrowIfNull(volumeLabel);

    // ── Volume geometry ──
    // ODS-2 sizes the volume in clusters (HM2$W_CLUSTER), so BITMAP.SYS stays a
    // fixed 16 blocks however large the volume gets and every other LBN in the
    // layout keeps its place. Sizing the volume to the payload and picking the
    // cluster to suit is what lifts the old fixed 4 MB ceiling.
    var dataBlocks = 0L;
    foreach (var (_, payload) in files)
      dataBlocks += (payload.Size + OpenVmsLayout.BlockSize - 1) / OpenVmsLayout.BlockSize;

    var clusterBlocks = OpenVmsBitmap.ClusterFor(OpenVmsLayout.DataAreaStartLbn + dataBlocks + 1);
    // Each file's run starts on a cluster boundary, so budget one cluster of
    // rounding per file on top of the payload itself.
    var neededBlocks = OpenVmsLayout.DataAreaStartLbn + dataBlocks
                     + (files.Count + 1) * (long)clusterBlocks;
    var volumeBlocks = Math.Max(OpenVmsLayout.VolumeBlocks,
      (neededBlocks + clusterBlocks - 1) / clusterBlocks * clusterBlocks);
    clusterBlocks = OpenVmsBitmap.ClusterFor(volumeBlocks);

    // ODS-2 retrieval pointers and the bitmap index both address LBNs as signed
    // 32-bit values, so the volume tops out at 2^31 blocks (1 TB).
    if (volumeBlocks > int.MaxValue)
      throw new IOException(
        $"OpenVMS: a {volumeBlocks:N0}-block volume exceeds the 32-bit LBN limit.");

    var volumeBytes = volumeBlocks * (long)OpenVmsLayout.BlockSize;
    var image = new byte[OpenVmsLayout.MetadataBytes];
    var deferred = new DeferredPayloads();

    // ── Bitmap ──
    var bitmap = new OpenVmsBitmap { ClusterBlocks = clusterBlocks, VolumeBlocks = volumeBlocks };
    bitmap.MarkMetadataAllocated();

    // ── INDEXF.SYS — pre-populate the reserved FIDs (1, 2, 4) so a real ODS-2 walker
    //    would at least find the metadata files even though we don't expose them as user entries.
    var indexFile = new OpenVmsFileHeader[OpenVmsLayout.MaxFiles + 1];

    indexFile[OpenVmsLayout.IndexFileId] = new OpenVmsFileHeader {
      FileId = OpenVmsLayout.IndexFileId,
      Sequence = 1,
      InUse = true,
      Name = "INDEXF.SYS",
      Size = OpenVmsLayout.IndexFileBlockCount * (long)OpenVmsLayout.BlockSize,
    };
    indexFile[OpenVmsLayout.IndexFileId].Extents.Add(
      new OpenVmsFileHeader.RetrievalPointer(OpenVmsLayout.IndexFileStartLbn, OpenVmsLayout.IndexFileBlockCount));

    indexFile[OpenVmsLayout.BitmapFileId] = new OpenVmsFileHeader {
      FileId = OpenVmsLayout.BitmapFileId,
      Sequence = 1,
      InUse = true,
      Name = "BITMAP.SYS",
      Size = OpenVmsLayout.BitmapBlockCount * (long)OpenVmsLayout.BlockSize,
    };
    indexFile[OpenVmsLayout.BitmapFileId].Extents.Add(
      new OpenVmsFileHeader.RetrievalPointer(OpenVmsLayout.BitmapStartLbn, OpenVmsLayout.BitmapBlockCount));

    indexFile[OpenVmsLayout.RootDirectoryFileId] = new OpenVmsFileHeader {
      FileId = OpenVmsLayout.RootDirectoryFileId,
      Sequence = 1,
      InUse = true,
      Name = "000000.DIR",
      Size = OpenVmsLayout.BlockSize,
    };
    indexFile[OpenVmsLayout.RootDirectoryFileId].Extents.Add(
      new OpenVmsFileHeader.RetrievalPointer(OpenVmsLayout.RootDirectoryLbn, 1));

    // ── Caller files ──
    var directoryEntries = new List<OpenVmsDirectory.Entry>();
    var nextFid = OpenVmsLayout.FirstUserFileId;
    var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var (rawName, payload) in files) {
      var name = NormalizeName(rawName);
      if (!seenNames.Add(name))
        throw new IOException($"Duplicate file name '{name}' (ODS-2 directories are flat — names must be unique).");
      if (nextFid > OpenVmsLayout.MaxFiles)
        throw new IOException($"INDEXF.SYS full (max {OpenVmsLayout.MaxFiles - OpenVmsLayout.FirstUserFileId + 1} user files).");

      var blocks = (int)((payload.Size + OpenVmsLayout.BlockSize - 1) / OpenVmsLayout.BlockSize);
      var startLbn = blocks > 0 ? bitmap.AllocateRun(blocks) : 0;
      if (blocks > 0 && startLbn < 0)
        throw new IOException($"Volume full: cannot allocate {blocks} contiguous LBN(s) for '{name}'.");

      var fh = new OpenVmsFileHeader {
        FileId = nextFid,
        Sequence = 1,
        InUse = true,
        Name = name,
        Size = payload.Size,
      };
      if (blocks > 0) fh.Extents.Add(new OpenVmsFileHeader.RetrievalPointer(startLbn, blocks));
      indexFile[nextFid] = fh;

      directoryEntries.Add(new OpenVmsDirectory.Entry(nextFid, 1, name, payload.Size));

      if (blocks > 0)
        deferred.Add(OpenVmsLayout.LbnToByteOffset(startLbn), payload);

      nextFid++;
    }

    // ── Serialize INDEXF.SYS ──
    for (var fid = 1; fid <= OpenVmsLayout.MaxFiles; fid++) {
      var fh = indexFile[fid] ?? new OpenVmsFileHeader { FileId = fid, Sequence = 0, InUse = false };
      var fhBytes = fh.Serialize();
      fhBytes.CopyTo(image.AsSpan((int)OpenVmsLayout.FileHeaderByteOffset(fid)));
    }

    // ── Serialize 000000.DIR ──
    var rootDir = new byte[OpenVmsLayout.BlockSize];
    OpenVmsDirectory.WriteChainLink(rootDir, 0);
    var slot = OpenVmsDirectory.FileEntryStartSlot;
    foreach (var entry in directoryEntries) {
      if (slot >= OpenVmsDirectory.EntriesPerBlock)
        throw new IOException($"Root directory full ({OpenVmsDirectory.FileEntriesPerBlock} entries per block; directory growth requires the chain extension which the writer leaves unallocated).");
      OpenVmsDirectory.WriteEntry(rootDir, slot++, entry);
    }
    rootDir.CopyTo(image.AsSpan((int)OpenVmsLayout.LbnToByteOffset(OpenVmsLayout.RootDirectoryLbn)));

    // ── Serialize BITMAP.SYS ──
    bitmap.Bytes.AsSpan(0, OpenVmsLayout.BitmapBlockCount * OpenVmsLayout.BlockSize)
      .CopyTo(image.AsSpan((int)OpenVmsLayout.LbnToByteOffset(OpenVmsLayout.BitmapStartLbn)));

    // ── Serialize the home block at LBN 1 ──
    var hbOffset = (int)OpenVmsLayout.LbnToByteOffset(OpenVmsLayout.HomeBlockLbn);
    var hb = image.AsSpan(hbOffset, OpenVmsLayout.BlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(hb.Slice(OpenVmsLayout.HbHomeLbn, 4), OpenVmsLayout.HomeBlockLbn);
    BinaryPrimitives.WriteUInt32LittleEndian(hb.Slice(OpenVmsLayout.HbAltHomeLbn, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hb.Slice(OpenVmsLayout.HbAltIdxLbn, 4), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(hb.Slice(OpenVmsLayout.HbStrucLev, 2), 0x0202);    // ODS-2
    BinaryPrimitives.WriteUInt16LittleEndian(hb.Slice(OpenVmsLayout.HbCluster, 2), (ushort)clusterBlocks);          // 1 LBN per cluster
    BinaryPrimitives.WriteUInt16LittleEndian(hb.Slice(OpenVmsLayout.HbHomeVbn, 2), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(hb.Slice(OpenVmsLayout.HbIbMapLbn, 4), OpenVmsLayout.BitmapStartLbn);
    BinaryPrimitives.WriteUInt32LittleEndian(hb.Slice(OpenVmsLayout.HbMaxFiles, 4), OpenVmsLayout.MaxFiles);
    BinaryPrimitives.WriteUInt16LittleEndian(hb.Slice(OpenVmsLayout.HbIbMapSize, 2), OpenVmsLayout.BitmapBlockCount);
    BinaryPrimitives.WriteUInt32LittleEndian(hb.Slice(OpenVmsLayout.HbOwnerUic, 4), 0x00010001);  // [1,1]

    // Layout marker at byte 132 so our reader recognises this as a CWB-OVMS-WB volume.
    OpenVmsLayout.LayoutMarker.CopyTo(hb.Slice(OpenVmsLayout.LayoutMarkerOffset));

    // Format string + volume label.
    var fmt = Encoding.ASCII.GetBytes("DECFILE11A ");
    fmt.CopyTo(hb.Slice(OpenVmsLayout.HbFormatString));
    var label = volumeLabel.Length > 12 ? volumeLabel[..12] : volumeLabel.PadRight(12, ' ');
    Encoding.ASCII.GetBytes(label).CopyTo(hb.Slice(OpenVmsLayout.HbVolumeName));

    return new VolumePlan(image, volumeBytes, deferred);
  }

  /// <summary>
  /// Normalises a caller-supplied file name to the 24-char ASCII slot
  /// in <see cref="OpenVmsDirectory"/>. Forward slashes and backslashes
  /// are collapsed to dots so caller paths like "subdir/file.txt" still
  /// fit (ODS-2 directories are flat — we deliberately don't fabricate
  /// a subdirectory tree).
  /// </summary>
  public static string NormalizeName(string raw) {
    ArgumentNullException.ThrowIfNull(raw);
    var s = raw.Replace('\\', '.').Replace('/', '.');
    if (s.Length > OpenVmsDirectory.FileNameLength) s = s[..OpenVmsDirectory.FileNameLength];
    return s.ToUpperInvariant();
  }
}
