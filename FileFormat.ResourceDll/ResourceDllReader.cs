#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileFormat.ResourceDll.ResourceDllConstants;

namespace FileFormat.ResourceDll;

/// <summary>
/// Reads files embedded as Win32 resources in a PE32/PE32+ DLL/EXE.
/// <see cref="Read"/> returns only <c>RT_RCDATA</c> string-named entries (the shape
/// <see cref="ResourceDllWriter"/> produces); <see cref="ReadAll"/> returns every
/// resource, regardless of type, suitable for general PE-resource browsing
/// (Tier 1 of <c>docs/multi-payload-archives.md</c>). Multi-language resources
/// expose only the first language entry.
/// </summary>
public sealed class ResourceDllReader {
  /// <summary>One <c>RT_RCDATA</c>-style entry as surfaced by <see cref="Read"/>.
  /// <c>Data.Length</c> is authoritative; there is no separate size field.</summary>
  public sealed record Entry(string Name, byte[] Data);

  /// <summary>One generic PE resource as surfaced by <see cref="ReadAll"/>.
  /// <paramref name="TypeId"/> is the RT_* numeric type (or a string type, in which
  /// case <paramref name="TypeName"/> is non-null). <paramref name="NameId"/> is
  /// the numeric id (0 when the resource has a string name, in which case
  /// <paramref name="NameString"/> is non-null).</summary>
  public sealed record RawResource(
    ushort TypeId, string? TypeName,
    ushort NameId, string? NameString,
    ushort LanguageId,
    byte[] Data);

  public List<Entry> Read(Stream stream) =>
    ReadAll(stream)
      .Where(r => r.TypeId == RtRcData && r.NameString != null)
      .Select(r => new Entry(r.NameString!, r.Data))
      .ToList();

