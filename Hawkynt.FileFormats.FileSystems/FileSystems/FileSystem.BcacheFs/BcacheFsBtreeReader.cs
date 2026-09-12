#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Driver-core b-tree reader. It follows btree_ptr replicas across member
/// devices, validates node identity and every log-structured bset checksum,
/// decodes packed or unpacked keys, applies the kernel's compatible historical
/// key transforms, and recursively walks the complete on-disk depth range.
/// </summary>
internal static class BcacheFsBtreeReader {
  private const int NodeHeaderBytes = 136;
  private const int BsetHeaderBytes = 24;
  private const int FirstKeysOffset = NodeHeaderBytes + BsetHeaderBytes;
  private const int AppendedEntryHeaderBytes = 16 + BsetHeaderBytes;
  private const ushort MetadataVersionMin = 9; // bcachefs_metadata_version_min
  private const ushort VersionBkeyRenumber = 10;
  private const ushort VersionInodeBtreeChange = 11;
  private const ushort VersionSnapshot = 12;

  internal static BcacheFsBtreeReadResult ReadTree(BcacheFsCoreVolume volume, BcacheFsBtreeId id) {
    ArgumentNullException.ThrowIfNull(volume);
    var diagnostics = new List<string>();
    var root = volume.Root(id);
    if (root == null)
      return new BcacheFsBtreeReadResult(
        id, [], [], [], diagnostics.Append($"btree {id} has no effective root.").ToArray(), false);

    if (!BcacheFsExtentCodec.TryReadBtreePointer(root.Key, volume.Superblock, out var rootPointer, out var rootError))
      return new BcacheFsBtreeReadResult(
        id, [], [], [], diagnostics.Append($"btree {id} root: {rootError}").ToArray(), false);

    var nodes = new List<BcacheFsBtreeNodeRecord>();
    var leafSlots = new List<BcacheFsRawKey>();
    var rawLeafRuns = new List<BcacheFsBsetRecord>();
    var visited = new HashSet<BcacheFsPhysicalNodeIdentity>();
    var complete = ReadSubtree(
      volume,
      (byte)id,
      root.Level,
      rootPointer!,
      nodes,
      leafSlots,
      rawLeafRuns,
      diagnostics,
      visited);

    leafSlots.Sort((a, b) => Compare(a.Position, b.Position));
    return new BcacheFsBtreeReadResult(id, nodes, leafSlots, rawLeafRuns, diagnostics, complete);
  }

