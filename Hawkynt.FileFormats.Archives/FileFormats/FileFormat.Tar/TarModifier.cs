#pragma warning disable CS1591
namespace FileFormat.Tar;

/// <summary>
/// Random-access in-place modifier for TAR archives. Add appends a new
/// entry just before the trailing zero blocks — touches only the new
/// entry's bytes plus the (small) terminator. Remove walks the header
/// chain to locate the target, then shifts trailing bytes forward to
/// close the gap (necessary because TAR has no central directory).
/// </summary>
public static class TarModifier {

  private const int BlockSize = TarConstants.BlockSize; // 512

  /// <summary>
  /// Appends a regular file entry. Walks the existing header chain to find
  /// the trailing zero blocks, writes the new header + data + zero blocks
  /// in their place, and truncates to the new length.
  /// </summary>
  public static void AddFile(Stream tar, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(tar);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var terminatorOffset = FindTerminatorOffset(tar);
    tar.Position = terminatorOffset;

    var entry = new TarEntry {
      Name = name,
      Size = data.Length,
      TypeFlag = TarConstants.TypeRegular,
      Mode = 0x1A4, // 0644
      ModifiedTime = DateTimeOffset.Now,
    };

    TarHeader.WriteHeader(tar, entry);
    if (data.Length > 0) {
      tar.Write(data);
      var pad = (BlockSize - data.Length % BlockSize) % BlockSize;
      if (pad > 0) {
        var zeros = new byte[pad];
        tar.Write(zeros);
      }
    }

    // Two 512-byte zero terminator blocks.
    var terminator = new byte[BlockSize * 2];
    tar.Write(terminator);
    tar.SetLength(tar.Position);
  }

  /// <summary>
  /// Removes the named entry. Returns true if found. The trailing portion
  /// of the file is shifted forward to close the gap (TAR has no central
  /// directory; readers walk headers sequentially, so we must compact).
  /// </summary>
  public static bool RemoveFile(Stream tar, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(tar);
    ArgumentNullException.ThrowIfNull(name);

    var locator = LocateEntry(tar, name);
    if (!locator.Found) return false;

    if (wipeData)
      ZeroRange(tar, locator.HeaderOffset, locator.TotalEntrySize);

    var afterEntry = locator.HeaderOffset + locator.TotalEntrySize;
    var bytesToShift = tar.Length - afterEntry;
    if (bytesToShift > 0) {
      // Forward shift: copy in BlockSize-aligned chunks from afterEntry → headerOffset.
      var buf = new byte[64 * 1024];
      var src = afterEntry;
      var dst = locator.HeaderOffset;
      while (bytesToShift > 0) {
        var chunk = (int)Math.Min(buf.Length, bytesToShift);
        tar.Position = src;
        var read = 0;
        while (read < chunk) {
          var n = tar.Read(buf, read, chunk - read);
          if (n <= 0) break;
          read += n;
        }
        tar.Position = dst;
        tar.Write(buf, 0, read);
        src += read;
        dst += read;
        bytesToShift -= read;
      }
    }
    tar.SetLength(tar.Length - locator.TotalEntrySize);
    return true;
  }

  // ── Header walking ────────────────────────────────────────────────────

  /// <summary>
  /// Returns the byte offset of the first 512-byte block that's all zeros
  /// (the start of the terminator). Walks header-chain only — never reads
  /// data blocks, just seeks past them using each header's size field.
  /// </summary>
  private static long FindTerminatorOffset(Stream tar) {
    tar.Position = 0;
    Span<byte> hdr = stackalloc byte[BlockSize];
    while (tar.Position + BlockSize <= tar.Length) {
      var blockStart = tar.Position;
      var read = ReadFully(tar, hdr);
      if (read < BlockSize) return blockStart; // truncated trailer

      if (IsAllZeros(hdr)) return blockStart;

      var size = ParseSize(hdr);
      var dataBlocks = (size + BlockSize - 1) / BlockSize;
      tar.Position = blockStart + BlockSize + dataBlocks * BlockSize;
    }
    return tar.Length; // no terminator found; append at end
  }

  private readonly record struct EntryLocator(bool Found, long HeaderOffset, long TotalEntrySize);

  private static EntryLocator LocateEntry(Stream tar, string targetName) {
    tar.Position = 0;
    Span<byte> hdr = stackalloc byte[BlockSize];
    while (tar.Position + BlockSize <= tar.Length) {
      var headerOffset = tar.Position;
      var read = ReadFully(tar, hdr);
      if (read < BlockSize) break;
      if (IsAllZeros(hdr)) break;

      var name = ParseName(hdr);
      var size = ParseSize(hdr);
      var dataBlocks = (size + BlockSize - 1) / BlockSize;
      var totalEntrySize = BlockSize + dataBlocks * BlockSize;

      if (name == targetName)
        return new EntryLocator(true, headerOffset, totalEntrySize);

      tar.Position = headerOffset + totalEntrySize;
    }
    return new EntryLocator(false, 0, 0);
  }

  // ── Header field parsing ──────────────────────────────────────────────

  private static long ParseSize(ReadOnlySpan<byte> hdr) {
    // Size field at offset 124, length 12 octal (terminated by NUL or space).
    long n = 0;
    for (var i = 124; i < 124 + 12; i++) {
      var c = hdr[i];
      if (c == 0 || c == (byte)' ') break;
      if (c < (byte)'0' || c > (byte)'7') return 0;
      n = (n << 3) | (long)(c - (byte)'0');
    }
    return n;
  }

  private static string ParseName(ReadOnlySpan<byte> hdr) {
    // Name = optional prefix (offset 345, len 155) + "/" + name (offset 0, len 100).
    var prefix = ReadCString(hdr.Slice(345, 155));
    var name = ReadCString(hdr.Slice(0, 100));
    return prefix.Length > 0 ? prefix + "/" + name : name;
  }

  private static string ReadCString(ReadOnlySpan<byte> data) {
    var end = data.Length;
    for (var i = 0; i < data.Length; i++) {
      if (data[i] == 0) { end = i; break; }
    }
    return System.Text.Encoding.UTF8.GetString(data[..end]);
  }

  private static bool IsAllZeros(ReadOnlySpan<byte> data) {
    foreach (var b in data) if (b != 0) return false;
    return true;
  }

  private static int ReadFully(Stream s, Span<byte> buf) {
    var read = 0;
    while (read < buf.Length) {
      var n = s.Read(buf[read..]);
      if (n <= 0) break;
      read += n;
    }
    return read;
  }

  private static void ZeroRange(Stream s, long offset, long length) {
    var buf = new byte[(int)Math.Min(length, 8192)];
    s.Position = offset;
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(buf.Length, remaining);
      s.Write(buf, 0, chunk);
      remaining -= chunk;
    }
  }
}
