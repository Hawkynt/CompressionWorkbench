#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Nilfs2;

/// <summary>
/// Byte-accurate NILFS2 superblock encoder/decoder, reverse-engineered against
/// <c>mkfs.nilfs2</c> (nilfs-utils 2.2.9) output and cross-checked field-by-field.
/// </summary>
/// <remarks>
/// <para>The on-disk superblock is a 1024-byte structure that lives at file
/// offset 1024 (primary) and again at <c>dev_size - 4096</c> (secondary). Only
/// the first <c>s_bytes</c> (= 280) bytes are covered by the checksum; the
/// remainder is padding to the block boundary.</para>
///
/// <para><b>Checksum.</b> <c>s_sum</c> is Linux <c>crc32_le</c> — reflected IEEE
/// polynomial 0xEDB88320, <b>no</b> initial/final inversion — seeded with
/// <c>s_crc_seed</c>, computed over the first <c>s_bytes</c> bytes with the
/// 4-byte <c>s_sum</c> field (offset 0x10) zeroed. Verified to reproduce the
/// exact value mkfs.nilfs2 writes.</para>
///
/// <para><b>Field map (little-endian).</b></para>
/// <list type="bullet">
///   <item><description>0x00 u32 s_rev_level (= 2 for NILFS2)</description></item>
///   <item><description>0x04 u16 s_minor_rev_level</description></item>
///   <item><description>0x06 u16 s_magic (= 0x3434)</description></item>
///   <item><description>0x08 u16 s_bytes (checksum length, = 280)</description></item>
///   <item><description>0x0A u16 s_flags</description></item>
///   <item><description>0x0C u32 s_crc_seed</description></item>
///   <item><description>0x10 u32 s_sum (crc32_le over [0,s_bytes) w/ this field zeroed)</description></item>
///   <item><description>0x14 u32 s_log_block_size (block_size = 1024 &lt;&lt; this)</description></item>
///   <item><description>0x18 u64 s_nsegments</description></item>
///   <item><description>0x20 u64 s_dev_size</description></item>
///   <item><description>0x28 u64 s_first_data_block</description></item>
///   <item><description>0x30 u32 s_blocks_per_segment</description></item>
///   <item><description>0x34 u32 s_r_segments_percentage</description></item>
///   <item><description>0x38 u64 s_last_cno</description></item>
///   <item><description>0x40 u64 s_last_pseg</description></item>
///   <item><description>0x48 u64 s_last_seq</description></item>
///   <item><description>0x50 u64 s_free_blocks_count</description></item>
///   <item><description>0x58 u64 s_ctime / 0x60 s_mtime / 0x68 s_wtime</description></item>
///   <item><description>0x70 u16 s_mnt_count / 0x72 s_max_mnt_count</description></item>
///   <item><description>0x74 u16 s_state / 0x76 s_errors</description></item>
///   <item><description>0x78 u64 s_lastcheck</description></item>
///   <item><description>0x80 u32 s_checkinterval / 0x84 s_creator_os</description></item>
///   <item><description>0x88 u16 s_def_resuid / 0x8A s_def_resgid</description></item>
///   <item><description>0x8C u32 s_first_ino (= 11)</description></item>
///   <item><description>0x90 u16 s_inode_size (= 128)</description></item>
///   <item><description>0x92 u16 s_dat_entry_size (= 32)</description></item>
///   <item><description>0x94 u16 s_checkpoint_size (= 192)</description></item>
///   <item><description>0x96 u16 s_segment_usage_size (= 16)</description></item>
///   <item><description>0x98 16b s_uuid</description></item>
///   <item><description>0xA8 80b s_volume_name (NUL-padded)</description></item>
/// </list>
/// </remarks>
public static class Nilfs2Superblock {

  public const ushort Magic = 0x3434;
  public const ushort SBytes = 280;
  public const int Size = 1024;
  public const int PrimaryOffset = 1024;
  /// <summary>The secondary superblock sits one block (4096 B) before EOF.</summary>
  public const int SecondaryBackOffset = 4096;

  public const ushort StateValid = 0x0001;        // NILFS_VALID_FS
  public const ushort ErrorsContinue = 0x0001;    // NILFS_ERRORS_CONTINUE

  /// <summary>
  /// Linux <c>crc32_le</c>: reflected IEEE polynomial, no input/output inversion.
  /// The <paramref name="seed"/> is the literal LFSR init (the NILFS s_crc_seed),
  /// not the usual 0xFFFFFFFF.
  /// </summary>
  public static uint Crc32Le(uint seed, ReadOnlySpan<byte> data) {
    var crc = seed;
    foreach (var b in data) {
      crc ^= b;
      for (var k = 0; k < 8; ++k)
        crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
    }
    return crc;
  }

