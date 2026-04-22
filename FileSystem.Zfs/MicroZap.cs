#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Zfs;

/// <summary>
/// Microzap block layout — a simple flat array-of-entries ZAP used when all entries fit in
/// a single block and values are 8 bytes. Per <c>include/sys/zap_leaf.h</c> /
/// <c>zap_impl.h</c>:
/// <code>
/// struct mzap_phys {
///   u64 mz_block_type;    // ZBT_MICRO = 0x8000000000000003
///   u64 mz_salt;
///   u64 mz_normflags;
///   u64 mz_pad[5];
///   mzap_ent_phys mz_chunk[...];
/// };
/// struct mzap_ent_phys {
///   u64 mze_value;
///   u32 mze_cd;
///   u16 mze_pad;
///   char mze_name[MZAP_NAME_LEN];  // 50 bytes, NUL-terminated
/// };
/// </code>
/// Header size: 64 bytes. Entry size: 64 bytes. So a 1024-byte block fits 15 entries.
/// </summary>
internal static class MicroZap {
  public const int HeaderSize = 64;
  public const int EntrySize = 64;
  public const int NameSize = 50;

  public static byte[] Encode(IEnumerable<(string Name, ulong Value)> entries, int blockSize = 1024) {
    var block = new byte[blockSize];
    var s = block.AsSpan();
    BinaryPrimitives.WriteUInt64LittleEndian(s[..8], ZfsConstants.ZbtMicro);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(8, 8), 0); // salt
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(16, 8), 0); // normflags
    // 5 * u64 padding — zeroed

    var off = HeaderSize;
    uint cd = 0;
    foreach (var (name, value) in entries) {
      if (off + EntrySize > blockSize)
        throw new InvalidOperationException("MicroZap overflow — use fatzap or larger block.");
      BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(off, 8), value);
      BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(off + 8, 4), cd);
      // pad @ off+12 (2 bytes)
      var nameBytes = Encoding.UTF8.GetBytes(name);
      if (nameBytes.Length >= NameSize)
        throw new InvalidOperationException($"MicroZap name too long ({name.Length} >= {NameSize}).");
      nameBytes.AsSpan().CopyTo(s.Slice(off + 14, nameBytes.Length));
      s[off + 14 + nameBytes.Length] = 0;
      off += EntrySize;
      cd++;
    }
    return block;
  }

  public static List<(string Name, ulong Value)> Decode(ReadOnlySpan<byte> block) {
    var result = new List<(string, ulong)>();
    if (block.Length < HeaderSize) return result;
    var bt = BinaryPrimitives.ReadUInt64LittleEndian(block[..8]);
    if (bt != ZfsConstants.ZbtMicro) return result; // not a microzap
    for (var off = HeaderSize; off + EntrySize <= block.Length; off += EntrySize) {
      // Name at off+14, NUL-terminated.
      var nameSpan = block.Slice(off + 14, NameSize);
      var nulIdx = nameSpan.IndexOf((byte)0);
      if (nulIdx <= 0) continue; // empty slot
      var name = Encoding.UTF8.GetString(nameSpan[..nulIdx]);
      var value = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(off, 8));
      result.Add((name, value));
    }
    return result;
  }
}
