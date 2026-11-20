using System.Buffers.Binary;

namespace FileFormat.Sqx;

/// <summary>
/// An SQX recovery record (block type 0x58, <c>SQ4RD</c>): the protected data in 512-byte blocks
/// (the last zero-padded), one 16-bit CRC per block, and R parity blocks, parity k being the XOR of
/// every block whose index is k modulo R. A 49-byte header precedes them: CRC-16, type, flags,
/// size, <c>SQ4RD</c>, level (percent), block count, parity count, format, protected size, the raw
/// CRC-32 of the CRCs and parity, and (external records only) the raw CRC-32 of the protected data.
/// </summary>
public sealed record SqxRecoveryRecord(uint Level, long FileBlocks, long ParityBlocks, byte Version, long DataSize,
    uint RecoveryCrc, uint DataCrc, ushort[] BlockCrcs, byte[] Parity) {

  /// <summary>Bytes of one protected block and one parity block.</summary>
  public const int BlockSize = 512;

  /// <summary>Bytes of the record's header.</summary>
  public const int HeaderSize = 49;

  /// <summary>The record's whole length: header, CRCs and parity.</summary>
  public long Length => HeaderSize + 2 * this.FileBlocks + BlockSize * this.ParityBlocks;

  /// <summary>Reads a record at the start of <paramref name="data"/>; false when there is none, or it does not check out.</summary>
  public static bool TryRead(ReadOnlySpan<byte> data, out SqxRecoveryRecord record) {
    record = null!;
    if (data.Length < HeaderSize || data[2] != SqxContainer.BlockRecovery || !data.Slice(7, 5).SequenceEqual("SQ4RD"u8))
      return false;
    if (BinaryPrimitives.ReadUInt16LittleEndian(data[5..]) != HeaderSize
        || SqxContainer.BlockCrc(data[2..HeaderSize]) != BinaryPrimitives.ReadUInt16LittleEndian(data))
      return false;
    var level = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
    var fileBlocks = BinaryPrimitives.ReadInt64LittleEndian(data[16..]);
    var parityBlocks = BinaryPrimitives.ReadInt64LittleEndian(data[24..]);
    var size = BinaryPrimitives.ReadInt64LittleEndian(data[33..]);
    if (fileBlocks < 0 || parityBlocks < 1 || size < 0 || fileBlocks != (size + BlockSize - 1) / BlockSize
        || HeaderSize + 2 * fileBlocks + BlockSize * parityBlocks > data.Length)
      return false;
    var body = data.Slice(HeaderSize, (int)(2 * fileBlocks + BlockSize * parityBlocks));
    var recoveryCrc = BinaryPrimitives.ReadUInt32LittleEndian(data[41..]);
    if (SqxContainer.RawCrc32(body) != recoveryCrc)
      return false;
    var crcs = new ushort[fileBlocks];
    for (var i = 0; i < crcs.Length; ++i)
      crcs[i] = BinaryPrimitives.ReadUInt16LittleEndian(body[(2 * i)..]);
    record = new(level, fileBlocks, parityBlocks, data[32], size, recoveryCrc, BinaryPrimitives.ReadUInt32LittleEndian(data[45..]),
      crcs, body[(int)(2 * fileBlocks)..].ToArray());
    return true;
  }

  /// <summary>Serializes the record (an external one carries the data's CRC in <see cref="DataCrc"/>, an internal one 0xFFFFFFFF).</summary>
  public byte[] ToArray() {
    var output = new byte[this.Length];
    output[2] = SqxContainer.BlockRecovery;
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(5), HeaderSize);
    "SQ4RD"u8.CopyTo(output.AsSpan(7));
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), this.Level);
    BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(16), this.FileBlocks);
    BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(24), this.ParityBlocks);
    output[32] = this.Version;
    BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(33), this.DataSize);
    for (var i = 0; i < this.BlockCrcs.Length; ++i)
      BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(HeaderSize + 2 * i), this.BlockCrcs[i]);
    this.Parity.CopyTo(output, HeaderSize + 2 * this.FileBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(41), SqxContainer.RawCrc32(output.AsSpan(HeaderSize)));
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(45), this.DataCrc);
    BinaryPrimitives.WriteUInt16LittleEndian(output, SqxContainer.BlockCrc(output.AsSpan(2, HeaderSize - 2)));
    return output;
  }
}

/// <summary>What <see cref="SqxRecovery.Repair"/> found and did.</summary>
/// <param name="Damaged">Protected blocks whose CRC failed.</param>
/// <param name="Repaired">Whether every damaged block was rebuilt (or none was damaged).</param>
/// <param name="Message">A summary for the user.</param>
public sealed record SqxRecoveryReport(int Damaged, bool Repaired, string Message);

/// <summary>
/// SQX recovery records: creates the external one (<c>name.sqx.sqr</c>) for an archive and repairs
/// an archive from its internal record or an external one. A parity group (the blocks with the
/// same index modulo the parity count) can lose one block.
/// </summary>
public static class SqxRecovery {
  private const int Block = SqxRecoveryRecord.BlockSize;

