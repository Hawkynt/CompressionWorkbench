#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Driver-grade superblock discovery: reads the standalone layout sector,
/// follows every advertised superblock copy, preserves every variable field,
/// and selects the newest checksum-valid copy by sequence number.
/// </summary>
internal sealed class BcacheFsDeviceSuperblocks {
  internal required IReadOnlyList<long> AdvertisedSectors { get; init; }
  internal required IReadOnlyList<BcacheFsSuperblockRecord> Copies { get; init; }
  internal required BcacheFsSuperblockRecord? Current { get; init; }
  internal required IReadOnlyList<string> Diagnostics { get; init; }

  internal static BcacheFsDeviceSuperblocks Read(Stream device) {
    ArgumentNullException.ThrowIfNull(device);
    if (!device.CanRead || !device.CanSeek)
      throw new ArgumentException("bcachefs superblock discovery requires a readable seekable stream.", nameof(device));

    var diagnostics = new List<string>();
    var sectors = ReadLayout(device, diagnostics);
    if (sectors.Count == 0)
      sectors.Add(PrimarySbSector);

    var copies = new List<BcacheFsSuperblockRecord>();
    foreach (var sector in sectors.Distinct()) {
      if (BcacheFsSuperblockRecord.TryRead(device, sector, out var copy, out var error))
        copies.Add(copy!);
      else
        diagnostics.Add($"superblock@{sector}: {error}");
    }

    foreach (var copy in copies.Where(c => c.StructurallyValid)) {
      var checksum = copy.Checksum;
      if (!checksum.Valid)
        diagnostics.Add($"superblock@{copy.Sector}: {checksum.Diagnostic}");
    }

    // Mirrors read_backup_supers(): only checksum-valid copies participate in
    // sequence selection. A torn higher-seq write must never outrank an older
    // intact copy merely because its fixed fields still parse.
    var current = copies
      .Where(c => c.StructurallyValid && c.Checksum.Valid)
      .OrderByDescending(c => c.Sequence)
      .ThenByDescending(c => c.Sector)
      .FirstOrDefault();

    return new BcacheFsDeviceSuperblocks {
      AdvertisedSectors = sectors,
      Copies = copies,
      Current = current,
      Diagnostics = diagnostics,
    };
  }

  private static List<long> ReadLayout(Stream device, List<string> diagnostics) {
    var result = new List<long>();
    var offset = LayoutSector * SectorSize;
    if (offset < 0 || offset + SectorSize > device.Length) {
      diagnostics.Add("standalone superblock layout sector is outside the device.");
      return result;
    }

    var bytes = new byte[SectorSize];
    device.Position = offset;
    device.ReadExactly(bytes);
    if (!bytes.AsSpan(0, 16).SequenceEqual(Magic)) {
      diagnostics.Add("standalone superblock layout has no BCHFS magic.");
      return result;
    }

    var count = bytes[18];
    if (count > 61) {
      diagnostics.Add($"standalone superblock layout advertises {count} copies; maximum is 61.");
      count = 61;
    }

    for (var i = 0; i < count; ++i) {
      var sector = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(24 + 8 * i));
      if (sector > long.MaxValue) {
        diagnostics.Add($"superblock layout entry {i} is outside signed stream addressing.");
        continue;
      }
      result.Add((long)sector);
    }
    return result;
  }
}

/// <summary>One concrete copy of <c>struct bch_sb</c>.</summary>
internal sealed record BcacheFsSuperblockRecord {
  internal required long Sector { get; init; }
  internal required ulong ChecksumLo { get; init; }
  internal required ulong ChecksumHi { get; init; }
  internal required ushort Version { get; init; }
  internal required ushort VersionMin { get; init; }
  internal required byte[] InternalUuidBytes { get; init; }
  internal required byte[] UserUuidBytes { get; init; }
  internal required string Label { get; init; }
  internal required ulong StoredSector { get; init; }
  internal required ulong Sequence { get; init; }
  internal required ushort BlockSizeSectors { get; init; }
  internal required byte DeviceIndex { get; init; }
  internal required byte DeviceCount { get; init; }
  internal required uint VariableU64s { get; init; }
  internal required ulong[] Flags { get; init; }
  internal required ulong[] Features { get; init; }
  internal required ulong[] Compat { get; init; }
  internal required IReadOnlyList<BcacheFsSuperblockField> Fields { get; init; }
  internal required byte[] RawBytes { get; init; }
  internal required bool StructurallyValid { get; init; }

  internal ulong FilesystemMagic => BinaryPrimitives.ReadUInt64LittleEndian(this.InternalUuidBytes);
  internal bool Clean => (this.Flags[0] & (1UL << 1)) != 0;
  internal bool BigEndian => (this.Flags[0] & (1UL << 62)) != 0;
  internal BcacheFsChecksumType ChecksumType => (BcacheFsChecksumType)((this.Flags[0] >> 2) & 0x3F);
  internal int BtreeNodeSectors => checked((int)((this.Flags[0] >> 12) & 0xFFFF));
  internal BcacheFsChecksumVerification Checksum
    => BcacheFsChecksumCodec.VerifyVstruct(this.ChecksumType, this.RawBytes);

  internal IEnumerable<BcacheFsSuperblockField> FieldsOf(BcacheFsSuperblockFieldType type)
    => this.Fields.Where(f => f.KnownType == type);

