using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Mpq;

/// <summary>
/// Random-access editor for MPQ v1 archives with the canonical trailing
/// hash-table + block-table layout. Existing file blocks keep their offsets and
/// bytes. Changed stored payloads replace the old tables, then the encrypted
/// tables are regenerated and the 32-byte header is patched.
/// </summary>
internal static class MpqInPlaceModifier {
  private const string ListfileName = "(listfile)";
  private const int HeaderSize = 32;
  private const int TableEntrySize = 16;
  private const int IoBufferSize = 64 * 1024;
  private const uint FileExists = 0x80000000;
  private const uint HashFree = 0xFFFFFFFF;
  private const uint HashDeleted = 0xFFFFFFFE;

  private readonly record struct Header(
    long HeaderOffset,
    uint ArchiveSize,
    uint HashTableOffset,
    uint BlockTableOffset,
    int HashTableEntries,
    int BlockTableEntries);

  private readonly record struct HashEntry(
    uint HashA,
    uint HashB,
    ushort Locale,
    ushort Platform,
    uint BlockIndex);

  private readonly record struct BlockEntry(
    uint FileOffset,
    uint CompressedSize,
    uint OriginalSize,
    uint Flags) {
    public bool Exists => (this.Flags & FileExists) != 0;
  }

  private sealed record PendingPayload(int BlockIndex, uint RelativeOffset, byte[] Data);
  private readonly record struct WipeRange(long Offset, long Length, int BlockIndex);

