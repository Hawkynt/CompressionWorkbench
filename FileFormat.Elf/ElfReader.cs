#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Elf;

/// <summary>
/// Reads an ELF (Executable and Linkable Format) object / executable / shared library
/// and surfaces each section header as a logical entry. Knows enough to:
/// <list type="bullet">
///   <item>Decode section names via the section-header string table (<c>e_shstrndx</c>).</item>
///   <item>Emit <c>.interp</c> as <c>interp.txt</c>.</item>
///   <item>Emit <c>.dynsym</c> / <c>.symtab</c> decoded against their paired string table as a combined <c>symbols.txt</c>.</item>
///   <item>Emit each <c>.note.*</c> section under <c>notes/{suffix}.bin</c>.</item>
///   <item>Emit everything else as <c>sections/{name}.bin</c>.</item>
/// </list>
/// Handles both 32-bit (<c>ELFCLASS32</c>) and 64-bit (<c>ELFCLASS64</c>) variants and
/// both endiannesses, as signalled by <c>e_ident[EI_CLASS]</c>/<c>EI_DATA</c>.
/// </summary>
public sealed class ElfReader {

  /// <summary>One entry surfaced from an ELF file.</summary>
  public sealed record Entry(string Name, byte[] Data);

  private const int ShtSymtab = 2;
  private const int ShtDynsym = 11;

  public List<Entry> ReadAll(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    stream.Position = 0;
    var all = new byte[stream.Length];
    stream.ReadExactly(all, 0, all.Length);

    if (all.Length < 52 || all[0] != 0x7F || all[1] != 'E' || all[2] != 'L' || all[3] != 'F')
      throw new InvalidDataException("Not an ELF file.");

    var is64 = all[4] == 2;
    var littleEndian = all[5] == 1;

    // ELF header layout differs by class:
    //   e_shoff:     32-bit offset 0x20 (32-bit ELF) / 0x28 (64-bit ELF)
    //   e_shentsize: 16-bit offset 0x2E (32-bit) / 0x3A (64-bit)
    //   e_shnum:     16-bit offset 0x30 (32-bit) / 0x3C (64-bit)
    //   e_shstrndx:  16-bit offset 0x32 (32-bit) / 0x3E (64-bit)
    long shoff; ushort shentsize, shnum, shstrndx;
    if (is64) {
      shoff     = (long)ReadU64(all, 0x28, littleEndian);
      shentsize = ReadU16(all, 0x3A, littleEndian);
      shnum     = ReadU16(all, 0x3C, littleEndian);
      shstrndx  = ReadU16(all, 0x3E, littleEndian);
    } else {
      shoff     = ReadU32(all, 0x20, littleEndian);
      shentsize = ReadU16(all, 0x2E, littleEndian);
      shnum     = ReadU16(all, 0x30, littleEndian);
      shstrndx  = ReadU16(all, 0x32, littleEndian);
    }
    if (shoff == 0 || shnum == 0)
      return []; // no section table — e.g. stripped shared object.

    if (shoff + (long)shnum * shentsize > all.Length)
      throw new InvalidDataException("Section header table extends past EOF.");

    // Each section-header entry:
    //   sh_name(4) sh_type(4) sh_flags(4/8) sh_addr(4/8) sh_offset(4/8) sh_size(4/8) ...
    var sections = new Section[shnum];
    for (var i = 0; i < shnum; i++) {
      var hOff = shoff + i * shentsize;
      var shName   = ReadU32(all, (int)hOff, littleEndian);
      var shType   = ReadU32(all, (int)hOff + 4, littleEndian);
      long shOffset, shSize;
      if (is64) {
        shOffset = (long)ReadU64(all, (int)hOff + 24, littleEndian);
        shSize   = (long)ReadU64(all, (int)hOff + 32, littleEndian);
      } else {
        shOffset = ReadU32(all, (int)hOff + 16, littleEndian);
        shSize   = ReadU32(all, (int)hOff + 20, littleEndian);
      }
      uint shLink = 0;
      if (is64)
        shLink = ReadU32(all, (int)hOff + 40, littleEndian);
      else
        shLink = ReadU32(all, (int)hOff + 24, littleEndian);
      sections[i] = new Section(shName, shType, shOffset, shSize, shLink);
    }

    // Resolve section names from the section-header string table.
    if (shstrndx >= shnum)
      return [];
    var shstr = sections[shstrndx];
    var shStrBytes = SafeRead(all, shstr.Offset, shstr.Size);

    var entries = new List<Entry>();
    for (var i = 0; i < shnum; i++) {
      if (i == 0) continue; // SHT_NULL
      var s = sections[i];
      var name = ReadAsciiZ(shStrBytes, (int)s.NameOffset);
      var bytes = SafeRead(all, s.Offset, s.Size);

      switch (name) {
        case ".interp":
          entries.Add(new Entry("interp.txt", bytes));
          break;
        case ".dynsym":
        case ".symtab": {
          // sh_link references the associated string table.
          byte[] strBytes = [];
          if (s.Link < shnum)
            strBytes = SafeRead(all, sections[s.Link].Offset, sections[s.Link].Size);
          var decoded = DecodeSymbolTable(bytes, strBytes, is64, littleEndian, name);
          entries.Add(new Entry($"symbols{(name == ".dynsym" ? "_dyn" : "")}.txt",
            Encoding.UTF8.GetBytes(decoded)));
          break;
        }
        default:
          if (name.StartsWith(".note")) {
            var suffix = name.Length > 5 ? name[5..].TrimStart('.') : "note";
            if (string.IsNullOrEmpty(suffix)) suffix = "note";
            entries.Add(new Entry($"notes/{SanitizeName(suffix)}.bin", bytes));
          } else {
            var safe = string.IsNullOrEmpty(name) ? $"section_{i}" : SanitizeName(name.TrimStart('.'));
            if (string.IsNullOrEmpty(safe)) safe = $"section_{i}";
            entries.Add(new Entry($"sections/{safe}.bin", bytes));
          }
          break;
      }
    }
    return entries;
  }