  internal IReadOnlyList<BcacheFsJournalBucketRange> JournalRanges() {
    var v2 = this.FieldsOf(BcacheFsSuperblockFieldType.JournalV2).ToList();
    if (v2.Count != 0)
      return v2.SelectMany(ParseJournalV2).ToArray();

    return this.FieldsOf(BcacheFsSuperblockFieldType.Journal)
      .SelectMany(ParseJournalV1)
      .Select(bucket => new BcacheFsJournalBucketRange(bucket, 1))
      .ToArray();
  }

  internal static bool TryRead(Stream device, long sector, out BcacheFsSuperblockRecord? record, out string error) {
    record = null;
    error = string.Empty;
    if (sector < 0 || sector > long.MaxValue / SectorSize) {
      error = "sector offset overflows stream addressing.";
      return false;
    }

    var byteOffset = sector * SectorSize;
    if (byteOffset + SbFixedBytes > device.Length) {
      error = "fixed superblock header is outside the device.";
      return false;
    }

    var fixedBytes = new byte[SbFixedBytes];
    device.Position = byteOffset;
    device.ReadExactly(fixedBytes);
    if (!fixedBytes.AsSpan(24, 16).SequenceEqual(Magic)) {
      error = "BCHFS magic is missing.";
      return false;
    }

    var variableU64s = BinaryPrimitives.ReadUInt32LittleEndian(fixedBytes.AsSpan(124));
    var variableBytes = (long)variableU64s * sizeof(ulong);
    var total = SbFixedBytes + variableBytes;
    if (total > int.MaxValue || byteOffset + total > device.Length) {
      error = $"superblock claims {total} bytes outside the device.";
      return false;
    }

    var raw = new byte[(int)total];
    device.Position = byteOffset;
    device.ReadExactly(raw);

    var fields = new List<BcacheFsSuperblockField>();
    var cursor = SbFixedBytes;
    var structurallyValid = true;
    while (cursor < raw.Length) {
      if (raw.Length - cursor < 8) {
        structurallyValid = false;
        error = "variable superblock field header is truncated.";
        break;
      }
      var words = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(cursor));
      var rawType = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(cursor + 4));
      if (words == 0) {
        structurallyValid = false;
        error = "variable superblock field has zero length.";
        break;
      }
      var bytes = (long)words * sizeof(ulong);
      if (bytes < 8 || bytes > int.MaxValue || cursor + bytes > raw.Length) {
        structurallyValid = false;
        error = $"superblock field {rawType} runs outside the variable area.";
        break;
      }

      var fieldBytes = raw.AsSpan(cursor, (int)bytes).ToArray();
      fields.Add(new BcacheFsSuperblockField(rawType, fieldBytes));
      cursor += (int)bytes;
    }

    var flags = new ulong[7];
    for (var i = 0; i < flags.Length; ++i)
      flags[i] = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(144 + 8 * i));
    var features = new[] {
      BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(208)),
      BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(216)),
    };
    var compat = new[] {
      BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(224)),
      BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(232)),
    };

    var labelBytes = raw.AsSpan(72, 32);
    var nul = labelBytes.IndexOf((byte)0);
    if (nul < 0) nul = labelBytes.Length;

    var storedSector = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(104));
    if (storedSector != (ulong)sector) {
      structurallyValid = false;
      if (error.Length == 0)
        error = $"superblock stored offset {storedSector} does not match copy sector {sector}.";
    }

    record = new BcacheFsSuperblockRecord {
      Sector = sector,
      ChecksumLo = BinaryPrimitives.ReadUInt64LittleEndian(raw),
      ChecksumHi = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(8)),
      Version = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(16)),
      VersionMin = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(18)),
      InternalUuidBytes = raw.AsSpan(40, 16).ToArray(),
      UserUuidBytes = raw.AsSpan(56, 16).ToArray(),
      Label = Encoding.UTF8.GetString(labelBytes[..nul]),
      StoredSector = storedSector,
      Sequence = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(112)),
      BlockSizeSectors = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(120)),
      DeviceIndex = raw[122],
      DeviceCount = raw[123],
      VariableU64s = variableU64s,
      Flags = flags,
      Features = features,
      Compat = compat,
      Fields = fields,
      RawBytes = raw,
      StructurallyValid = structurallyValid,
    };
    return structurallyValid;
  }

  private static IEnumerable<long> ParseJournalV1(BcacheFsSuperblockField field) {
    var bytes = field.RawBytes;
    for (var offset = 8; offset + 8 <= bytes.Length; offset += 8) {
      var bucket = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
      if (bucket <= long.MaxValue) yield return (long)bucket;
    }
  }

  private static IEnumerable<BcacheFsJournalBucketRange> ParseJournalV2(BcacheFsSuperblockField field) {
    var bytes = field.RawBytes;
    for (var offset = 8; offset + 16 <= bytes.Length; offset += 16) {
      var start = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
      var count = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 8));
      if (start > long.MaxValue || count > long.MaxValue) continue;
      yield return new BcacheFsJournalBucketRange((long)start, (long)count);
    }
  }
}

internal sealed record BcacheFsSuperblockField(uint RawType, byte[] RawBytes) {
  internal BcacheFsSuperblockFieldType? KnownType
    => Enum.IsDefined(typeof(BcacheFsSuperblockFieldType), this.RawType)
      ? (BcacheFsSuperblockFieldType)this.RawType
      : null;
}

internal readonly record struct BcacheFsJournalBucketRange(long FirstBucket, long Count) {
  internal IEnumerable<long> Buckets() {
    for (var i = 0L; i < this.Count; ++i)
      yield return checked(this.FirstBucket + i);
  }
}
