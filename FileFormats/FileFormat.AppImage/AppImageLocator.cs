using System.Buffers.Binary;

namespace FileFormat.AppImage;

/// <summary>
/// Parses the ELF header of an AppImage file just far enough to determine:
/// <list type="bullet">
///   <item>AppImage type (1 or 2, from the <c>AI\x01</c>/<c>AI\x02</c> marker in e_ident[8..10]).</item>
///   <item>Target architecture (e_machine, optionally bitness and endianness).</item>
///   <item>The offset at which the appended SquashFS filesystem begins.</item>
/// </list>
/// The SquashFS start is computed as the maximum of:
/// <list type="bullet">
///   <item>End of the section header table (<c>e_shoff + e_shnum * e_shentsize</c>).</item>
///   <item>End of the program header table (<c>e_phoff + e_phnum * e_phentsize</c>).</item>
///   <item>End of the last section payload that has in-file bytes
///         (<c>max(sh_offset + sh_size)</c> for sh_type != SHT_NOBITS).</item>
/// </list>
/// followed by a scan for the <c>hsqs</c> magic (possibly skipping alignment padding
/// that some <c>appimagetool</c> runtimes insert up to a few kilobytes past the ELF end).
/// </summary>
internal static class AppImageLocator {
  // Offsets within the ELF header where the AppImage marker lives.
  internal const int AppImageMarkerOffset = 8;

  private const uint SquashFsMagicLe = 0x73717368u; // "hsqs" little-endian

  internal sealed record Info(
    byte AppImageType,
    byte ElfClass,       // 1 = 32-bit, 2 = 64-bit
    byte ElfData,        // 1 = little-endian, 2 = big-endian
    ushort Machine,
    long SquashFsOffset,
    long FileLength);

