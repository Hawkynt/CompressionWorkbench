#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Mpq;

/// <summary>
/// Writes Blizzard MPQ v1 archives. WORM creation only; existing archives are
/// not modified in place. Files are stored uncompressed (no method negotiation,
/// no sector splitting). A "(listfile)" stream is auto-generated so file names
/// roundtrip through <see cref="MpqReader"/>.
/// </summary>
public sealed class MpqWriter {
  private const string ListfileName = "(listfile)";
  private const uint HeaderMagic = 0x1A51504D;     // "MPQ\x1A"
  private const uint FlagFileExists = 0x80000000;
  private const uint FlagEmptyHashSlot = 0xFFFFFFFF;
  // sector size shift = 3 -> 4096 byte sectors. Irrelevant for stored files but
  // stored verbatim in the header.
  private const ushort DefaultSectorSizeShift = 3;

  private readonly List<(string name, byte[] data)> _files = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (string.IsNullOrEmpty(name))
      throw new ArgumentException("Name must be non-empty.", nameof(name));
    if (string.Equals(name, ListfileName, StringComparison.Ordinal))
      throw new ArgumentException("'(listfile)' is reserved -- it is generated automatically.", nameof(name));
    _files.Add((name, data));
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    // Build the listfile. MPQ convention is for the listfile to include its own
    // entry so readers iterating the block table don't expose it as an anonymous
    // "File0000N" leftover.
    var allNames = new List<string>(_files.Count + 1);
    allNames.AddRange(_files.Select(f => f.name));
    allNames.Add(ListfileName);
    var listfileBytes = Encoding.UTF8.GetBytes(string.Join("\r\n", allNames));

    // All files (user + listfile) get a block table entry; only user files get
    // a hash slot for the user-visible name. The listfile gets a hash slot too
    // so the reader's TryReadListfile() can find it by name.
    var allFiles = new List<(string name, byte[] data)>(_files.Count + 1);
    allFiles.AddRange(_files);
    allFiles.Add((ListfileName, listfileBytes));

    // Choose hash table size: smallest power of 2 >= max(16, 2 * file count) so
    // collisions stay rare under linear probing.
    var hashTableEntries = NextPowerOfTwo(Math.Max(16, allFiles.Count * 2));

    // Compute layout: header(32) + file data + hash table + block table.
    const int headerSize = 32;
    var dataSize = 0L;
    var blockOffsets = new uint[allFiles.Count];
    for (var i = 0; i < allFiles.Count; i++) {
      blockOffsets[i] = checked((uint)(headerSize + dataSize));
      dataSize += allFiles[i].data.Length;
    }
    var hashTableOffset = checked((uint)(headerSize + dataSize));
    var blockTableOffset = checked(hashTableOffset + (uint)hashTableEntries * 16u);
    var archiveSize = checked(blockTableOffset + (uint)allFiles.Count * 16u);

    // Build hash table in memory.
    var hashTable = new byte[hashTableEntries * 16];
    for (var i = 0; i < hashTable.Length; i += 16) {
      // Empty slot sentinels -- per MS-MPQ: hashA = 0xFFFFFFFF and blockIndex = 0xFFFFFFFF
      // (StormLib also recognises blockIndex 0xFFFFFFFE as "deleted"). We use FREE.
      BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(i + 0), 0xFFFFFFFFu);
      BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(i + 4), 0xFFFFFFFFu);
      // locale (uint16) + platform (uint16) = 0
      BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(i + 12), FlagEmptyHashSlot);
    }

    // Insert each file into the hash table (linear probing on collision).
    for (var i = 0; i < allFiles.Count; i++) {
      var name = allFiles[i].name;
      var hashA = MpqCrypto.HashString(name, MpqCrypto.HashTypeNameA);
      var hashB = MpqCrypto.HashString(name, MpqCrypto.HashTypeNameB);
      var slot = MpqCrypto.HashString(name, MpqCrypto.HashTypeOffset) % (uint)hashTableEntries;

      var probed = 0u;
      while (probed < (uint)hashTableEntries) {
        var off = (int)slot * 16;
        // Empty when blockIndex == 0xFFFFFFFF.
        if (BinaryPrimitives.ReadUInt32LittleEndian(hashTable.AsSpan(off + 12)) == FlagEmptyHashSlot) {
          BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(off + 0), hashA);
          BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(off + 4), hashB);
          BinaryPrimitives.WriteUInt16LittleEndian(hashTable.AsSpan(off + 8), 0); // locale = neutral
          BinaryPrimitives.WriteUInt16LittleEndian(hashTable.AsSpan(off + 10), 0); // platform = default
          BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(off + 12), (uint)i);
          break;
        }
        slot = (slot + 1) % (uint)hashTableEntries;
        probed++;
      }
      if (probed >= (uint)hashTableEntries)
        throw new InvalidOperationException("MpqWriter: hash table full -- this shouldn't happen given the 2x sizing.");
    }
    MpqCrypto.EncryptBlock(hashTable, MpqCrypto.HashString("(hash table)", MpqCrypto.HashTypeFileKey));

    // Build block table.
    var blockTable = new byte[allFiles.Count * 16];
    for (var i = 0; i < allFiles.Count; i++) {
      var off = i * 16;
      BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(off + 0), blockOffsets[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(off + 4), (uint)allFiles[i].data.Length); // compressed = original (stored)
      BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(off + 8), (uint)allFiles[i].data.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(off + 12), FlagFileExists);
    }
    MpqCrypto.EncryptBlock(blockTable, MpqCrypto.HashString("(block table)", MpqCrypto.HashTypeFileKey));

    // Write header.
    Span<byte> hdr = stackalloc byte[headerSize];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[0..], HeaderMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], headerSize);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..], archiveSize);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[12..], 0); // format version v1
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[14..], DefaultSectorSizeShift);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[16..], hashTableOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[20..], blockTableOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[24..], (uint)hashTableEntries);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[28..], (uint)allFiles.Count);
    output.Write(hdr);

    // Write file data in block-table order.
    foreach (var (_, data) in allFiles)
      output.Write(data);

    // Write encrypted hash table, then encrypted block table.
    output.Write(hashTable);
    output.Write(blockTable);
  }

  private static int NextPowerOfTwo(int v) {
    if (v <= 1) return 1;
    var p = 1;
    while (p < v) p <<= 1;
    return p;
  }
}
