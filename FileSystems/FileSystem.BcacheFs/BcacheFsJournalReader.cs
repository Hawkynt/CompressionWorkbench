#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Reads the physical journal ring of one member device. The result keeps every
/// replica by sequence number and records checksum/parse state separately so a
/// multi-device recovery layer can prefer a good replica without discarding the
/// evidence that another copy was damaged.
/// </summary>
internal static class BcacheFsJournalReader {
  internal static BcacheFsJournalDeviceLog ReadDevice(
      Stream device,
      BcacheFsSuperblockRecord superblock) {
    ArgumentNullException.ThrowIfNull(device);
    ArgumentNullException.ThrowIfNull(superblock);
    if (!device.CanRead || !device.CanSeek)
      throw new ArgumentException("journal reading requires a readable seekable device stream.", nameof(device));

    var diagnostics = new List<string>();
    var members = BcacheFsMembers.Read(superblock);
    if (superblock.DeviceIndex >= members.Count)
      return new BcacheFsJournalDeviceLog(superblock.DeviceIndex, [],
        [$"members table has {members.Count} entries but superblock device index is {superblock.DeviceIndex}."]);

    var member = members[superblock.DeviceIndex];
    if (member.BucketSizeSectors == 0)
      return new BcacheFsJournalDeviceLog(superblock.DeviceIndex, [], ["member bucket size is zero."]);

    var blockSectors = Math.Max(1, (int)superblock.BlockSizeSectors);
    var blockBytes = checked(blockSectors * SectorSize);
    var bucketBytes = checked((int)member.BucketSizeSectors * SectorSize);
    var replicas = new List<BcacheFsJournalReplica>();

    foreach (var range in superblock.JournalRanges())
      foreach (var bucket in range.Buckets()) {
        if (bucket < 0 || bucket > long.MaxValue / member.BucketSizeSectors) {
          diagnostics.Add($"journal bucket {bucket} overflows sector addressing.");
          continue;
        }

        var firstSector = checked(bucket * member.BucketSizeSectors);
        var firstByte = checked(firstSector * (long)SectorSize);
        if (firstByte < 0 || firstByte + bucketBytes > device.Length) {
          diagnostics.Add($"journal bucket {bucket} ({firstSector} sectors) lies outside device.");
          continue;
        }

        var bytes = new byte[bucketBytes];
        device.Position = firstByte;
        device.ReadExactly(bytes);
        ScanBucket(bytes, superblock, bucket, firstSector, blockBytes, replicas, diagnostics);
      }

    return new BcacheFsJournalDeviceLog(superblock.DeviceIndex, replicas, diagnostics);
  }

  private static void ScanBucket(
      byte[] bucketBytes,
      BcacheFsSuperblockRecord superblock,
      long bucket,
      long firstSector,
      int blockBytes,
      List<BcacheFsJournalReplica> replicas,
      List<string> diagnostics) {
    var cursor = 0;
    var sawChecksumFailure = false;
    ulong highestSequence = 0;

    while (cursor + BcacheFsJournalFormat.SetHeaderBytes <= bucketBytes.Length) {
      var span = bucketBytes.AsSpan(cursor);
      var magic = BinaryPrimitives.ReadUInt64LittleEndian(span[16..]);
      if (magic != BcacheFsJournalFormat.ExpectedMagic(superblock.FilesystemMagic)) {
        if (!sawChecksumFailure) break;
        cursor = checked(cursor + blockBytes);
        continue;
      }

      var sequence = BinaryPrimitives.ReadUInt64LittleEndian(span[24..]);
      if (highestSequence != 0 && sequence < highestSequence)
        break; // partially overwritten old tail: kernel stops here too
      highestSequence = Math.Max(highestSequence, sequence);

      var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[36..]);
      var rawChecksumType = (byte)(flags & 0xF);
      var payloadU64s = BinaryPrimitives.ReadUInt32LittleEndian(span[40..]);
      var setBytesLong = BcacheFsJournalFormat.SetHeaderBytes + (long)payloadU64s * sizeof(ulong);
      if (setBytesLong > int.MaxValue || setBytesLong > span.Length) {
        diagnostics.Add($"journal seq {sequence} in bucket {bucket} claims {setBytesLong} bytes past bucket end.");
        sawChecksumFailure = true;
        cursor = checked(cursor + blockBytes);
        continue;
      }

      var setBytes = (int)setBytesLong;
      var raw = span[..setBytes].ToArray();
      var knownChecksum = Enum.IsDefined(typeof(BcacheFsChecksumType), rawChecksumType)
        ? (BcacheFsChecksumType?)rawChecksumType
        : null;

      BcacheFsChecksumVerification verification;
      BcacheFsJournalSet? parsed = null;
      string parseDiagnostic = string.Empty;
      if (knownChecksum == null) {
        verification = new BcacheFsChecksumVerification(false, false,
          $"unknown checksum type {rawChecksumType}.");
      } else {
        verification = BcacheFsChecksumCodec.VerifyVstruct(knownChecksum.Value, raw);
        var encrypted = knownChecksum is BcacheFsChecksumType.ChaCha20Poly1305_80
          or BcacheFsChecksumType.ChaCha20Poly1305_128;
        if (!encrypted && !BcacheFsJournalFormat.TryParse(raw, superblock.FilesystemMagic, out parsed, out parseDiagnostic))
          parsed = null;
      }

      if (!verification.Valid && !verification.KeyRequired)
        sawChecksumFailure = true;

      replicas.Add(new BcacheFsJournalReplica(
        superblock.DeviceIndex,
        bucket,
        firstSector + cursor / SectorSize,
        cursor / SectorSize,
        sequence,
        rawChecksumType,
        verification,
        parsed,
        raw,
        parseDiagnostic));

      var aligned = RoundUp(setBytes, blockBytes);
      if (aligned <= 0) break;
      cursor = checked(cursor + aligned);
    }
  }

  private static int RoundUp(int value, int unit) {
    if (unit <= 0) throw new ArgumentOutOfRangeException(nameof(unit));
    var result = ((long)value + unit - 1) / unit * unit;
    return checked((int)result);
  }
}