  private static bool ReadSubtree(
      BcacheFsCoreVolume volume,
      byte btreeId,
      byte expectedLevel,
      BcacheFsBtreePointer pointer,
      List<BcacheFsBtreeNodeRecord> nodes,
      List<BcacheFsRawKey> leafSlots,
      List<BcacheFsBsetRecord> rawLeafRuns,
      List<string> diagnostics,
      HashSet<BcacheFsPhysicalNodeIdentity> visited) {
    if (expectedLevel >= BcacheFsOnDiskCatalog.MaxBtreeDepth) {
      diagnostics.Add($"btree {btreeId} level {expectedLevel} exceeds maximum depth {BcacheFsOnDiskCatalog.MaxBtreeDepth - 1}.");
      return false;
    }

    if (!TryReadNode(volume, btreeId, expectedLevel, pointer, diagnostics, out var node))
      return false;

    var identity = new BcacheFsPhysicalNodeIdentity(
      node!.PhysicalPointer.Device,
      node.PhysicalPointer.Sector,
      node.FirstSetSequence);
    if (!visited.Add(identity)) {
      diagnostics.Add($"btree {btreeId} contains a cycle through device {identity.Device} sector {identity.Sector} seq {identity.Sequence}.");
      return false;
    }

    nodes.Add(node);
    var slots = ComposeNodeSlots(volume, node);
    var complete = true;

    if (node.Level == 0) {
      leafSlots.AddRange(slots.Values);
      rawLeafRuns.AddRange(node.Sets);
      return true;
    }

    Bpos? previousChildMax = null;
    foreach (var key in slots.Values.OrderBy(k => k.Position, BposComparer.Instance)) {
      if (key.Type is not (BcacheFsKeyType.BtreePtr or BcacheFsKeyType.BtreePtrV2)) {
        diagnostics.Add($"btree {btreeId} interior level {node.Level} contains key type {key.RawType} at {Format(key.Position)}.");
        complete = false;
        continue;
      }

      if (!BcacheFsExtentCodec.TryReadBtreePointer(key, volume.Superblock, out var child, out var childError)) {
        diagnostics.Add($"btree {btreeId} child at {Format(key.Position)}: {childError}");
        complete = false;
        continue;
      }

      if (Compare(child!.MaxKey, node.MinKey) < 0 || Compare(child.MaxKey, node.MaxKey) > 0) {
        diagnostics.Add($"btree {btreeId} child max {Format(child.MaxKey)} lies outside parent range {Format(node.MinKey)}..{Format(node.MaxKey)}.");
        complete = false;
        continue;
      }

      if (!child.Legacy) {
        if (Compare(child.MinKey, child.MaxKey) > 0) {
          diagnostics.Add($"btree {btreeId} child range {Format(child.MinKey)}..{Format(child.MaxKey)} is inverted.");
          complete = false;
          continue;
        }
        if (Compare(child.MinKey, node.MinKey) < 0 || Compare(child.MinKey, node.MaxKey) > 0) {
          diagnostics.Add($"btree {btreeId} child min {Format(child.MinKey)} lies outside parent range {Format(node.MinKey)}..{Format(node.MaxKey)}.");
          complete = false;
          continue;
        }
        if (previousChildMax is { } previous && Compare(previous, child.MinKey) >= 0) {
          diagnostics.Add($"btree {btreeId} interior level {node.Level} has overlapping child ranges at {Format(child.MinKey)}.");
          complete = false;
          continue;
        }
      }

      previousChildMax = child.MaxKey;
      if (!ReadSubtree(
          volume,
          btreeId,
          checked((byte)(node.Level - 1)),
          child,
          nodes,
          leafSlots,
          rawLeafRuns,
          diagnostics,
          visited))
        complete = false;
    }

    return complete;
  }

  /// <summary>
  /// Composes exact bkey slots inside one node. Bsets are an append log, so a
  /// later visible bset wins at the same position; the recovery journal is newer
  /// still. Blacklisted bsets remain in RawLeafRuns but do not participate.
  /// Extent-range overlap is deliberately not flattened here: partial extent
  /// overwrites require preserving checksum/compression source bounds and are
  /// handled by the extent view layer rather than by inventing trimmed bkeys.
  /// </summary>
  internal static IReadOnlyDictionary<Bpos, BcacheFsRawKey> ComposeNodeSlots(
      BcacheFsCoreVolume volume,
      BcacheFsBtreeNodeRecord node) {
    var result = new Dictionary<Bpos, BcacheFsRawKey>();
    foreach (var set in node.Sets.Where(s => s.Visible)) {
      foreach (var key in set.Keys) {
        // RANGE_UPDATED permits the pointer to narrow a node without rewriting
        // the old bsets immediately. Kernel read drops keys outside that new
        // effective range after sorting; do the same in the logical view.
        if (Compare(key.Position, node.MinKey) < 0 || Compare(key.Position, node.MaxKey) > 0)
          continue;

        if (key.Type == BcacheFsKeyType.Deleted)
          result.Remove(key.Position);
        else
          result[key.Position] = key;
      }
    }

    foreach (var update in volume.Overlay.Keys(node.BtreeId, node.Level)
      .OrderBy(k => k.Sequence)
      .ThenBy(k => k.JournalOrder)) {
      if (Compare(update.Key.Position, node.MinKey) < 0 || Compare(update.Key.Position, node.MaxKey) > 0)
        continue;

      // Accounting keys are deltas, not replacement slots. Their replay is a
      // separate semantic operation and must not destroy the checkpoint value.
      if (update.Key.Type == BcacheFsKeyType.Accounting)
        continue;

      if (update.Key.Type == BcacheFsKeyType.Deleted)
        result.Remove(update.Key.Position);
      else
        result[update.Key.Position] = update.Key;
    }
    return result;
  }

