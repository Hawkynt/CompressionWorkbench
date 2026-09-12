using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ps1MemoryCard;

internal static class Ps1MemoryCardImage {
  internal const int FrameSize = 128;
  internal const int BlockSize = 8 * 1024;
  internal const int CardSize = 128 * 1024;
  internal const int DataBlocksPerBank = 15;
  internal const int MaxBanks = 64;

  internal const uint LiveFirst = 0x00000051;
  internal const uint LiveMiddle = 0x00000052;
  internal const uint LiveLast = 0x00000053;
  internal const uint FreeFresh = 0x000000A0;
  internal const uint FreeDeletedFirst = 0x000000A1;
  internal const uint FreeDeletedMiddle = 0x000000A2;
  internal const uint FreeDeletedLast = 0x000000A3;

  internal sealed record Save(
    int BankIndex,
    string Name,
    int DeclaredSize,
    byte[] Data,
    IReadOnlyList<int> Blocks);

  internal sealed record Parsed(byte[] Bytes, int BankCount, IReadOnlyList<Save> Saves);

  internal static Parsed Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    var image = copy.ToArray();

    if (image.Length == 0 || image.Length % CardSize != 0)
      throw new InvalidDataException(
        $"PS1 memory-card image size must be a non-zero multiple of {CardSize} bytes; got {image.Length}.");

    var bankCount = image.Length / CardSize;
    if (bankCount is < 1 or > MaxBanks)
      throw new InvalidDataException($"Unsupported PS1 memory-card bank count {bankCount}; supported range is 1..{MaxBanks}.");

    var saves = new List<Save>();
    for (var bank = 0; bank < bankCount; ++bank)
      ReadBank(image, bank, saves);