  /// <summary>
  /// The external recovery record of <paramref name="archive"/> with <paramref name="level"/>
  /// percent of recovery data: ceil(blocks × level / 100) parity blocks, as Squeez writes them.
  /// </summary>
  public static byte[] CreateExternal(ReadOnlySpan<byte> archive, int level = 2) {
    if (level is < 1 or > 100)
      throw new ArgumentOutOfRangeException(nameof(level), "SQX: recovery data is 1 to 100 percent.");
    if (archive.IsEmpty)
      throw new ArgumentException("SQX: an empty archive has nothing to protect.", nameof(archive));
    return Build(archive, level, SqxContainer.RawCrc32(archive)).ToArray();
  }

  private static SqxRecoveryRecord Build(ReadOnlySpan<byte> data, int level, uint dataCrc) {
    long blocks = (data.Length + Block - 1) / Block;
    var parityCount = Math.Max(1, (blocks * level + 99) / 100);
    var crcs = new ushort[blocks];
    var parity = new byte[parityCount * Block];
    Span<byte> block = stackalloc byte[Block];
    for (var i = 0; i < blocks; ++i) {
      block.Clear();
      data.Slice(i * Block, (int)Math.Min(Block, data.Length - (long)i * Block)).CopyTo(block);
      crcs[i] = (ushort)SqxContainer.RawCrc32(block);
      var target = parity.AsSpan((int)(i % parityCount * Block), Block);
      for (var k = 0; k < Block; ++k) target[k] ^= block[k];
    }
    return new((uint)level, blocks, parityCount, SqxTables11, data.Length, 0, dataCrc, crcs, parity);
  }

  private const byte SqxTables11 = 11;

  /// <summary>
  /// Repairs <paramref name="archive"/> in place from <paramref name="externalRecord"/> or, when
  /// that is null, from the archive's own recovery record.
  /// </summary>
  /// <exception cref="InvalidDataException">No recovery record, or one that does not fit the archive.</exception>
  public static SqxRecoveryReport Repair(byte[] archive, byte[]? externalRecord = null) {
    ArgumentNullException.ThrowIfNull(archive);
    SqxRecoveryRecord record;
    if (externalRecord != null) {
      if (!SqxRecoveryRecord.TryRead(externalRecord, out record))
        throw new InvalidDataException("SQX: the external recovery record is damaged or not one.");
      if (record.DataSize != archive.Length)
        throw new InvalidDataException($"SQX: the recovery record protects {record.DataSize} bytes, the archive has {archive.Length}.");
    } else {
      var at = FindInternal(archive);
      if (at < 0 || !SqxRecoveryRecord.TryRead(archive.AsSpan(at), out record))
        throw new InvalidDataException("SQX: the archive has no intact recovery record.");
      if (record.DataSize != at)
        throw new InvalidDataException("SQX: the recovery record does not protect the data in front of it.");
    }

    var length = record.DataSize;
    var damaged = new List<long>();
    Span<byte> block = stackalloc byte[Block];
    for (var i = 0L; i < record.FileBlocks; ++i) {
      Read(archive, length, i, block);
      if ((ushort)SqxContainer.RawCrc32(block) != record.BlockCrcs[i])
        damaged.Add(i);
    }
    if (damaged.Count == 0)
      return new(0, true, "SQX: the archive is intact.");
    var groups = damaged.GroupBy(i => i % record.ParityBlocks).ToList();
    if (groups.Any(g => g.Count() > 1))
      return new(damaged.Count, false, $"SQX: {damaged.Count} damaged blocks, more than one in a parity group; not repaired.");

    foreach (var bad in damaged) {
      var rebuilt = record.Parity.AsSpan((int)(bad % record.ParityBlocks * Block), Block).ToArray();
      for (var i = bad % record.ParityBlocks; i < record.FileBlocks; i += record.ParityBlocks) {
        if (i == bad) continue;
        Read(archive, length, i, block);
        for (var k = 0; k < Block; ++k) rebuilt[k] ^= block[k];
      }
      if ((ushort)SqxContainer.RawCrc32(rebuilt) != record.BlockCrcs[bad])
        return new(damaged.Count, false, $"SQX: block {bad} does not rebuild to its CRC; not repaired.");
      var start = bad * Block;
      rebuilt.AsSpan(0, (int)Math.Min(Block, length - start)).CopyTo(archive.AsSpan((int)start));
    }
    return new(damaged.Count, true, $"SQX: {damaged.Count} damaged blocks rebuilt.");
  }

  private static void Read(byte[] archive, long length, long index, Span<byte> block) {
    block.Clear();
    var start = index * Block;
    archive.AsSpan((int)start, (int)Math.Min(Block, length - start)).CopyTo(block);
  }

  /// <summary>Offset of the last block that reads as an intact recovery record, or -1.</summary>
  private static int FindInternal(byte[] archive) {
    for (var at = archive.AsSpan().LastIndexOf("SQ4RD"u8); at >= 7; at = archive.AsSpan(0, at).LastIndexOf("SQ4RD"u8))
      if (SqxRecoveryRecord.TryRead(archive.AsSpan(at - 7), out _))
        return at - 7;
    return -1;
  }
}