  private static bool TryReadNode(
      BcacheFsCoreVolume volume,
      byte expectedBtree,
      byte expectedLevel,
      BcacheFsBtreePointer pointer,
      List<string> diagnostics,
      out BcacheFsBtreeNodeRecord? node) {
    node = null;
    var failures = new List<string>();

    foreach (var replica in pointer.Replicas) {
      if (replica.Unused || replica.Unwritten) {
        failures.Add($"device {replica.Device} sector {replica.Sector}: pointer is {(replica.Unused ? "unused" : "unwritten")}.");
        continue;
      }
      if (!volume.Devices.TryGetValue(replica.Device, out var device)) {
        failures.Add($"device {replica.Device} sector {replica.Sector}: member device was not supplied.");
        continue;
      }

      if (TryReadReplica(
          volume,
          device,
          replica,
          pointer,
          expectedBtree,
          expectedLevel,
          out node,
          out var error))
        return true;

      failures.Add($"device {replica.Device} sector {replica.Sector}: {error}");
    }

    diagnostics.Add($"btree {expectedBtree} level {expectedLevel} node could not be read from any replica: {string.Join("; ", failures)}");
    return false;
  }

  private static bool TryReadReplica(
      BcacheFsCoreVolume volume,
      Stream device,
      BcacheFsExtentPointer physical,
      BcacheFsBtreePointer pointer,
      byte expectedBtree,
      byte expectedLevel,
      out BcacheFsBtreeNodeRecord? node,
      out string error) {
    node = null;
    error = string.Empty;
    var superblock = volume.Superblock;

    var blockSectors = superblock.BlockSizeSectors;
    if (blockSectors == 0 || (blockSectors & (blockSectors - 1)) != 0) {
      error = $"invalid bcachefs block size {blockSectors} sectors.";
      return false;
    }
    if (superblock.BtreeNodeSectors <= 0 || blockSectors > superblock.BtreeNodeSectors) {
      error = $"block size {blockSectors} sectors exceeds b-tree node size {superblock.BtreeNodeSectors}.";
      return false;
    }

    var explicitSectors = pointer.SectorsWritten;
    if (explicitSectors > superblock.BtreeNodeSectors) {
      error = $"pointer claims {explicitSectors} sectors_written; node size is {superblock.BtreeNodeSectors}.";
      return false;
    }
    if (explicitSectors != 0 && explicitSectors % blockSectors != 0) {
      error = $"pointer sectors_written {explicitSectors} is not aligned to block size {blockSectors}.";
      return false;
    }

    // btree_ptr and early btree_ptr_v2 with sectors_written == 0 require reading
    // the full node allocation and stopping at the first nonmatching bset seq.
    var sectorsToRead = explicitSectors == 0 ? superblock.BtreeNodeSectors : explicitSectors;
    var bytesToRead = checked(sectorsToRead * SectorSize);
    var byteOffset = checked(physical.Sector * (long)SectorSize);
    if (byteOffset < 0 || byteOffset + bytesToRead > device.Length) {
      error = $"node range {physical.Sector}+{sectorsToRead} sectors lies outside device.";
      return false;
    }

    var bytes = new byte[bytesToRead];
    device.Position = byteOffset;
    device.ReadExactly(bytes);
    if (bytes.Length < FirstKeysOffset) {
      error = "btree node is shorter than its first bset header.";
      return false;
    }

    var expectedMagic = superblock.FilesystemMagic ^ BcacheFsOnDiskCatalog.BtreeSetMagicXor;
    var magic = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16));
    if (magic != expectedMagic) {
      error = $"btree node magic 0x{magic:X16} does not match filesystem magic 0x{expectedMagic:X16}.";
      return false;
    }

    var flags = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(24));
    var rawBtree = (flags & 0xFUL) | (((flags >> 9) & 0xFFFFUL) << 4);
    var actualLevel = (byte)((flags >> 4) & 0xF);
    if (rawBtree != expectedBtree) {
      error = $"node belongs to raw btree id {rawBtree}, expected {expectedBtree}.";
      return false;
    }
    if (actualLevel != expectedLevel) {
      error = $"node level is {actualLevel}, expected {expectedLevel}.";
      return false;
    }
    var actualBtree = expectedBtree;

    var firstBsetFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(NodeHeaderBytes + 16));
    var firstVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(NodeHeaderBytes + 20));
    if (!IsVersionCompatible(firstVersion)) {
      error = $"unsupported bset version {firstVersion >> 10}.{firstVersion & 0x3FF}.";
      return false;
    }
    var firstBigEndian = (firstBsetFlags & (1U << 4)) != 0;

    var storedMinKey = CompatNodeBpos(
      ReadStoredBpos(bytes.AsSpan(32), firstBigEndian), expectedBtree, firstVersion);
    var storedMaxKey = CompatNodeBpos(
      ReadStoredBpos(bytes.AsSpan(52), firstBigEndian), expectedBtree, firstVersion);
    var format = BcacheFsKeyFormat.Read(bytes.AsSpan(80, 56));
    if (!TryValidateKeyFormat(format, out error))
      return false;

    var sets = new List<BcacheFsBsetRecord>();
    if (!TryReadSet(bytes, 0, true, format, volume, expectedBtree, expectedLevel, out var first, out error))
      return false;
    if (!first!.Visible) {
      error = $"first bset has blacklisted journal sequence {first.JournalSequence}.";
      return false;
    }
    sets.Add(first);

    if (pointer.Legacy) {
      if (first.Sequence == 0) {
        error = "legacy b-tree pointer references a node with bset sequence zero.";
        return false;
      }
    } else if (pointer.Sequence != first.Sequence) {
      error = $"btree pointer seq {pointer.Sequence} does not match node bset seq {first.Sequence}.";
      return false;
    }

    Bpos minKey;
    Bpos maxKey;
    if (pointer.RangeUpdated) {
      if (pointer.Legacy) {
        error = "legacy b-tree pointer cannot carry RANGE_UPDATED.";
        return false;
      }
      minKey = pointer.MinKey;
      maxKey = pointer.MaxKey;
    } else {
      minKey = storedMinKey;
      maxKey = storedMaxKey;
      if (!pointer.Legacy && Compare(pointer.MinKey, storedMinKey) != 0) {
        error = $"btree pointer min {Format(pointer.MinKey)} does not match node min {Format(storedMinKey)}.";
        return false;
      }
      // The pointer key is the node's high bound for both legacy and v2.
      if (Compare(pointer.MaxKey, storedMaxKey) != 0) {
        error = $"btree pointer max {Format(pointer.MaxKey)} does not match node max {Format(storedMaxKey)}.";
        return false;
      }
    }

    if (Compare(minKey, maxKey) > 0) {
      error = $"btree node range {Format(minKey)}..{Format(maxKey)} is inverted.";
      return false;
    }

    var blockBytes = checked(blockSectors * SectorSize);
    var next = RoundUp(first.EndByte, blockBytes);
    while (next + AppendedEntryHeaderBytes <= bytes.Length) {
      var candidateSequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(next + 16));
      if (candidateSequence != first.Sequence) {
        if (explicitSectors != 0) {
          error = $"btree node data ends at sector {next / SectorSize}, before sectors_written {explicitSectors}.";
          return false;
        }

        if (TryFindVisibleBsetSignatureAfterEnd(
            bytes,
            next + blockBytes,
            blockBytes,
            first.Sequence,
            volume,
            out var laterSector)) {
          error = $"found live bset signature after node end at sector offset {laterSector}.";
          return false;
        }
        break;
      }

      if (!TryReadSet(bytes, next, false, format, volume, expectedBtree, expectedLevel, out var appended, out error))
        return false;
      if (!appended!.Visible && explicitSectors != 0) {
        error = $"blacklisted bset journal sequence {appended.JournalSequence} lies inside sectors_written at sector {next / SectorSize}.";
        return false;
      }
      sets.Add(appended);

      var advanced = RoundUp(appended.EndByte, blockBytes);
      if (advanced <= next) {
        error = $"bset at sector offset {next / SectorSize} does not advance the node reader.";
        return false;
      }
      next = advanced;
    }

    if (explicitSectors != 0 && next != bytes.Length) {
      error = $"btree node data accounts for {next / SectorSize} sectors but pointer records {explicitSectors}.";
      return false;
    }

    if (!pointer.RangeUpdated)
      foreach (var set in sets)
        foreach (var key in set.Keys)
          if (Compare(key.Position, minKey) < 0 || Compare(key.Position, maxKey) > 0) {
            error = $"bkey {Format(key.Position)} lies outside node range {Format(minKey)}..{Format(maxKey)}.";
            return false;
          }

    node = new BcacheFsBtreeNodeRecord(
      actualBtree,
      actualLevel,
      minKey,
      maxKey,
      (uint)(flags >> 32),
      first.Sequence,
      format,
      physical,
      sets,
      bytes);
    return true;
  }

  private static bool TryReadSet(
      byte[] node,
      int entryOffset,
      bool first,
      BcacheFsKeyFormat keyFormat,
      BcacheFsCoreVolume volume,
      byte btreeId,
      byte level,
      out BcacheFsBsetRecord? set,
      out string error) {
    set = null;
    var bsetOffset = first ? NodeHeaderBytes : entryOffset + 16;
    var keysOffset = first ? FirstKeysOffset : entryOffset + AppendedEntryHeaderBytes;
    if (bsetOffset + BsetHeaderBytes > node.Length) {
      error = "bset header is truncated.";
      return false;
    }

    var sequence = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(bsetOffset));
    var journalSequence = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(bsetOffset + 8));
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(bsetOffset + 16));
    var version = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(bsetOffset + 20));
    var keyU64s = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(bsetOffset + 22));
    var end = checked(keysOffset + keyU64s * sizeof(ulong));
    if (end > node.Length) {
      error = $"bset at sector offset {entryOffset / SectorSize} overruns the bytes read for this node.";
      return false;
    }

    if (!IsVersionCompatible(version)) {
      error = $"unsupported bset version {version >> 10}.{version & 0x3FF}.";
      return false;
    }
    if (!first && keyU64s == 0) {
      error = $"appended bset at sector offset {entryOffset / SectorSize} is empty.";
      return false;
    }
    if ((flags & (1U << 5)) != 0) {
      error = "BSET_SEPARATE_WHITEOUTS is obsolete and unsupported by current bcachefs.";
      return false;
    }

    var recordedOffset = (int)(flags >> 16);
    if (!first && recordedOffset != 0 && recordedOffset != entryOffset / SectorSize) {
      error = $"bset records sector offset {recordedOffset} but is stored at {entryOffset / SectorSize}.";
      return false;
    }

    var rawCsumType = (byte)(flags & 0xF);
    if (!Enum.IsDefined(typeof(BcacheFsChecksumType), rawCsumType)) {
      error = $"bset uses unknown checksum type {rawCsumType}.";
      return false;
    }
    var csumType = (BcacheFsChecksumType)rawCsumType;
    var checksumStart = first ? 0 : entryOffset;
    var verification = BcacheFsChecksumCodec.VerifyVstruct(
      csumType,
      node.AsSpan(checksumStart, end - checksumStart));
    if (!verification.Valid) {
      error = verification.Diagnostic;
      return false;
    }

    var bigEndian = (flags & (1U << 4)) != 0;
    var keys = new List<BcacheFsRawKey>();
    var cursor = keysOffset;
    Bpos? previous = null;
    while (cursor < end) {
      if (node[cursor] == 0) {
        error = $"bset at sector offset {entryOffset / SectorSize} contains a zero-length bkey.";
        return false;
      }
      var keyBytes = node[cursor] * sizeof(ulong);
      if (cursor + keyBytes > end) {
        error = $"bkey at byte {cursor} overruns bset ending at {end}.";
        return false;
      }
      if (!BcacheFsRawKeyCodec.TryDecode(
          node.AsSpan(cursor, keyBytes),
          keyFormat,
          out var key,
          out var keyError,
          bigEndian)) {
        error = $"bkey at byte {cursor}: {keyError}";
        return false;
      }

      key = ApplyKeyCompatibility(key!, btreeId, level, version);
      if (previous is { } p && Compare(p, key.Position) > 0) {
        error = $"bset keys are out of order at {Format(key.Position)}.";
        return false;
      }
      previous = key.Position;
      keys.Add(key);
      cursor += keyBytes;
    }

    var visible = journalSequence == 0 || !volume.Overlay.IsBlacklisted(journalSequence);
    set = new BcacheFsBsetRecord(
      entryOffset / SectorSize,
      sequence,
      journalSequence,
      flags,
      version,
      csumType,
      visible,
      keys,
      end);
    error = string.Empty;
    return true;
  }

  private static BcacheFsRawKey ApplyKeyCompatibility(
      BcacheFsRawKey key,
      byte btreeId,
      byte level,
      ushort version) {
    var rawType = key.RawType;
    if (version < VersionBkeyRenumber)
      rawType = RenumberLegacyKeyType(btreeId, level, rawType);

    var position = key.Position;
    if (version < VersionInodeBtreeChange && btreeId == (byte)BcacheFsBtreeId.Inodes)
      position = new Bpos(position.Offset, position.Inode, position.Snapshot);

    if (version < VersionSnapshot && (level != 0 || HasSnapshots(btreeId)))
      position = position with { Snapshot = uint.MaxValue };

    return key with { RawType = rawType, Position = position };
  }

  private static byte RenumberLegacyKeyType(byte btreeId, byte level, byte rawType) {
    if (level != 0)
      return rawType == 128 ? (byte)BcacheFsKeyType.BtreePtr : rawType;

    return ((BcacheFsBtreeId)btreeId, rawType) switch {
      (BcacheFsBtreeId.Extents, 128 or 129) => (byte)BcacheFsKeyType.Extent,
      (BcacheFsBtreeId.Extents, 130) => (byte)BcacheFsKeyType.Reservation,
      (BcacheFsBtreeId.Inodes, 128) => (byte)BcacheFsKeyType.Inode,
      (BcacheFsBtreeId.Inodes, 130) => (byte)BcacheFsKeyType.InodeGeneration,
      (BcacheFsBtreeId.Dirents, 128) => (byte)BcacheFsKeyType.Dirent,
      (BcacheFsBtreeId.Dirents, 129) => (byte)BcacheFsKeyType.HashWhiteout,
      (BcacheFsBtreeId.Xattrs, 128) => (byte)BcacheFsKeyType.Xattr,
      (BcacheFsBtreeId.Xattrs, 129) => (byte)BcacheFsKeyType.HashWhiteout,
      (BcacheFsBtreeId.Alloc, 128) => (byte)BcacheFsKeyType.Alloc,
      (BcacheFsBtreeId.Quotas, 128) => (byte)BcacheFsKeyType.Quota,
      _ => rawType,
    };
  }

  private static Bpos CompatNodeBpos(Bpos position, byte btreeId, ushort version)
    => version < VersionInodeBtreeChange && btreeId == (byte)BcacheFsBtreeId.Inodes
      ? new Bpos(position.Offset, position.Inode, position.Snapshot)
      : position;

  private static Bpos ReadStoredBpos(ReadOnlySpan<byte> source, bool bigEndian) {
    if (!bigEndian)
      return ReadBpos(source);

    Span<byte> canonical = stackalloc byte[20];
    source[..20].CopyTo(canonical);
    canonical.Reverse();
    return ReadBpos(canonical);
  }

  private static bool HasSnapshots(byte btreeId)
    => btreeId is
      (byte)BcacheFsBtreeId.Extents or
      (byte)BcacheFsBtreeId.Inodes or
      (byte)BcacheFsBtreeId.Dirents or
      (byte)BcacheFsBtreeId.Xattrs;

  private static bool IsVersionCompatible(ushort version)
    => version >= MetadataVersionMin &&
       (version >> 10) <= BcacheFsOnDiskCatalog.MetadataVersionMajor;

  private static bool TryValidateKeyFormat(BcacheFsKeyFormat format, out string error) {
    if (format.FieldCount != BcacheFsKeyFormat.FieldCountCurrent) {
      error = $"bkey_format declares {format.FieldCount} fields; expected {BcacheFsKeyFormat.FieldCountCurrent}.";
      return false;
    }
    if (format.KeyU64s <= 0 || format.KeyU64s > BkeyU64s) {
      error = $"bkey_format key_u64s is {format.KeyU64s}; expected 1..{BkeyU64s}.";
      return false;
    }

    var availableBits = checked(format.KeyU64s * 64 - 24);
    var usedBits = 0;
    for (var i = 0; i < BcacheFsKeyFormat.FieldCountCurrent; ++i) {
      var bits = format.Bits[i];
      if (bits is < 0 or > 64) {
        error = $"bkey_format field {i} has invalid width {bits}.";
        return false;
      }
      usedBits += bits;
    }
    if (usedBits > availableBits) {
      error = $"bkey_format needs {usedBits} packed bits but key_u64s provides only {availableBits}.";
      return false;
    }

    error = string.Empty;
    return true;
  }

  private static bool TryFindVisibleBsetSignatureAfterEnd(
      byte[] node,
      int start,
      int blockBytes,
      ulong sequence,
      BcacheFsCoreVolume volume,
      out int sectorOffset) {
    for (var offset = start; offset + AppendedEntryHeaderBytes <= node.Length; offset += blockBytes) {
      var bsetOffset = offset + 16;
      if (BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(bsetOffset)) != sequence)
        continue;
      var journalSequence = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(bsetOffset + 8));
      if (journalSequence != 0 && volume.Overlay.IsBlacklisted(journalSequence))
        continue;
      sectorOffset = offset / SectorSize;
      return true;
    }

    sectorOffset = -1;
    return false;
  }

  private static int RoundUp(int value, int alignment)
    => checked((value + alignment - 1) / alignment * alignment);

  private static string Format(Bpos position)
    => $"{position.Inode}:{position.Offset}:{position.Snapshot}";

  private sealed class BposComparer : IComparer<Bpos> {
    internal static readonly BposComparer Instance = new();
    public int Compare(Bpos x, Bpos y) => BcacheFsFormat.Compare(x, y);
  }
}