  /// <summary>
  /// Adds or replaces stored files with O(table bytes + changed payload bytes)
  /// archive I/O. Existing compressed/encrypted members are preserved verbatim.
  /// </summary>
  public static void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ValidateStream(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    var changes = new Dictionary<string, (string Name, byte[] Data)>(StringComparer.OrdinalIgnoreCase);
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName))
        continue;
      if (string.Equals(input.ArchiveName, ListfileName, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("'(listfile)' is reserved and regenerated automatically.", nameof(inputs));
      ValidateName(input.ArchiveName);
      changes[input.ArchiveName] = (input.ArchiveName, input.ReadContent());
    }
    if (changes.Count == 0)
      return;

    var state = ReadState(archive);
    var listNames = ReadListfile(archive, state.Header, state.Hashes, state.Blocks);
    var nameSet = new HashSet<string>(listNames, StringComparer.OrdinalIgnoreCase);
    foreach (var change in changes.Values) {
      if (nameSet.Add(change.Name))
        listNames.Add(change.Name);
    }
    NormalizeListfileTail(listNames);

    var hashes = state.Hashes.ToArray();
    var blocks = state.Blocks.ToList();
    var refs = BuildReferenceCounts(hashes, blocks.Count);
    var pending = new List<PendingPayload>(changes.Count + 1);
    var wipes = new List<WipeRange>();
    var wipedBlocks = new HashSet<int>();
    var cursor = (long)state.Header.HashTableOffset;

    foreach (var change in changes.Values) {
      var slot = FindHashSlot(hashes, change.Name);
      int blockIndex;
      if (slot >= 0) {
        var oldIndex = CheckedBlockIndex(hashes[slot].BlockIndex, blocks.Count, change.Name);
        if (refs[oldIndex] <= 0)
          throw new InvalidDataException($"MPQ hash entry '{change.Name}' references an unreferenced block.");

        if (refs[oldIndex] == 1) {
          blockIndex = oldIndex;
          ScheduleWipe(state.Header, state.Blocks[oldIndex], oldIndex, wipes, wipedBlocks);
        } else {
          --refs[oldIndex];
          blockIndex = AllocateBlock(blocks, refs);
          refs[blockIndex] = 1;
          hashes[slot] = hashes[slot] with { BlockIndex = (uint)blockIndex };
        }
      } else {
        slot = FindInsertionSlot(hashes, change.Name);
        if (slot < 0)
          throw new NotSupportedException("MPQ hash table has no free/deleted slot for an in-place addition.");
        blockIndex = AllocateBlock(blocks, refs);
        refs[blockIndex] = 1;
        hashes[slot] = new HashEntry(
          MpqCrypto.HashString(change.Name, MpqCrypto.HashTypeNameA),
          MpqCrypto.HashString(change.Name, MpqCrypto.HashTypeNameB),
          0,
          0,
          (uint)blockIndex);
      }

      var relativeOffset = CheckedUInt32(cursor, "MPQ file offset");
      pending.Add(new PendingPayload(blockIndex, relativeOffset, change.Data));
      blocks[blockIndex] = StoredBlock(relativeOffset, change.Data.Length);
      cursor = checked(cursor + change.Data.LongLength);
    }

    ReplaceListfile(state.Header, hashes, blocks, refs, listNames, pending, wipes, wipedBlocks, ref cursor);
    ValidateWipes(state.Header, state.Blocks, refs, wipedBlocks, wipes);
    Commit(archive, state.Header, hashes, blocks, pending, wipes, cursor);
  }

  /// <summary>
  /// Removes names by changing hash/block metadata, regenerating the listfile and
  /// wiping only blocks whose final hash-reference count reaches zero.
  /// </summary>
  public static void Remove(Stream archive, IReadOnlyCollection<string> entryNames) {
    ValidateStream(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Count == 0)
      return;

    var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var name in entryNames) {
      if (string.IsNullOrEmpty(name) || string.Equals(name, ListfileName, StringComparison.OrdinalIgnoreCase))
        continue;
      ValidateName(name);
      requested.Add(name);
    }
    if (requested.Count == 0)
      return;

    var state = ReadState(archive);
    var listNames = ReadListfile(archive, state.Header, state.Hashes, state.Blocks);
    var hashes = state.Hashes.ToArray();
    var blocks = state.Blocks.ToList();
    var refs = BuildReferenceCounts(hashes, blocks.Count);
    var wipes = new List<WipeRange>();
    var wipedBlocks = new HashSet<int>();
    var matchedAny = false;

    foreach (var name in requested) {
      var slot = FindHashSlot(hashes, name);
      if (slot < 0)
        continue;
      var blockIndex = CheckedBlockIndex(hashes[slot].BlockIndex, blocks.Count, name);
      if (refs[blockIndex] <= 0)
        throw new InvalidDataException($"MPQ hash entry '{name}' references an unreferenced block.");

      hashes[slot] = hashes[slot] with { BlockIndex = HashDeleted };
      --refs[blockIndex];
      if (refs[blockIndex] == 0) {
        ScheduleWipe(state.Header, state.Blocks[blockIndex], blockIndex, wipes, wipedBlocks);
        blocks[blockIndex] = default;
      }
      matchedAny = true;
    }

    if (!matchedAny)
      return;

    listNames.RemoveAll(name => requested.Contains(name));
    NormalizeListfileTail(listNames);

    var pending = new List<PendingPayload>(1);
    var cursor = (long)state.Header.HashTableOffset;
    ReplaceListfile(state.Header, hashes, blocks, refs, listNames, pending, wipes, wipedBlocks, ref cursor);
    ValidateWipes(state.Header, state.Blocks, refs, wipedBlocks, wipes);
    Commit(archive, state.Header, hashes, blocks, pending, wipes, cursor);
  }

  private sealed record State(Header Header, HashEntry[] Hashes, BlockEntry[] Blocks);

  private static State ReadState(Stream archive) {
    archive.Position = 0;
    Span<byte> prefix = stackalloc byte[12];
    archive.ReadExactly(prefix[..4]);
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(prefix[..4]);
    long headerOffset = 0;

    if (magic == MpqReader.UserDataMagic) {
      archive.Position = 0;
      archive.ReadExactly(prefix);
      headerOffset = BinaryPrimitives.ReadUInt32LittleEndian(prefix[8..12]);
    }

    archive.Position = headerOffset;
    Span<byte> rawHeader = stackalloc byte[HeaderSize];
    archive.ReadExactly(rawHeader);
    if (BinaryPrimitives.ReadUInt32LittleEndian(rawHeader[..4]) != MpqReader.HeaderMagic)
      throw new InvalidDataException("Stream does not contain an MPQ header at the declared offset.");

    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(rawHeader[4..8]);
    var archiveSize = BinaryPrimitives.ReadUInt32LittleEndian(rawHeader[8..12]);
    var version = BinaryPrimitives.ReadUInt16LittleEndian(rawHeader[12..14]);
    var hashOffset = BinaryPrimitives.ReadUInt32LittleEndian(rawHeader[16..20]);
    var blockOffset = BinaryPrimitives.ReadUInt32LittleEndian(rawHeader[20..24]);
    var hashCountRaw = BinaryPrimitives.ReadUInt32LittleEndian(rawHeader[24..28]);
    var blockCountRaw = BinaryPrimitives.ReadUInt32LittleEndian(rawHeader[28..32]);

    if (version != 0 || headerSize != HeaderSize)
      throw new NotSupportedException("Changed-byte MPQ editing currently supports the 32-byte MPQ v1 header only.");
    if (hashCountRaw == 0 || hashCountRaw > int.MaxValue || blockCountRaw > int.MaxValue)
      throw new NotSupportedException("MPQ table entry counts are outside the supported in-memory range.");

    var hashCount = (int)hashCountRaw;
    var blockCount = (int)blockCountRaw;
    var hashBytesLength = checked((long)hashCount * TableEntrySize);
    var blockBytesLength = checked((long)blockCount * TableEntrySize);
    if ((long)blockOffset != (long)hashOffset + hashBytesLength ||
        archiveSize != (long)blockOffset + blockBytesLength ||
        archive.Length != checked(headerOffset + archiveSize))
      throw new NotSupportedException(
        "Changed-byte MPQ editing requires hash and block tables to be contiguous at the physical end of the archive.");

    var hashBytes = new byte[checked(hashCount * TableEntrySize)];
    archive.Position = checked(headerOffset + hashOffset);
    archive.ReadExactly(hashBytes);
    MpqCrypto.DecryptBlock(hashBytes, MpqCrypto.HashString("(hash table)", MpqCrypto.HashTypeFileKey));

    var blockBytes = new byte[checked(blockCount * TableEntrySize)];
    archive.Position = checked(headerOffset + blockOffset);
    archive.ReadExactly(blockBytes);
    MpqCrypto.DecryptBlock(blockBytes, MpqCrypto.HashString("(block table)", MpqCrypto.HashTypeFileKey));

    var hashes = new HashEntry[hashCount];
    for (var i = 0; i < hashes.Length; ++i) {
      var span = hashBytes.AsSpan(i * TableEntrySize, TableEntrySize);
      hashes[i] = new HashEntry(
        BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]),
        BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]),
        BinaryPrimitives.ReadUInt16LittleEndian(span[8..10]),
        BinaryPrimitives.ReadUInt16LittleEndian(span[10..12]),
        BinaryPrimitives.ReadUInt32LittleEndian(span[12..16]));
    }

    var blocks = new BlockEntry[blockCount];
    for (var i = 0; i < blocks.Length; ++i) {
      var span = blockBytes.AsSpan(i * TableEntrySize, TableEntrySize);
      blocks[i] = new BlockEntry(
        BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]),
        BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]),
        BinaryPrimitives.ReadUInt32LittleEndian(span[8..12]),
        BinaryPrimitives.ReadUInt32LittleEndian(span[12..16]));
    }

    ValidateLiveBlockExtents(new Header(headerOffset, archiveSize, hashOffset, blockOffset, hashCount, blockCount), hashes, blocks);
    return new State(new Header(headerOffset, archiveSize, hashOffset, blockOffset, hashCount, blockCount), hashes, blocks);
  }

  private static List<string> ReadListfile(
      Stream archive,
      Header header,
      HashEntry[] hashes,
      BlockEntry[] blocks) {
    var slot = FindHashSlot(hashes, ListfileName);
    if (slot < 0)
      throw new NotSupportedException("Changed-byte MPQ editing requires a readable '(listfile)'.");
    var blockIndex = CheckedBlockIndex(hashes[slot].BlockIndex, blocks.Length, ListfileName);
    var block = blocks[blockIndex];
    if (!block.Exists)
      throw new InvalidDataException("MPQ '(listfile)' points at a non-live block.");

    byte[] data;
    try {
      archive.Position = 0;
      var reader = new MpqReader(archive);
      data = reader.Extract(new MpqEntry {
        FileName = ListfileName,
        OriginalSize = block.OriginalSize,
        CompressedSize = block.CompressedSize,
        Flags = block.Flags,
        FileOffset = block.FileOffset,
      });
    } catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or EndOfStreamException) {
      throw new NotSupportedException("Changed-byte MPQ editing requires a decodable '(listfile)'.", ex);
    }

    var names = Encoding.UTF8.GetString(data)
      .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
      .ToList();
    return names;
  }

  private static void ReplaceListfile(
      Header header,
      HashEntry[] hashes,
      List<BlockEntry> blocks,
      List<int> refs,
      List<string> listNames,
      List<PendingPayload> pending,
      List<WipeRange> wipes,
      HashSet<int> wipedBlocks,
      ref long cursor) {
    var slot = FindHashSlot(hashes, ListfileName);
    if (slot < 0)
      throw new NotSupportedException("Changed-byte MPQ editing requires an existing '(listfile)' hash entry.");
    var oldIndex = CheckedBlockIndex(hashes[slot].BlockIndex, blocks.Count, ListfileName);
    if (refs[oldIndex] <= 0)
      throw new InvalidDataException("MPQ '(listfile)' references an unreferenced block.");

    int blockIndex;
    if (refs[oldIndex] == 1) {
      blockIndex = oldIndex;
      ScheduleWipe(header, blocks[oldIndex], oldIndex, wipes, wipedBlocks);
    } else {
      --refs[oldIndex];
      blockIndex = AllocateBlock(blocks, refs);
      refs[blockIndex] = 1;
      hashes[slot] = hashes[slot] with { BlockIndex = (uint)blockIndex };
    }

    var bytes = Encoding.UTF8.GetBytes(string.Join("\r\n", listNames));
    var relativeOffset = CheckedUInt32(cursor, "MPQ listfile offset");
    pending.Add(new PendingPayload(blockIndex, relativeOffset, bytes));
    blocks[blockIndex] = StoredBlock(relativeOffset, bytes.Length);
    cursor = checked(cursor + bytes.LongLength);
  }

  private static void NormalizeListfileTail(List<string> names) {
    names.RemoveAll(name => string.Equals(name, ListfileName, StringComparison.OrdinalIgnoreCase));
    names.Add(ListfileName);
  }

  private static List<int> BuildReferenceCounts(HashEntry[] hashes, int blockCount) {
    var refs = Enumerable.Repeat(0, blockCount).ToList();
    foreach (var hash in hashes) {
      if (hash.BlockIndex is HashFree or HashDeleted)
        continue;
      if (hash.BlockIndex >= (uint)blockCount)
        throw new InvalidDataException($"MPQ hash table references block {hash.BlockIndex} beyond the block table.");
      ++refs[(int)hash.BlockIndex];
    }
    return refs;
  }

  private static int FindHashSlot(HashEntry[] hashes, string name) {
    if (hashes.Length == 0)
      return -1;
    var hashA = MpqCrypto.HashString(name, MpqCrypto.HashTypeNameA);
    var hashB = MpqCrypto.HashString(name, MpqCrypto.HashTypeNameB);
    var start = (int)(MpqCrypto.HashString(name, MpqCrypto.HashTypeOffset) % (uint)hashes.Length);
    for (var i = start; ; i = (i + 1) % hashes.Length) {
      var entry = hashes[i];
      if (entry.BlockIndex == HashFree)
        return -1;
      if (entry.BlockIndex != HashDeleted && entry.HashA == hashA && entry.HashB == hashB)
        return i;
      if ((i + 1) % hashes.Length == start)
        return -1;
    }
  }

  private static int FindInsertionSlot(HashEntry[] hashes, string name) {
    var start = (int)(MpqCrypto.HashString(name, MpqCrypto.HashTypeOffset) % (uint)hashes.Length);
    var deleted = -1;
    for (var i = start; ; i = (i + 1) % hashes.Length) {
      if (hashes[i].BlockIndex == HashDeleted && deleted < 0)
        deleted = i;
      if (hashes[i].BlockIndex == HashFree)
        return deleted >= 0 ? deleted : i;
      if ((i + 1) % hashes.Length == start)
        return deleted;
    }
  }

  private static int CheckedBlockIndex(uint blockIndex, int blockCount, string name) {
    if (blockIndex >= (uint)blockCount)
      throw new InvalidDataException($"MPQ hash entry '{name}' references invalid block {blockIndex}.");
    return (int)blockIndex;
  }

  private static int AllocateBlock(List<BlockEntry> blocks, List<int> refs) {
    for (var i = 0; i < blocks.Count; ++i) {
      if (!blocks[i].Exists && refs[i] == 0)
        return i;
    }
    blocks.Add(default);
    refs.Add(0);
    return blocks.Count - 1;
  }

  private static BlockEntry StoredBlock(uint offset, int length) {
    var size = checked((uint)length);
    return new BlockEntry(offset, size, size, FileExists);
  }

  private static void ScheduleWipe(
      Header header,
      BlockEntry oldBlock,
      int blockIndex,
      List<WipeRange> wipes,
      HashSet<int> wipedBlocks) {
    if (!oldBlock.Exists || oldBlock.CompressedSize == 0 || !wipedBlocks.Add(blockIndex))
      return;
    wipes.Add(new WipeRange(
      checked(header.HeaderOffset + oldBlock.FileOffset),
      oldBlock.CompressedSize,
      blockIndex));
  }

  private static void ValidateWipes(
      Header header,
      BlockEntry[] originalBlocks,
      List<int> finalRefs,
      HashSet<int> wipedBlocks,
      List<WipeRange> wipes) {
    foreach (var wipe in wipes) {
      for (var i = 0; i < originalBlocks.Length; ++i) {
        if (i == wipe.BlockIndex || wipedBlocks.Contains(i) || i >= finalRefs.Count || finalRefs[i] == 0)
          continue;
        var survivor = originalBlocks[i];
        if (!survivor.Exists || survivor.CompressedSize == 0)
          continue;
        var start = checked(header.HeaderOffset + survivor.FileOffset);
        var end = checked(start + survivor.CompressedSize);
        if (wipe.Offset < end && start < wipe.Offset + wipe.Length)
          throw new NotSupportedException(
            $"MPQ block {wipe.BlockIndex} overlaps surviving block {i}; destructive wiping is unsafe.");
      }
    }
  }

  private static void ValidateLiveBlockExtents(Header header, HashEntry[] hashes, BlockEntry[] blocks) {
    var referenced = BuildReferenceCounts(hashes, blocks.Length);
    for (var i = 0; i < blocks.Length; ++i) {
      if (referenced[i] == 0 || !blocks[i].Exists)
        continue;
      var block = blocks[i];
      var end = checked((long)block.FileOffset + block.CompressedSize);
      if (block.FileOffset < HeaderSize || end > header.HashTableOffset)
        throw new NotSupportedException(
          "Changed-byte MPQ editing requires every referenced file block to lie wholly before the trailing tables.");
    }
  }

  private static void Commit(
      Stream archive,
      Header header,
      HashEntry[] hashes,
      List<BlockEntry> blocks,
      List<PendingPayload> pending,
      List<WipeRange> wipes,
      long payloadEnd) {
    var hashOffset = CheckedUInt32(payloadEnd, "MPQ hash-table offset");
    var hashBytes = SerializeHashes(hashes);
    var blockOffsetLong = checked(payloadEnd + hashBytes.LongLength);
    var blockOffset = CheckedUInt32(blockOffsetLong, "MPQ block-table offset");
    var blockBytes = SerializeBlocks(blocks);
    var archiveSizeLong = checked(blockOffsetLong + blockBytes.LongLength);
    var archiveSize = CheckedUInt32(archiveSizeLong, "MPQ archive size");

    MpqCrypto.EncryptBlock(hashBytes, MpqCrypto.HashString("(hash table)", MpqCrypto.HashTypeFileKey));
    MpqCrypto.EncryptBlock(blockBytes, MpqCrypto.HashString("(block table)", MpqCrypto.HashTypeFileKey));

    archive.Position = checked(header.HeaderOffset + header.HashTableOffset);
    foreach (var payload in pending) {
      var expected = checked(header.HeaderOffset + payload.RelativeOffset);
      if (archive.Position != expected)
        throw new InvalidDataException("MPQ pending payload layout is not contiguous.");
      archive.Write(payload.Data);
    }
    archive.Write(hashBytes);
    archive.Write(blockBytes);

    Span<byte> value = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(value, archiveSize);
    archive.Position = header.HeaderOffset + 8;
    archive.Write(value);
    BinaryPrimitives.WriteUInt32LittleEndian(value, hashOffset);
    archive.Position = header.HeaderOffset + 16;
    archive.Write(value);
    BinaryPrimitives.WriteUInt32LittleEndian(value, blockOffset);
    archive.Position = header.HeaderOffset + 20;
    archive.Write(value);
    BinaryPrimitives.WriteUInt32LittleEndian(value, checked((uint)blocks.Count));
    archive.Position = header.HeaderOffset + 28;
    archive.Write(value);

    archive.SetLength(checked(header.HeaderOffset + archiveSize));
    foreach (var wipe in wipes)
      ZeroRange(archive, wipe.Offset, wipe.Length);
    archive.Flush();
  }

  private static byte[] SerializeHashes(HashEntry[] hashes) {
    var bytes = new byte[checked(hashes.Length * TableEntrySize)];
    for (var i = 0; i < hashes.Length; ++i) {
      var span = bytes.AsSpan(i * TableEntrySize, TableEntrySize);
      BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], hashes[i].HashA);
      BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], hashes[i].HashB);
      BinaryPrimitives.WriteUInt16LittleEndian(span[8..10], hashes[i].Locale);
      BinaryPrimitives.WriteUInt16LittleEndian(span[10..12], hashes[i].Platform);
      BinaryPrimitives.WriteUInt32LittleEndian(span[12..16], hashes[i].BlockIndex);
    }
    return bytes;
  }

  private static byte[] SerializeBlocks(List<BlockEntry> blocks) {
    var bytes = new byte[checked(blocks.Count * TableEntrySize)];
    for (var i = 0; i < blocks.Count; ++i) {
      var span = bytes.AsSpan(i * TableEntrySize, TableEntrySize);
      BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], blocks[i].FileOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], blocks[i].CompressedSize);
      BinaryPrimitives.WriteUInt32LittleEndian(span[8..12], blocks[i].OriginalSize);
      BinaryPrimitives.WriteUInt32LittleEndian(span[12..16], blocks[i].Flags);
    }
    return bytes;
  }

  private static uint CheckedUInt32(long value, string what) {
    if (value < 0 || value > uint.MaxValue)
      throw new NotSupportedException($"{what} exceeds the MPQ v1 32-bit limit.");
    return (uint)value;
  }

  private static void ZeroRange(Stream archive, long offset, long length) {
    if (length <= 0)
      return;
    var zeroes = new byte[IoBufferSize];
    archive.Position = offset;
    var remaining = length;
    while (remaining > 0) {
      var count = (int)Math.Min(zeroes.Length, remaining);
      archive.Write(zeroes, 0, count);
      remaining -= count;
    }
  }

  private static void ValidateName(string name) {
    foreach (var ch in name)
      if (ch > 0xFF)
        throw new ArgumentException("MPQ v1 hashing supports byte-range file names only.", nameof(name));
  }

  private static void ValidateStream(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new NotSupportedException("Changed-byte MPQ editing requires a readable, writable, seekable stream.");
  }
}