  internal static Info Locate(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));

    var fileLength = stream.Length;
    if (fileLength < 64)
      throw new InvalidDataException("Not an AppImage: file is shorter than a minimal ELF header.");

    stream.Position = 0;
    Span<byte> ident = stackalloc byte[16];
    ReadExact(stream, ident);

    if (ident[0] != 0x7F || ident[1] != (byte)'E' || ident[2] != (byte)'L' || ident[3] != (byte)'F')
      throw new InvalidDataException("Not an ELF file, cannot be an AppImage.");

    // AppImage marker at e_ident[8..10]: 'A' 'I' type-byte
    if (ident[AppImageMarkerOffset] != (byte)'A' || ident[AppImageMarkerOffset + 1] != (byte)'I')
      throw new InvalidDataException("Missing AppImage 'AI' marker at ELF offset 8.");

    var appImageType = ident[AppImageMarkerOffset + 2];
    if (appImageType is not (1 or 2))
      throw new InvalidDataException($"Unsupported AppImage type: {appImageType}.");

    var elfClass = ident[4];
    var elfData = ident[5];
    var is64 = elfClass == 2;
    var le = elfData == 1;

    // Load enough of the header to read offsets/sizes.
    var headerLen = is64 ? 64 : 52;
    if (fileLength < headerLen)
      throw new InvalidDataException("ELF header truncated.");

    stream.Position = 0;
    var hdr = new byte[headerLen];
    ReadExact(stream, hdr);

    var machine = ReadU16(hdr, 0x12, le);
    long phoff, shoff;
    ushort phentsize, phnum, shentsize, shnum;
    if (is64) {
      phoff     = (long)ReadU64(hdr, 0x20, le);
      shoff     = (long)ReadU64(hdr, 0x28, le);
      phentsize = ReadU16(hdr, 0x36, le);
      phnum     = ReadU16(hdr, 0x38, le);
      shentsize = ReadU16(hdr, 0x3A, le);
      shnum     = ReadU16(hdr, 0x3C, le);
    } else {
      phoff     = ReadU32(hdr, 0x1C, le);
      shoff     = ReadU32(hdr, 0x20, le);
      phentsize = ReadU16(hdr, 0x2A, le);
      phnum     = ReadU16(hdr, 0x2C, le);
      shentsize = ReadU16(hdr, 0x2E, le);
      shnum     = ReadU16(hdr, 0x30, le);
    }

    long elfEnd = headerLen;
    if (phoff > 0) elfEnd = Math.Max(elfEnd, phoff + (long)phnum * phentsize);
    if (shoff > 0) elfEnd = Math.Max(elfEnd, shoff + (long)shnum * shentsize);

    // Walk the section header table and track max (sh_offset + sh_size) for sh_type != SHT_NOBITS (8).
    if (shoff > 0 && shnum > 0 && shentsize >= (is64 ? 64 : 40)) {
      var tableLen = (long)shnum * shentsize;
      if (shoff + tableLen <= fileLength) {
        stream.Position = shoff;
        var table = new byte[tableLen];
        ReadExact(stream, table);
        for (var i = 0; i < shnum; i++) {
          var rowOff = i * shentsize;
          var shType = ReadU32(table, rowOff + 4, le);
          long shOffset, shSize;
          if (is64) {
            shOffset = (long)ReadU64(table, rowOff + 24, le);
            shSize   = (long)ReadU64(table, rowOff + 32, le);
          } else {
            shOffset = ReadU32(table, rowOff + 16, le);
            shSize   = ReadU32(table, rowOff + 20, le);
          }
          if (shType == 8) continue; // SHT_NOBITS — no bytes on disk
          if (shOffset == 0) continue;
          var sectionEnd = shOffset + shSize;
          if (sectionEnd > elfEnd)
            elfEnd = sectionEnd;
        }
      }
    }

    if (elfEnd > fileLength)
      throw new InvalidDataException("Computed ELF end is past end of file — AppImage is truncated.");

    // Find SquashFS by scanning for the 'hsqs' magic starting at elfEnd.
    // Some AppImages align the filesystem to a 4 KiB boundary; we scan up to 64 KiB past elfEnd.
    var squashStart = FindSquashFs(stream, elfEnd, fileLength, maxScan: 64 * 1024);
    if (squashStart < 0)
      throw new InvalidDataException("AppImage: could not locate SquashFS 'hsqs' magic past ELF end.");

    return new Info(
      AppImageType: appImageType,
      ElfClass: elfClass,
      ElfData: elfData,
      Machine: machine,
      SquashFsOffset: squashStart,
      FileLength: fileLength);
  }

  private static long FindSquashFs(Stream stream, long start, long length, int maxScan) {
    // First try the canonical location — elfEnd itself.
    if (start + 4 <= length && ReadMagic(stream, start) == SquashFsMagicLe)
      return start;
    // Then scan forward up to maxScan bytes.
    var scanLimit = Math.Min(start + maxScan, length - 4);
    for (var p = start + 1; p <= scanLimit; p++)
      if (ReadMagic(stream, p) == SquashFsMagicLe)
        return p;
    return -1;
  }

  private static uint ReadMagic(Stream stream, long at) {
    stream.Position = at;
    Span<byte> b = stackalloc byte[4];
    var n = stream.Read(b);
    return n < 4 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(b);
  }

  internal static string MachineName(ushort m) => m switch {
    0 => "none",
    3 => "x86",
    8 => "mips",
    40 => "arm",
    62 => "x86_64",
    183 => "aarch64",
    243 => "riscv",
    _ => $"em_{m}",
  };

  private static void ReadExact(Stream s, Span<byte> buf) {
    var total = 0;
    while (total < buf.Length) {
      var n = s.Read(buf[total..]);
      if (n <= 0) throw new EndOfStreamException("Unexpected EOF while reading ELF header.");
      total += n;
    }
  }

  private static void ReadExact(Stream s, byte[] buf) => ReadExact(s, buf.AsSpan());

  private static ushort ReadU16(ReadOnlySpan<byte> b, int o, bool le) =>
    le ? BinaryPrimitives.ReadUInt16LittleEndian(b[o..])
       : BinaryPrimitives.ReadUInt16BigEndian(b[o..]);

  private static uint ReadU32(ReadOnlySpan<byte> b, int o, bool le) =>
    le ? BinaryPrimitives.ReadUInt32LittleEndian(b[o..])
       : BinaryPrimitives.ReadUInt32BigEndian(b[o..]);

  private static ulong ReadU64(ReadOnlySpan<byte> b, int o, bool le) =>
    le ? BinaryPrimitives.ReadUInt64LittleEndian(b[o..])
       : BinaryPrimitives.ReadUInt64BigEndian(b[o..]);
}