  private readonly record struct Section(uint NameOffset, uint Type, long Offset, long Size, uint Link);

  private static byte[] SafeRead(byte[] all, long offset, long size) {
    if (size <= 0) return [];
    if (offset < 0 || offset + size > all.Length) return [];
    return all.AsSpan((int)offset, (int)size).ToArray();
  }

  private static string DecodeSymbolTable(byte[] table, byte[] strTable, bool is64, bool le, string label) {
    var entrySize = is64 ? 24 : 16;
    if (entrySize == 0 || table.Length < entrySize) return $"# {label}: empty\n";
    var sb = new StringBuilder();
    sb.Append("# ").Append(label).Append('\n');
    var count = table.Length / entrySize;
    for (var i = 0; i < count; i++) {
      var off = i * entrySize;
      // 32-bit: st_name(4) st_value(4) st_size(4) st_info(1) st_other(1) st_shndx(2)
      // 64-bit: st_name(4) st_info(1) st_other(1) st_shndx(2) st_value(8) st_size(8)
      var nameOff = ReadU32(table, off, le);
      var name = ReadAsciiZ(strTable, (int)nameOff);
      if (string.IsNullOrEmpty(name)) continue;
      sb.Append(name).Append('\n');
    }
    return sb.ToString();
  }

  private static string ReadAsciiZ(byte[] b, int offset) {
    if (offset < 0 || offset >= b.Length) return "";
    var end = offset;
    while (end < b.Length && b[end] != 0) end++;
    return Encoding.ASCII.GetString(b, offset, end - offset);
  }

  private static ushort ReadU16(byte[] b, int off, bool le) =>
    le ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(off))
       : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(off));

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
    return sb.ToString();
  }
}
