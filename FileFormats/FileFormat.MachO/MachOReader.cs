#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileFormat.MachO.MachOConstants;

namespace FileFormat.MachO;

/// <summary>
/// Reads Mach-O executables as logical archives:
/// <list type="bullet">
///   <item>Fat / universal binaries yield one entry per embedded slice (<c>slice_{arch}.macho</c>), carrying the raw per-slice bytes.</item>
///   <item>Single-slice Mach-O binaries yield one entry per <c>LC_SEGMENT</c>/<c>LC_SEGMENT_64</c> (under <c>segments/</c>) plus metadata entries for <c>LC_SYMTAB</c>, <c>LC_UUID</c>, and <c>LC_CODE_SIGNATURE</c>.</item>
/// </list>
/// All numeric fields are parsed using the endianness implied by the magic word.
/// </summary>
public sealed class MachOReader {

  /// <summary>One logical entry surfaced from a Mach-O or fat Mach-O file.</summary>
  public sealed record Entry(string Name, byte[] Data);

  public List<Entry> ReadAll(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    stream.Position = 0;
    var all = new byte[stream.Length];
    stream.ReadExactly(all, 0, all.Length);

    if (all.Length < 4)
      throw new InvalidDataException("File too short to be Mach-O.");

    var magic = BinaryPrimitives.ReadUInt32BigEndian(all);
    return magic switch {
      FatMagic or FatCigam or FatMagic64 or FatCigam64 => ReadFat(all, magic),
      _ => ReadSingleSlice(all)
    };
  }

  private static List<Entry> ReadFat(byte[] all, uint magic) {
    // Fat headers are always big-endian on disk; Cigam variants are just the byte-swapped
    // magic so other parsers recognise them — the layout is still BE.
    var is64 = magic is FatMagic64 or FatCigam64;
    if (all.Length < 8) throw new InvalidDataException("Truncated fat header.");
    var nfat = BinaryPrimitives.ReadUInt32BigEndian(all.AsSpan(4));
    var recSize = is64 ? 32 : 20;
    var headerEnd = 8 + (int)nfat * recSize;
    if (nfat > 0x1000 || headerEnd > all.Length)
      throw new InvalidDataException($"Implausible fat header (nfat_arch={nfat}).");

    var entries = new List<Entry>((int)nfat);
    for (var i = 0; i < nfat; i++) {
      var off = 8 + i * recSize;
      var cpuType = BinaryPrimitives.ReadInt32BigEndian(all.AsSpan(off));
      // cpusubtype at off+4 (unused)
      long sliceOffset;
      long sliceSize;
      if (is64) {
        sliceOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(all.AsSpan(off + 8));
        sliceSize   = (long)BinaryPrimitives.ReadUInt64BigEndian(all.AsSpan(off + 16));
      } else {
        sliceOffset = BinaryPrimitives.ReadUInt32BigEndian(all.AsSpan(off + 8));
        sliceSize   = BinaryPrimitives.ReadUInt32BigEndian(all.AsSpan(off + 12));
      }
      if (sliceOffset < 0 || sliceSize < 0 || sliceOffset + sliceSize > all.Length)
        throw new InvalidDataException($"Fat slice {i} spans beyond file (offset={sliceOffset}, size={sliceSize}).");

      var slice = all.AsSpan((int)sliceOffset, (int)sliceSize).ToArray();
      entries.Add(new Entry($"slice_{CpuTypeName(cpuType)}.macho", slice));
    }
    return entries;
  }