internal sealed record BcacheFsJournalDeviceLog(
  byte DeviceIndex,
  IReadOnlyList<BcacheFsJournalReplica> Replicas,
  IReadOnlyList<string> Diagnostics) {

  internal BcacheFsJournalLog AsLog() => BcacheFsJournalLog.Merge([this]);
}

internal sealed record BcacheFsJournalReplica(
  byte DeviceIndex,
  long Bucket,
  long Sector,
  int BucketOffsetSectors,
  ulong Sequence,
  byte RawChecksumType,
  BcacheFsChecksumVerification Checksum,
  BcacheFsJournalSet? Parsed,
  byte[] RawBytes,
  string ParseDiagnostic) {

  internal bool IsEncrypted => this.Checksum.KeyRequired;
  internal bool Replayable => this.Checksum.Valid && this.Parsed != null;
}

/// <summary>Merged journal view across all available member devices.</summary>
internal sealed class BcacheFsJournalLog {
  internal required IReadOnlyList<BcacheFsJournalSequence> Sequences { get; init; }
  internal required ulong OldestRequiredSequence { get; init; }
  internal required IReadOnlyList<string> Diagnostics { get; init; }

  internal IEnumerable<BcacheFsJournalSequence> ReplayWindow()
    => this.Sequences.Where(s => s.Sequence >= this.OldestRequiredSequence);

  internal static BcacheFsJournalLog Merge(IEnumerable<BcacheFsJournalDeviceLog> devices) {
    ArgumentNullException.ThrowIfNull(devices);
    var all = devices.ToList();
    var diagnostics = all.SelectMany(d => d.Diagnostics).ToList();
    var sequences = new List<BcacheFsJournalSequence>();
    ulong oldestRequired = 0;

    foreach (var group in all.SelectMany(d => d.Replicas).GroupBy(r => r.Sequence).OrderBy(g => g.Key)) {
      var replicas = group.ToList();
      var replayable = replicas.Where(r => r.Replayable).ToList();
      BcacheFsJournalReplica? preferred = replayable.FirstOrDefault();

      if (replayable.Count > 1) {
        var canonical = replayable[0].RawBytes;
        if (replayable.Skip(1).Any(r => !r.RawBytes.AsSpan().SequenceEqual(canonical)))
          diagnostics.Add($"journal sequence {group.Key} has non-identical checksum-valid replicas.");
      }

      if (preferred?.Parsed is { } set && !set.Header.NoFlush)
        oldestRequired = Math.Max(oldestRequired, set.Header.LastSequence);

      sequences.Add(new BcacheFsJournalSequence(group.Key, replicas, preferred));
    }

    return new BcacheFsJournalLog {
      Sequences = sequences,
      OldestRequiredSequence = oldestRequired,
      Diagnostics = diagnostics,
    };
  }
}

internal sealed record BcacheFsJournalSequence(
  ulong Sequence,
  IReadOnlyList<BcacheFsJournalReplica> Replicas,
  BcacheFsJournalReplica? Preferred) {

  internal bool Replayable => this.Preferred?.Replayable == true;
}