  public List<RawResource> ReadAll(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));

    var origin = stream.Position;
    Span<byte> u32 = stackalloc byte[4];

    stream.Seek(origin + 0x3C, SeekOrigin.Begin);
    stream.ReadExactly(u32);
    var peOff = origin + BinaryPrimitives.ReadUInt32LittleEndian(u32);

    stream.Seek(peOff, SeekOrigin.Begin);
    Span<byte> sigBuf = stackalloc byte[4];
    stream.ReadExactly(sigBuf);
    if (sigBuf[0] != 'P' || sigBuf[1] != 'E')
      throw new InvalidDataException("Not a PE file (missing 'PE' signature).");

    Span<byte> coff = stackalloc byte[20];
    stream.ReadExactly(coff);
    var numSections = BinaryPrimitives.ReadUInt16LittleEndian(coff[2..]);
    var optHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(coff[16..]);

    Span<byte> optMagicBuf = stackalloc byte[2];
    stream.ReadExactly(optMagicBuf);
    var pe32Plus = BinaryPrimitives.ReadUInt16LittleEndian(optMagicBuf) == 0x020B;

    // Data Directories start at byte 96 (PE32) or 112 (PE32+) within the optional header.
    // Resource Table is data-directory index 2 (after [0]=Export, [1]=Import), each 8 bytes.
    var dataDirsOff = pe32Plus ? 112 : 96;
    stream.Seek(peOff + 4 + 20 + dataDirsOff + 2 * 8, SeekOrigin.Begin);
    Span<byte> rsrcDir = stackalloc byte[8];
    stream.ReadExactly(rsrcDir);
    var rsrcRva = BinaryPrimitives.ReadUInt32LittleEndian(rsrcDir[..4]);
    var rsrcSize = BinaryPrimitives.ReadUInt32LittleEndian(rsrcDir[4..]);
    if (rsrcRva == 0 || rsrcSize == 0)
      return [];

    stream.Seek(peOff + 4 + 20 + optHeaderSize, SeekOrigin.Begin);
    var sections = new PeSection[numSections];
    Span<byte> secBuf = stackalloc byte[40];
    for (var i = 0; i < numSections; i++) {
      stream.ReadExactly(secBuf);
      sections[i] = new PeSection(
        BinaryPrimitives.ReadUInt32LittleEndian(secBuf[12..]),
        BinaryPrimitives.ReadUInt32LittleEndian(secBuf[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(secBuf[20..]),
        BinaryPrimitives.ReadUInt32LittleEndian(secBuf[16..]));
    }

    var rsrcFileOff = RvaToFileOffset(sections, origin, rsrcRva);
    var rsrc = new byte[rsrcSize];
    stream.Seek(rsrcFileOff, SeekOrigin.Begin);
    stream.ReadExactly(rsrc, 0, rsrc.Length);

    return WalkResourceTree(rsrc, rsrcRva);
  }

  private readonly record struct PeSection(uint Va, uint VSize, uint Raw, uint RawSize) {
    public bool Contains(uint rva) => rva >= this.Va && rva < this.Va + Math.Max(this.VSize, this.RawSize);
  }

  private static long RvaToFileOffset(PeSection[] sections, long origin, uint rva) {
    foreach (var s in sections)
      if (s.Contains(rva))
        return origin + s.Raw + (rva - s.Va);
    throw new InvalidDataException($"RVA 0x{rva:X8} is outside any section.");
  }

  private static List<RawResource> WalkResourceTree(byte[] rsrc, uint rsrcRva) {
    var entries = new List<RawResource>();
    var rootCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.AsSpan(12)) +
                    BinaryPrimitives.ReadUInt16LittleEndian(rsrc.AsSpan(14));

    for (var i = 0; i < rootCount; i++) {
      var typeEntryOff = DirHeaderSize + i * DirEntrySize;
      var typeIdField = BinaryPrimitives.ReadUInt32LittleEndian(rsrc.AsSpan(typeEntryOff));
      var typeChild = BinaryPrimitives.ReadUInt32LittleEndian(rsrc.AsSpan(typeEntryOff + 4));
      if ((typeChild & HighBitFlag) == 0) continue;
      var typeDirOff = (int)(typeChild & ~HighBitFlag);

      ushort typeId;
      string? typeName;
      if ((typeIdField & HighBitFlag) != 0) {
        typeName = ReadUtf16String(rsrc, (int)(typeIdField & ~HighBitFlag));
        typeId = 0;
      } else {
        typeId = (ushort)typeIdField;
        typeName = null;
      }

      var typeChildren = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.AsSpan(typeDirOff + 12)) +
                         BinaryPrimitives.ReadUInt16LittleEndian(rsrc.AsSpan(typeDirOff + 14));

      for (var j = 0; j < typeChildren; j++) {
        var nameEntryOff = typeDirOff + DirHeaderSize + j * DirEntrySize;
        var nameIdField = BinaryPrimitives.ReadUInt32LittleEndian(rsrc.AsSpan(nameEntryOff));
        var nameChild = BinaryPrimitives.ReadUInt32LittleEndian(rsrc.AsSpan(nameEntryOff + 4));
        if ((nameChild & HighBitFlag) == 0) continue;
        var langDirOff = (int)(nameChild & ~HighBitFlag);

        ushort nameId;
        string? nameString;
        if ((nameIdField & HighBitFlag) != 0) {
          nameString = ReadUtf16String(rsrc, (int)(nameIdField & ~HighBitFlag));
          nameId = 0;
        } else {
          nameId = (ushort)nameIdField;
          nameString = null;
        }

        // Expose only the first language entry.
        var langChildCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.AsSpan(langDirOff + 12)) +
                             BinaryPrimitives.ReadUInt16LittleEndian(rsrc.AsSpan(langDirOff + 14));
        if (langChildCount == 0) continue;
        var langEntryOff = langDirOff + DirHeaderSize;
        var langIdField = BinaryPrimitives.ReadUInt32LittleEndian(rsrc.AsSpan(langEntryOff));
        var langChild = BinaryPrimitives.ReadUInt32LittleEndian(rsrc.AsSpan(langEntryOff + 4));
        if ((langChild & HighBitFlag) != 0) continue;
        var dataEntryOff = (int)langChild;
        var languageId = (ushort)langIdField;

        var dataRva = BinaryPrimitives.ReadUInt32LittleEndian(rsrc.AsSpan(dataEntryOff));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(rsrc.AsSpan(dataEntryOff + 4));
        if (dataRva < rsrcRva)
          throw new InvalidDataException($"Resource data RVA 0x{dataRva:X8} precedes .rsrc base 0x{rsrcRva:X8}.");
        var dataOffsetInSection = (int)(dataRva - rsrcRva);
        if (dataOffsetInSection + (long)dataSize > rsrc.Length)
          throw new InvalidDataException("Resource data extends past .rsrc section.");
        var blob = rsrc.AsSpan(dataOffsetInSection, (int)dataSize).ToArray();

        entries.Add(new RawResource(typeId, typeName, nameId, nameString, languageId, blob));
      }
    }

    return entries;
  }

  private static string ReadUtf16String(byte[] rsrc, int stringOff) {
    if (stringOff + 2 > rsrc.Length)
      throw new InvalidDataException("Resource name string offset extends past .rsrc section.");
    var len = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.AsSpan(stringOff));
    var byteLen = 2 * len;
    if (stringOff + 2 + byteLen > rsrc.Length)
      throw new InvalidDataException("Resource name extends past .rsrc section.");
    return Encoding.Unicode.GetString(rsrc, stringOff + 2, byteLen);
  }
}