  /// <summary>
  /// Writes the s_sum checksum into <paramref name="sb"/> (a span starting at the
  /// superblock) using <paramref name="seed"/> over the first <see cref="SBytes"/>
  /// bytes with the s_sum field zeroed first.
  /// </summary>
  public static void FinalizeChecksum(Span<byte> sb, uint seed) {
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x10..], 0); // zero s_sum before hashing
    var sum = Crc32Le(seed, sb[..SBytes]);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x10..], sum);
  }

  /// <summary>
  /// Verifies the stored s_sum against a freshly computed crc32_le over the first
  /// <see cref="SBytes"/> bytes with the s_sum field treated as zero.
  /// </summary>
  public static bool VerifyChecksum(ReadOnlySpan<byte> sb) {
    if (sb.Length < SBytes) return false;
    var stored = BinaryPrimitives.ReadUInt32LittleEndian(sb[0x10..]);
    var seed = BinaryPrimitives.ReadUInt32LittleEndian(sb[0x0C..]);
    Span<byte> tmp = stackalloc byte[SBytes];
    sb[..SBytes].CopyTo(tmp);
    BinaryPrimitives.WriteUInt32LittleEndian(tmp[0x10..], 0);
    return Crc32Le(seed, tmp) == stored;
  }

  /// <summary>
  /// Encodes a complete 1024-byte superblock into <paramref name="dest"/> and seals
  /// it with a valid checksum.
  /// </summary>
  public static void Encode(
      Span<byte> dest,
      uint logBlockSize,
      ulong nSegments,
      ulong devSize,
      uint blocksPerSegment,
      ulong lastCno,
      ulong lastPseg,
      ulong lastSeq,
      ulong freeBlocks,
      ulong ctime,
      ushort state,
      uint crcSeed,
      ReadOnlySpan<byte> uuid,
      string? volumeLabel) {
    dest[..Size].Clear();

    BinaryPrimitives.WriteUInt32LittleEndian(dest, 2u);                       // s_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x04..], 0);                // s_minor_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x06..], Magic);           // s_magic
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x08..], SBytes);          // s_bytes
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x0A..], 0);               // s_flags
    BinaryPrimitives.WriteUInt32LittleEndian(dest[0x0C..], crcSeed);         // s_crc_seed
    // 0x10 s_sum filled by FinalizeChecksum
    BinaryPrimitives.WriteUInt32LittleEndian(dest[0x14..], logBlockSize);    // s_log_block_size
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x18..], nSegments);       // s_nsegments
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x20..], devSize);         // s_dev_size
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x28..], 1ul);            // s_first_data_block
    BinaryPrimitives.WriteUInt32LittleEndian(dest[0x30..], blocksPerSegment);// s_blocks_per_segment
    BinaryPrimitives.WriteUInt32LittleEndian(dest[0x34..], 5u);             // s_r_segments_percentage
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x38..], lastCno);         // s_last_cno
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x40..], lastPseg);        // s_last_pseg
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x48..], lastSeq);         // s_last_seq
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x50..], freeBlocks);      // s_free_blocks_count
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x58..], ctime);           // s_ctime
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x60..], ctime);           // s_mtime
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x68..], ctime);           // s_wtime
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x70..], 0);               // s_mnt_count
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x72..], 50);              // s_max_mnt_count
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x74..], state);           // s_state
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x76..], ErrorsContinue);  // s_errors
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x78..], ctime);           // s_lastcheck
    BinaryPrimitives.WriteUInt32LittleEndian(dest[0x80..], 15552000u);       // s_checkinterval (180d)
    BinaryPrimitives.WriteUInt32LittleEndian(dest[0x84..], 0u);              // s_creator_os (Linux)
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x88..], 0);               // s_def_resuid
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x8A..], 0);               // s_def_resgid
    BinaryPrimitives.WriteUInt32LittleEndian(dest[0x8C..], 11u);             // s_first_ino
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x90..], 128);             // s_inode_size
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x92..], 32);              // s_dat_entry_size
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x94..], 192);            // s_checkpoint_size
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x96..], 16);             // s_segment_usage_size

    if (!uuid.IsEmpty)
      uuid[..Math.Min(16, uuid.Length)].CopyTo(dest[0x98..]);

    if (!string.IsNullOrEmpty(volumeLabel)) {
      var lbl = Encoding.ASCII.GetBytes(volumeLabel);
      lbl.AsSpan(0, Math.Min(80, lbl.Length)).CopyTo(dest[0xA8..]);
    }

    FinalizeChecksum(dest, crcSeed);
  }
}