internal sealed record BcacheFsBtreeReadResult(
  BcacheFsBtreeId BtreeId,
  IReadOnlyList<BcacheFsBtreeNodeRecord> Nodes,
  IReadOnlyList<BcacheFsRawKey> MaterializedLeafSlots,
  IReadOnlyList<BcacheFsBsetRecord> RawLeafRuns,
  IReadOnlyList<string> Diagnostics,
  bool Complete) {
  internal IEnumerable<BcacheFsJournalKeyUpdate> AccountingJournalDeltas(BcacheFsCoreVolume volume)
    => volume.Overlay.KeyUpdates.Where(k =>
      k.BtreeId == (byte)this.BtreeId &&
      k.Level == 0 &&
      k.Key.Type == BcacheFsKeyType.Accounting);
}

internal sealed record BcacheFsBtreeNodeRecord(
  byte BtreeId,
  byte Level,
  Bpos MinKey,
  Bpos MaxKey,
  uint NodeSequence,
  ulong FirstSetSequence,
  BcacheFsKeyFormat KeyFormat,
  BcacheFsExtentPointer PhysicalPointer,
  IReadOnlyList<BcacheFsBsetRecord> Sets,
  byte[] RawBytes);

internal sealed record BcacheFsBsetRecord(
  int SectorOffset,
  ulong Sequence,
  ulong JournalSequence,
  uint Flags,
  ushort Version,
  BcacheFsChecksumType ChecksumType,
  bool Visible,
  IReadOnlyList<BcacheFsRawKey> Keys,
  int EndByte);

internal readonly record struct BcacheFsPhysicalNodeIdentity(byte Device, long Sector, ulong Sequence);