    return new Parsed(image, bankCount, saves);
  }

  internal static byte[] Build(int bankCount, IEnumerable<Save> saves) {
    ArgumentOutOfRangeException.ThrowIfLessThan(bankCount, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(bankCount, MaxBanks);
    ArgumentNullException.ThrowIfNull(saves);

    var image = new byte[checked(bankCount * CardSize)];
    Array.Fill(image, (byte)0xFF);
    for (var bank = 0; bank < bankCount; ++bank)
      FormatFreshBank(image, bank);

    foreach (var bankGroup in saves.GroupBy(s => s.BankIndex).OrderBy(g => g.Key)) {
      if (bankGroup.Key < 0 || bankGroup.Key >= bankCount)
        throw new InvalidDataException($"Save targets bank {bankGroup.Key + 1}, outside a {bankCount}-bank card.");

      var nextSlot = 0;
      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var save in bankGroup) {
        ValidateRawName(save.Name);
        if (!names.Add(save.Name))
          throw new InvalidDataException($"Duplicate PS1 save name '{save.Name}' in bank {bankGroup.Key + 1}.");
        if (save.DeclaredSize <= 0)
          throw new InvalidDataException($"PS1 save '{save.Name}' has invalid size {save.DeclaredSize}.");
        if (save.Data.Length > save.DeclaredSize)
          throw new InvalidDataException($"PS1 save '{save.Name}' contains more bytes than its declared size.");

        var blockCount = BlocksRequired(save.DeclaredSize);
        if (blockCount > DataBlocksPerBank)
          throw new InvalidDataException($"PS1 save '{save.Name}' needs {blockCount} blocks; one save can use at most 15.");
        if (nextSlot + blockCount > DataBlocksPerBank)
          throw new InvalidDataException($"Bank {bankGroup.Key + 1} does not have {blockCount} free contiguous blocks for '{save.Name}'.");

        var slots = Enumerable.Range(nextSlot, blockCount).ToArray();
        WriteSave(image, bankGroup.Key, save, slots);
        nextSlot += blockCount;
      }
    }

    return image;
  }

  internal static int BlocksRequired(int byteLength)
    => Math.Max(1, checked((byteLength + BlockSize - 1) / BlockSize));

  internal static int RoundStoredSize(int byteLength) {
    if (byteLength < 0) throw new ArgumentOutOfRangeException(nameof(byteLength));
    var blocks = Math.Max(1, checked((byteLength + BlockSize - 1) / BlockSize));
    if (blocks > DataBlocksPerBank)
      throw new InvalidDataException($"A PS1 save may occupy at most {DataBlocksPerBank} blocks ({DataBlocksPerBank * BlockSize} bytes).");
    return checked(blocks * BlockSize);
  }

  internal static string ArchiveName(Save save, int bankCount) {
    var escaped = EscapeName(save.Name);
    return bankCount == 1 ? escaped : $"bank{save.BankIndex + 1:D2}/{escaped}";
  }

  internal static (int? BankIndex, string Name) ParseArchiveName(string archiveName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(archiveName);
    var normalized = archiveName.Replace('\\', '/').TrimStart('/');
    if (normalized.StartsWith("bank", StringComparison.OrdinalIgnoreCase)) {
      var slash = normalized.IndexOf('/');
      if (slash > 4 && int.TryParse(normalized.AsSpan(4, slash - 4), out var bankNumber) && bankNumber > 0)
        return (bankNumber - 1, UnescapeName(normalized[(slash + 1)..]));
    }

    if (normalized.Contains('/'))
      throw new InvalidDataException(
        $"PS1 memory cards do not support directories; '{archiveName}' is neither a file nor a synthetic bankNN/file path.");
    return (null, UnescapeName(normalized));
  }

  internal static void Rewrite(Stream stream, byte[] image) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(image);
    if (!stream.CanWrite || !stream.CanSeek)
      throw new ArgumentException("PS1 memory-card mutation requires a writable, seekable stream.", nameof(stream));
    stream.Position = 0;
    stream.SetLength(0);
    stream.Write(image);
    stream.Flush();
    stream.Position = 0;
  }

  internal static long Wipe(Stream stream, bool wipeClusterTips, bool wipeDeletedEntries) {
    var parsed = Read(stream);
    var image = parsed.Bytes;
    long wiped = 0;

    for (var bank = 0; bank < parsed.BankCount; ++bank) {
      var bankSaves = parsed.Saves.Where(s => s.BankIndex == bank).ToArray();
      var liveSlots = bankSaves.SelectMany(s => s.Blocks).ToHashSet();

      foreach (var slot in Enumerable.Range(0, DataBlocksPerBank)) {
        if (liveSlots.Contains(slot)) continue;
        var offset = BankBase(bank) + (slot + 1) * BlockSize;
        image.AsSpan(offset, BlockSize).Fill(0xFF);
        wiped += BlockSize;
      }

      if (wipeClusterTips) {
        foreach (var save in bankSaves) {
          var remainder = save.DeclaredSize % BlockSize;
          if (remainder == 0) continue;
          var finalSlot = save.Blocks[^1];
          var offset = BankBase(bank) + (finalSlot + 1) * BlockSize + remainder;
          var length = BlockSize - remainder;
          image.AsSpan(offset, length).Fill(0xFF);
          wiped += length;
        }
      }

      if (!wipeDeletedEntries) continue;
      for (var slot = 0; slot < DataBlocksPerBank; ++slot) {
        var frame = DirectoryFrame(image, bank, slot);
        var state = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        if (state is not (FreeDeletedFirst or FreeDeletedMiddle or FreeDeletedLast)) continue;
        frame.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(frame, FreeFresh);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[8..], 0xFFFF);
        StampChecksum(frame);
        wiped += FrameSize;
      }
    }

    Rewrite(stream, image);
    return wiped;
  }

  internal static IEnumerable<(long Offset, long Length, bool Used, string? Name)> EnumeratePhysicalExtents(Parsed parsed) {
    for (var bank = 0; bank < parsed.BankCount; ++bank) {
      var bankBase = BankBase(bank);
      yield return (bankBase, BlockSize, true, $"bank{bank + 1:D2}: metadata");

      var owner = new Dictionary<int, string>();
      foreach (var save in parsed.Saves.Where(s => s.BankIndex == bank)) {
        var archiveName = ArchiveName(save, parsed.BankCount);
        foreach (var slot in save.Blocks)
          owner[slot] = archiveName;
      }

      for (var slot = 0; slot < DataBlocksPerBank; ++slot) {
        var offset = bankBase + (slot + 1) * BlockSize;
        if (owner.TryGetValue(slot, out var name))
          yield return (offset, BlockSize, true, name);
        else
          yield return (offset, BlockSize, false, null);
      }
    }
  }

  internal static void ValidateAllMetadataChecksums(Parsed parsed) {
    for (var bank = 0; bank < parsed.BankCount; ++bank) {
      ValidateFrameChecksum(parsed.Bytes.AsSpan(BankBase(bank), FrameSize), bank, 0);
      for (var frame = 1; frame <= 35; ++frame)
        ValidateFrameChecksum(parsed.Bytes.AsSpan(BankBase(bank) + frame * FrameSize, FrameSize), bank, frame);
      ValidateFrameChecksum(parsed.Bytes.AsSpan(BankBase(bank) + 63 * FrameSize, FrameSize), bank, 63);
    }
  }

  private static void ReadBank(byte[] image, int bank, List<Save> saves) {
    var bankBase = BankBase(bank);
    var header = image.AsSpan(bankBase, FrameSize);
    if (header[0] != (byte)'M' || header[1] != (byte)'C')
      throw new InvalidDataException($"Bank {bank + 1} does not begin with the PS1 memory-card 'MC' header.");
    ValidateFrameChecksum(header, bank, 0);

    var states = new uint[DataBlocksPerBank];
    for (var slot = 0; slot < DataBlocksPerBank; ++slot) {
      var frame = DirectoryFrame(image, bank, slot);
      ValidateFrameChecksum(frame, bank, slot + 1);
      states[slot] = BinaryPrimitives.ReadUInt32LittleEndian(frame);
      if (!IsKnownState(states[slot]))
        throw new InvalidDataException($"Bank {bank + 1}, directory slot {slot + 1} has unknown allocation state 0x{states[slot]:X8}.");
    }

    var claimed = new bool[DataBlocksPerBank];
    for (var slot = 0; slot < DataBlocksPerBank; ++slot) {
      if (states[slot] != LiveFirst) continue;
      var first = DirectoryFrame(image, bank, slot);
      var declared = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(first[4..]));
      var name = ReadName(first[10..31]);
      if (string.IsNullOrEmpty(name))
        throw new InvalidDataException($"Bank {bank + 1}, directory slot {slot + 1} is a live first block with no filename.");

      var blocks = new List<int>();
      var local = new HashSet<int>();
      var current = slot;
      while (true) {
        if (current is < 0 or >= DataBlocksPerBank)
          throw new InvalidDataException($"PS1 save '{name}' in bank {bank + 1} points outside the 15 data blocks.");
        if (!local.Add(current))
          throw new InvalidDataException($"PS1 save '{name}' in bank {bank + 1} contains a cyclic block chain.");
        if (claimed[current])
          throw new InvalidDataException($"PS1 save '{name}' in bank {bank + 1} cross-links a block already owned by another save.");

        var frame = DirectoryFrame(image, bank, current);
        var state = states[current];
        var next = BinaryPrimitives.ReadUInt16LittleEndian(frame[8..]);
        var firstBlock = blocks.Count == 0;
        if (firstBlock) {
          if (state != LiveFirst)
            throw new InvalidDataException($"PS1 save '{name}' in bank {bank + 1} does not start with state 0x51.");
        } else if (next == 0xFFFF) {
          if (state != LiveLast)
            throw new InvalidDataException($"PS1 save '{name}' in bank {bank + 1} does not end with state 0x53.");
        } else if (state != LiveMiddle) {
          throw new InvalidDataException($"PS1 save '{name}' in bank {bank + 1} has a non-middle block without state 0x52.");
        }

        blocks.Add(current);
        claimed[current] = true;
        if (next == 0xFFFF) break;
        current = next;
      }

      var capacity = checked(blocks.Count * BlockSize);
      if (declared <= 0 || declared > capacity)
        throw new InvalidDataException(
          $"PS1 save '{name}' in bank {bank + 1} declares {declared} bytes but its chain holds {capacity}.");

      var data = new byte[declared];
      var written = 0;
      foreach (var block in blocks) {
        var take = Math.Min(BlockSize, declared - written);
        if (take <= 0) break;
        image.AsSpan(bankBase + (block + 1) * BlockSize, take).CopyTo(data.AsSpan(written));
        written += take;
      }
      saves.Add(new Save(bank, name, declared, data, blocks));
    }

    for (var slot = 0; slot < DataBlocksPerBank; ++slot)
      if (states[slot] is LiveMiddle or LiveLast && !claimed[slot])
        throw new InvalidDataException($"Bank {bank + 1}, directory slot {slot + 1} is an orphaned live continuation block.");
  }

  private static void FormatFreshBank(byte[] image, int bank) {
    var bankBase = BankBase(bank);

    var header = image.AsSpan(bankBase, FrameSize);
    header.Clear();
    header[0] = (byte)'M';
    header[1] = (byte)'C';
    StampChecksum(header);

    for (var slot = 0; slot < DataBlocksPerBank; ++slot) {
      var frame = DirectoryFrame(image, bank, slot);
      frame.Clear();
      BinaryPrimitives.WriteUInt32LittleEndian(frame, FreeFresh);
      BinaryPrimitives.WriteUInt16LittleEndian(frame[8..], 0xFFFF);
      StampChecksum(frame);
    }

    for (var frameIndex = 16; frameIndex <= 35; ++frameIndex) {
      var frame = image.AsSpan(bankBase + frameIndex * FrameSize, FrameSize);
      frame.Clear();
      BinaryPrimitives.WriteUInt32LittleEndian(frame, 0xFFFFFFFF);
      StampChecksum(frame);
    }

    var writeTest = image.AsSpan(bankBase + 63 * FrameSize, FrameSize);
    writeTest.Clear();
    writeTest[0] = (byte)'M';
    writeTest[1] = (byte)'C';
    StampChecksum(writeTest);
  }

  private static void WriteSave(byte[] image, int bank, Save save, IReadOnlyList<int> slots) {
    var dataOffset = 0;
    for (var i = 0; i < slots.Count; ++i) {
      var slot = slots[i];
      var next = i == slots.Count - 1 ? 0xFFFF : slots[i + 1];
      var state = i switch {
        0 => LiveFirst,
        _ when i == slots.Count - 1 => LiveLast,
        _ => LiveMiddle,
      };

      var frame = DirectoryFrame(image, bank, slot);
      frame.Clear();
      BinaryPrimitives.WriteUInt32LittleEndian(frame, state);
      BinaryPrimitives.WriteUInt16LittleEndian(frame[8..], checked((ushort)next));
      if (i == 0) {
        BinaryPrimitives.WriteUInt32LittleEndian(frame[4..], checked((uint)save.DeclaredSize));
        var nameBytes = Encoding.ASCII.GetBytes(save.Name);
        nameBytes.CopyTo(frame[10..]);
      }
      StampChecksum(frame);

      var dataBlock = image.AsSpan(BankBase(bank) + (slot + 1) * BlockSize, BlockSize);
      dataBlock.Clear();
      var take = Math.Min(BlockSize, save.Data.Length - dataOffset);
      if (take > 0) {
        save.Data.AsSpan(dataOffset, take).CopyTo(dataBlock);
        dataOffset += take;
      }
    }
  }

  private static Span<byte> DirectoryFrame(byte[] image, int bank, int slot)
    => image.AsSpan(BankBase(bank) + (slot + 1) * FrameSize, FrameSize);

  private static int BankBase(int bank) => checked(bank * CardSize);

  private static void ValidateRawName(string name) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    if (name.Length > 20)
      throw new InvalidDataException($"PS1 memory-card filenames are limited to 20 ASCII characters: '{name}'.");
    foreach (var ch in name)
      if (ch is < ' ' or > '~')
        throw new InvalidDataException($"PS1 memory-card filename '{name}' contains non-ASCII printable character U+{(int)ch:X4}.");
  }

  private static string ReadName(ReadOnlySpan<byte> field) {
    var length = field.IndexOf((byte)0);
    if (length < 0) length = field.Length;
    return Encoding.ASCII.GetString(field[..length]);
  }

  private static string EscapeName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var ch in name) {
      if (ch is '%' or '/' or '\\' || ch is < ' ' or > '~')
        sb.Append('%').Append(((byte)ch).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
      else
        sb.Append(ch);
    }
    return sb.ToString();
  }

  private static string UnescapeName(string name) {
    var sb = new StringBuilder(name.Length);
    for (var i = 0; i < name.Length; ++i) {
      if (name[i] == '%' && i + 2 < name.Length
          && byte.TryParse(name.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var value)) {
        sb.Append((char)value);
        i += 2;
      } else {
        sb.Append(name[i]);
      }
    }
    var result = sb.ToString();
    ValidateRawName(result);
    return result;
  }

  private static bool IsKnownState(uint state)
    => state is LiveFirst or LiveMiddle or LiveLast
      or FreeFresh or FreeDeletedFirst or FreeDeletedMiddle or FreeDeletedLast;

  internal static void StampChecksum(Span<byte> frame) {
    byte checksum = 0;
    for (var i = 0; i < FrameSize - 1; ++i) checksum ^= frame[i];
    frame[FrameSize - 1] = checksum;
  }

  private static void ValidateFrameChecksum(ReadOnlySpan<byte> frame, int bank, int frameIndex) {
    byte checksum = 0;
    for (var i = 0; i < FrameSize - 1; ++i) checksum ^= frame[i];
    if (checksum != frame[FrameSize - 1])
      throw new InvalidDataException(
        $"PS1 memory-card bank {bank + 1}, frame {frameIndex} checksum mismatch: stored 0x{frame[FrameSize - 1]:X2}, computed 0x{checksum:X2}.");
  }
}