  private static List<Entry> ReadSingleSlice(byte[] all) {
    // Figure out bitness and endianness. In host-endian (LE) variants the magic is
    // 0xFEEDFACE/F in LE when read as a little-endian u32; in swapped (BE) variants
    // the magic reads as 0xFEEDFACE/F when read BE. We sniff both.
    var leMagic = BinaryPrimitives.ReadUInt32LittleEndian(all);
    var beMagic = BinaryPrimitives.ReadUInt32BigEndian(all);

    bool is64, littleEndian;
    if (leMagic == MhMagic) {
      is64 = false; littleEndian = true;
    } else if (leMagic == MhMagic64) {
      is64 = true; littleEndian = true;
    } else if (beMagic == MhMagic) {
      is64 = false; littleEndian = false;
    } else if (beMagic == MhMagic64) {
      is64 = true; littleEndian = false;
    } else {
      throw new InvalidDataException($"Not a Mach-O file (magic 0x{leMagic:X8}).");
    }

    var headerSize = is64 ? 32 : 28;
    if (all.Length < headerSize) throw new InvalidDataException("Truncated mach_header.");

    // mach_header layout: magic(4) cputype(4) cpusubtype(4) filetype(4) ncmds(4) sizeofcmds(4) flags(4) [reserved(4) — 64-bit only]
    uint ncmds = ReadU32(all, 16, littleEndian);
    uint sizeOfCmds = ReadU32(all, 20, littleEndian);
    if (ncmds > 0x10000 || sizeOfCmds > all.Length)
      throw new InvalidDataException("Implausible load-command count/size.");

    var entries = new List<Entry>();
    var cursor = headerSize;
    var cmdsEnd = headerSize + (long)sizeOfCmds;
    if (cmdsEnd > all.Length) throw new InvalidDataException("Load commands extend past EOF.");

    for (var i = 0; i < ncmds; i++) {
      if (cursor + 8 > cmdsEnd) break;
      var cmd = ReadU32(all, cursor, littleEndian);
      var cmdSize = ReadU32(all, cursor + 4, littleEndian);
      if (cmdSize < 8 || cursor + cmdSize > cmdsEnd)
        throw new InvalidDataException($"Bad load command #{i} (cmd=0x{cmd:X}, size={cmdSize}).");

      switch (cmd) {
        case LcSegment:
        case LcSegment64: {
          var (segName, fileOff, fileSize) = ParseSegment(all, cursor, cmd == LcSegment64, littleEndian);
          if (fileSize > 0 && fileOff + fileSize <= (ulong)(long)all.Length) {
            var bytes = all.AsSpan((int)fileOff, (int)fileSize).ToArray();
            entries.Add(new Entry($"segments/{SanitizeName(segName)}.bin", bytes));
          } else {
            entries.Add(new Entry($"segments/{SanitizeName(segName)}.bin", []));
          }
          break;
        }
        case LcSymtab: {
          // symtab_command: cmd(4) cmdsize(4) symoff(4) nsyms(4) stroff(4) strsize(4)
          var stroff = ReadU32(all, cursor + 16, littleEndian);
          var strsize = ReadU32(all, cursor + 20, littleEndian);
          if (strsize > 0 && stroff + strsize <= all.Length)
            entries.Add(new Entry("symbols.txt", all.AsSpan((int)stroff, (int)strsize).ToArray()));
          break;
        }
        case LcUuid: {
          // uuid_command: cmd(4) cmdsize(4) uuid(16)
          if (cmdSize >= 24) {
            var uuidBytes = all.AsSpan(cursor + 8, 16).ToArray();
            entries.Add(new Entry("metadata/uuid.bin", uuidBytes));
          }
          break;
        }
        case LcCodeSignature: {
          // linkedit_data_command: cmd(4) cmdsize(4) dataoff(4) datasize(4)
          var dataoff = ReadU32(all, cursor + 8, littleEndian);
          var datasize = ReadU32(all, cursor + 12, littleEndian);
          if (datasize > 0 && dataoff + datasize <= all.Length)
            entries.Add(new Entry("metadata/code_signature.bin", all.AsSpan((int)dataoff, (int)datasize).ToArray()));
          break;
        }
      }

      cursor += (int)cmdSize;
    }

    return entries;
  }

  private static (string Name, ulong FileOff, ulong FileSize) ParseSegment(byte[] all, int cmdOff, bool is64, bool littleEndian) {
    // segment_command(_64): cmd(4) cmdsize(4) segname(16) vmaddr(...) vmsize(...) fileoff(...) filesize(...) ...
    // On 32-bit the size fields are 4 bytes; on 64-bit they're 8 bytes.
    var segNameBytes = all.AsSpan(cmdOff + 8, 16);
    var nul = segNameBytes.IndexOf((byte)0);
    if (nul < 0) nul = segNameBytes.Length;
    var name = Encoding.ASCII.GetString(segNameBytes[..nul]);
    if (string.IsNullOrEmpty(name)) name = "UNNAMED";

    ulong fileOff, fileSize;
    if (is64) {
      // skip: cmd(4) cmdsize(4) segname(16) vmaddr(8) vmsize(8) -> fileoff at +40, filesize at +48
      fileOff  = ReadU64(all, cmdOff + 40, littleEndian);
      fileSize = ReadU64(all, cmdOff + 48, littleEndian);
    } else {
      // skip: cmd(4) cmdsize(4) segname(16) vmaddr(4) vmsize(4) -> fileoff at +32, filesize at +36
      fileOff  = ReadU32(all, cmdOff + 32, littleEndian);
      fileSize = ReadU32(all, cmdOff + 36, littleEndian);
    }
    return (name, fileOff, fileSize);
  }

  private static uint ReadU32(byte[] b, int off, bool le) =>
    le ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off))
       : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off));

  private static ulong ReadU64(byte[] b, int off, bool le) =>
    le ? BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(off))
       : BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(off));

  private static string SanitizeName(string s) {
    var sb = new StringBuilder(s.Length);
    foreach (var c in s)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    return sb.Length == 0 ? "UNNAMED" : sb.ToString();
  }
}
