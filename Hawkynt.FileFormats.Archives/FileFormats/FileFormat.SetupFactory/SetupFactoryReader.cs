using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.Dictionary.PkwareDcl;
using FileFormat.PeResources;

namespace FileFormat.SetupFactory;

/// <summary>How one Setup Factory member is stored.</summary>
public enum SetupFactoryCompression {
  /// <summary>Stored as is.</summary>
  None,
  /// <summary>PKWARE Data Compression Library (implode).</summary>
  Pkware,
  /// <summary>LZMA: 5 property bytes, the 64-bit unpacked size, the raw stream.</summary>
  Lzma,
  /// <summary>LZMA2: 1 property byte, the 64-bit unpacked size, the raw stream.</summary>
  Lzma2,
}

/// <summary>One file of a Setup Factory installer.</summary>
/// <param name="Path">Name: the installer's own files by their stored name, installed files as destination folder and file name, '/'-separated.</param>
/// <param name="Offset">Offset of the member in the installer.</param>
/// <param name="StoredSize">Stored size of the member.</param>
/// <param name="Size">Unpacked size, -1 when only decoding tells.</param>
/// <param name="Compression">How the member is stored.</param>
/// <param name="Crc">CRC-32 of the unpacked file, 0 when none is recorded.</param>
/// <param name="Xored">The first 2000 bytes are XOR-ed with 0x07 (the embedded runtime, <c>irsetup.exe</c>).</param>
public sealed record SetupFactoryEntry(string Path, long Offset, long StoredSize, long Size, SetupFactoryCompression Compression, uint Crc, bool Xored);

/// <summary>
/// Setup Factory installers, versions 5 to 9 (Indigo Rose): a stub executable followed by a
/// signature, the runtime <c>irsetup.exe</c>, the installer's own files (among them the script
/// <c>irsetup.dat</c>) and the installed files the script's <c>CFileInfo</c> /
/// <c>CSetupFileData</c> block lists, each PKWARE DCL, LZMA or LZMA2 compressed or stored.
/// </summary>
/// <remarks>
/// Converted from the <c>sfact</c> module of Observer (lazyhamster, LGPL-3.0-or-later), revision
/// <c>ddbe6b6d</c>; see THIRD-PARTY-NOTICE.Observer.txt. Deviations: the version 7 runtime is
/// found with or without the pad byte before its size (by the XOR-ed <c>MZ</c> it starts with),
/// where Observer always skips one byte; a version 9 setup whose manifest names no version is read
/// with the script layout that parses; a member marked compressed whose stored size is its size and
/// whose bytes match its CRC-32 is taken as stored (the builder keeps files it cannot shrink); and
/// every member with a recorded CRC-32 is checked, the installer's own files included.
/// </remarks>
public sealed class SetupFactoryReader {
  private static ReadOnlySpan<byte> Signature7 => [0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7];
  private static ReadOnlySpan<byte> Signature8 => [0xE0, 0xE0, 0xE1, 0xE1, 0xE2, 0xE2, 0xE3, 0xE3, 0xE4, 0xE4, 0xE5, 0xE5, 0xE6, 0xE6, 0xE7, 0xE7];

  /// <summary>Where versions 5 and 6 keep their signature (Observer's list).</summary>
  private static readonly (int Offset, int Version)[] FixedOffsets = [(0x8000, 5), (0x12000, 6), (0x15000, 6), (0x1A000, 6)];

  private const string ScriptName = "irsetup.dat";
  private const int XoredLength = 2000;
  private const byte XorKey = 0x07;

  private static readonly Encoding Ansi = CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;

  private readonly byte[] _data;

  private SetupFactoryReader(byte[] data, int version, int minor, List<SetupFactoryEntry> entries) {
    this._data = data;
    this.Version = version;
    this.MinorVersion = minor;
    this.Entries = entries;
  }

  /// <summary>Major version of the installer format (5 to 9).</summary>
  public int Version { get; }

  /// <summary>Minor version, where the format depends on it (version 9).</summary>
  public int MinorVersion { get; }

  /// <summary>The runtime, the installer's own files and the installed files.</summary>
  public IReadOnlyList<SetupFactoryEntry> Entries { get; }

  /// <summary>Whether the stream carries a Setup Factory signature where a version 5 to 9 installer keeps it.</summary>
  public static bool IsSetupFactory(Stream stream) {
    try {
      foreach (var (offset, _) in FixedOffsets)
        if (Matches(stream, offset, Signature7) && SmallCount(stream, offset + Signature7.Length))
          return true;
      var overlay = OverlayStart(stream);
      if (overlay <= 0)
        return false;
      return Matches(stream, overlay, Signature8) || (Matches(stream, overlay, Signature7) && !SmallCount(stream, overlay + Signature7.Length));
    } catch (Exception e) when (e is IOException or EndOfStreamException or ArgumentException) {
      return false;
    }
  }

  private static bool Matches(Stream stream, long offset, ReadOnlySpan<byte> signature) {
    if (offset < 0 || offset + signature.Length > stream.Length)
      return false;
    Span<byte> buffer = stackalloc byte[16];
    stream.Position = offset;
    stream.ReadExactly(buffer[..signature.Length]);
    return buffer[..signature.Length].SequenceEqual(signature);
  }

  /// <summary>Versions 5 and 6 follow the signature with a file count, version 7 with the runtime's size.</summary>
  private static bool SmallCount(Stream stream, long offset) {
    if (offset + 4 > stream.Length)
      return false;
    Span<byte> b = stackalloc byte[4];
    stream.Position = offset;
    stream.ReadExactly(b);
    return BinaryPrimitives.ReadUInt32LittleEndian(b) <= 100;
  }

  /// <summary>Opens a Setup Factory installer held in memory.</summary>
  /// <exception cref="InvalidDataException">No signature, or the installer's own files do not decode.</exception>
  /// <exception cref="NotSupportedException">A version 8 or 9 setup whose runtime version cannot be told.</exception>
  public static SetupFactoryReader Open(byte[] data) {
    using var stream = new MemoryStream(data, writable: false);
    foreach (var (offset, version) in FixedOffsets)
      if (Matches(stream, offset, Signature7) && SmallCount(stream, offset + Signature7.Length))
        return OpenVersion56(data, offset + Signature7.Length, version);

    var overlay = OverlayStart(stream);
    if (overlay > 0 && Matches(stream, overlay, Signature8))
      return OpenVersion89(data, stream, overlay + Signature8.Length);
    if (overlay > 0 && Matches(stream, overlay, Signature7))
      return OpenVersion7(data, overlay + Signature7.Length);
    throw new InvalidDataException("no Setup Factory signature");
  }

  // ---- versions 5 and 6 -------------------------------------------------------------------

  private static SetupFactoryReader OpenVersion56(byte[] data, long p, int version) {
    var entries = new List<SetupFactoryEntry>();
    var count = U32(data, ref p);
    var script = ReadOwnFiles(data, ref p, count, version == 5 ? 16 : 260, wideSize: false, entries, SetupFactoryCompression.Pkware);
    if (script != null)
      ParseFileInfo(script, p, version, entries);
    return new(data, version, 0, entries);
  }

  /// <summary>The version 5/6 script: a <c>CFileInfo</c> block at its start.</summary>
  private static void ParseFileInfo(byte[] script, long contentBase, int version, List<SetupFactoryEntry> entries) {
    var s = new Cursor(script, 0);
    var count = s.U16();
    s.Skip(4);
    var className = s.ClassName();
    if (className != "CFileInfo")
      throw new InvalidDataException($"the script's file block is {className}, not CFileInfo");
    var next = contentBase;
    for (var i = 0; i < count; ++i) {
      if (version == 6)
        s.Skip(4);
      s.SkipString(); // full source path
      var baseName = s.String();
      s.SkipString(); // source directory
      s.SkipString(); // suffix
      s.Skip(1);
      var size = s.U32();
      s.Skip(1 + 4 + 4 + 4 + 25); // attributes, creation time, unknown, modification time
      var folder = s.String();
      s.Skip(5);
      s.SkipString(); // shortcut description
      s.SkipString(); // shortcut arguments
      s.SkipString(); // shortcut working folder
      s.Skip(1);
      s.SkipString(); // icon path
      s.Skip(5);
      s.SkipString(); // package name
      s.Skip(1);
      s.SkipString(); // font name
      s.Skip(3);
      var compressed = s.U8() != 0;
      s.Skip(2); // attribute choice, forced attributes
      if (version == 5)
        s.Skip(15);
      else {
        s.Skip(6);
        s.SkipString();
        s.Skip(2);
      }
      var stored = s.U32();
      var crc = s.U32();
      s.Skip(39);
      entries.Add(new(Join(folder, baseName), next, stored, size, compressed ? SetupFactoryCompression.Pkware : SetupFactoryCompression.None, crc, false));
      next += stored;
    }
  }

  // ---- version 7 --------------------------------------------------------------------------

  private static SetupFactoryReader OpenVersion7(byte[] data, long p) {
    var entries = new List<SetupFactoryEntry>();
    // The runtime's size follows the signature, after a pad byte in some installers: whichever
    // reading puts the XOR-ed "MZ" right after the size is the one.
    long runtime = -1;
    for (var pad = 0; pad <= 1 && runtime < 0; ++pad) {
      var at = p + pad;
      if (at + 6 > data.Length)
        break;
      var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)at));
      if (at + 4 + size <= data.Length && data[at + 4] == ('M' ^ XorKey) && data[at + 5] == ('Z' ^ XorKey))
        runtime = at;
    }
    if (runtime < 0)
      throw new InvalidDataException("no runtime executable after the version 7 signature");
    p = runtime;
    entries.Add(Special(data, ref p, "irsetup.exe", wideSize: false, xored: true));
    var count = U32(data, ref p);
    var script = ReadOwnFiles(data, ref p, count, 260, wideSize: false, entries, SetupFactoryCompression.Pkware);
    if (script != null)
      ParseSetupFileData(script, p, 7, 0, SetupFactoryCompression.Pkware, entries);
    return new(data, 7, 0, entries);
  }

  // ---- versions 8 and 9 -------------------------------------------------------------------

  private static SetupFactoryReader OpenVersion89(byte[] data, Stream stream, long p) {
    var (version, minor) = RuntimeVersion(stream);
    var entries = new List<SetupFactoryEntry>();
    p += 10;
    entries.Add(Special(data, ref p, "irsetup.exe", wideSize: true, xored: true));
    var count = U32(data, ref p);
    if (count > 1000) {
      // Not a count: the size of the Lua runtime the setup carries next.
      p -= 4;
      entries.Add(Special(data, ref p, "lua5.1.dll", wideSize: true, xored: false));
      count = U32(data, ref p);
    }

    var compression = count == 0 ? SetupFactoryCompression.Pkware : DetectCompression(data, p + 264 + 8 + 4 + 4);
    var script = ReadOwnFiles(data, ref p, count, 264, wideSize: true, entries, compression);
    if (script == null)
      return new(data, version ?? 9, minor ?? 0, entries);

    if (version is { } v)
      ParseSetupFileData(script, p, v, minor!.Value, compression, entries);
    else {
      // The manifest names no version: read the layout that parses and whose members fill the data.
      var parsed = false;
      foreach (var m in (int[])[3, 0]) {
        var attempt = new List<SetupFactoryEntry>();
        try {
          ParseSetupFileData(script, p, 9, m, compression, attempt);
        } catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException) {
          continue;
        }
        if (attempt.Count == 0 || attempt[^1].Offset + attempt[^1].StoredSize > data.Length)
          continue;
        entries.AddRange(attempt);
        minor = m;
        parsed = true;
        break;
      }
      if (!parsed)
        throw new NotSupportedException("the runtime's version is not recorded and no script layout fits");
    }
    return new(data, version ?? 9, minor ?? 0, entries);
  }

  private static SetupFactoryCompression DetectCompression(byte[] data, long at) {
    if (at + 2 > data.Length)
      throw new InvalidDataException("the installer ends inside its file table");
    return (data[at], data[at + 1]) switch {
      (0x00, 0x06) => SetupFactoryCompression.Pkware,
      (0x5D, 0x00) => SetupFactoryCompression.Lzma,
      (0x18, _) => SetupFactoryCompression.Lzma2,
      _ => throw new InvalidDataException("the installer's own files use an unknown compression"),
    };
  }

  /// <summary>
  /// The runtime's version from its manifest: "Setup Factory 8.0 Run-time" is 8.0; "Setup
  /// Factory 9 Run-time" and "Setup Factory Run-time" (9.5 on) carry it in the assembly identity.
  /// Null when the manifest is a plain "Setup" one.
  /// </summary>
  private static (int? Major, int? Minor) RuntimeVersion(Stream stream) {
    var manifest = "";
    if (PeResourceLocator.Find(stream, 24, 1) is { } at && at.Offset + at.Size <= stream.Length) {
      var bytes = new byte[at.Size];
      stream.Position = at.Offset;
      stream.ReadExactly(bytes);
      manifest = Encoding.UTF8.GetString(bytes);
    }
    if (manifest.Contains("<description>Setup Factory 8.0 Run-time</description>", StringComparison.Ordinal))
      return (8, 0);
    if (manifest.Contains("<description>Setup Factory 9 Run-time</description>", StringComparison.Ordinal) ||
        manifest.Contains("<description>Setup Factory Run-time</description>", StringComparison.Ordinal)) {
      var identity = manifest.IndexOf("<assemblyIdentity", StringComparison.Ordinal);
      var version = identity < 0 ? -1 : manifest.IndexOf("version=", identity, StringComparison.Ordinal);
      if (version >= 0) {
        var digits = manifest[(version + 9)..].Split('.', 3);
        if (digits.Length >= 2 && int.TryParse(digits[0], out var major) && int.TryParse(digits[1], out var minor) && major == 9)
          return (9, minor);
      }
      throw new NotSupportedException("a Setup Factory runtime manifest without a version 9 assembly identity");
    }
    return (null, null);
  }

  // ---- shared -----------------------------------------------------------------------------

  /// <summary>The <c>CSetupFileData</c> block of a version 7 to 9 script.</summary>
  private static void ParseSetupFileData(byte[] script, long contentBase, int version, int minor, SetupFactoryCompression compression, List<SetupFactoryEntry> entries) {
    var block = IndexOf(script, "CSetupFileData"u8);
    if (block < 8)
      return;
    var s = new Cursor(script, block - 8);
    var count = s.U16();
    s.Skip(4);
    var className = s.ClassName();
    if (className != "CSetupFileData")
      throw new InvalidDataException($"the script's file block is {className}, not CSetupFileData");
    var wide = version >= 8;
    var next = contentBase;
    s.Skip(5);
    for (var i = 0; i < count; ++i) {
      s.SkipString(); // full source path
      var baseName = s.String();
      s.SkipString(); // source directory
      s.SkipString(); // suffix
      s.SkipString(); // run-time folder
      s.SkipString(); // description
      s.Skip(2);
      var size = wide ? (long)s.U64() : s.U32();
      s.Skip(1); // attributes
      s.Skip(wide ? 4 + 8 + 16 + 8 + 25 : 37); // creation and modification times
      var folder = s.String();
      s.Skip(version == 9 && minor >= 3 ? 11 : 10);
      for (var k = 0; k < 5; ++k)
        s.SkipString(); // shortcut location, comment, description, arguments, start folder
      s.Skip(1);
      s.SkipString(); // icon path
      s.Skip(8);
      s.SkipString(); // font registry name
      s.Skip(3);
      var compressed = s.U8() != 0;
      s.Skip(2); // attribute choice, forced attributes
      s.Skip(10);
      var skip = s.U16();
      s.Skip(skip * 2);
      s.SkipString(); // condition
      s.Skip(2);
      s.SkipString(); // install type
      s.SkipString();
      var packages = s.U16();
      for (var k = 0; k < packages; ++k)
        s.SkipString();
      s.SkipString(); // notes
      var stored = wide ? (long)s.U64() : s.U32();
      var crc = s.U32();
      s.Skip(8);
      entries.Add(new(Join(folder, baseName), next, stored, size, compressed ? compression : SetupFactoryCompression.None, crc, false));
      next += stored;
    }
  }

  /// <summary>The installer's own files: a fixed-size name, the stored size and the CRC-32, then the member.</summary>
  private static byte[]? ReadOwnFiles(byte[] data, ref long p, uint count, int nameSize, bool wideSize, List<SetupFactoryEntry> entries, SetupFactoryCompression compression) {
    if (count > 10000)
      throw new InvalidDataException("implausible count of the installer's own files");
    byte[]? script = null;
    for (var i = 0; i < count; ++i) {
      if (p + nameSize + (wideSize ? 16 : 8) > data.Length)
        throw new InvalidDataException("the installer ends inside its file table");
      var raw = data.AsSpan((int)p, nameSize);
      var end = raw.IndexOf((byte)0);
      var name = Ansi.GetString(end < 0 ? raw : raw[..end]);
      p += nameSize;
      var stored = wideSize ? (long)U64(data, ref p) : U32(data, ref p);
      var crc = U32(data, ref p);
      if (wideSize)
        p += 4;
      if (stored < 0 || p + stored > data.Length)
        throw new InvalidDataException($"{name} runs past the end of the installer");
      var entry = new SetupFactoryEntry(name, p, stored, -1, compression, crc, false);
      var content = Decode(data, entry);
      entries.Add(entry with { Size = content.LongLength });
      if (name == ScriptName)
        script = content;
      p += stored;
    }
    return script;
  }

  private static SetupFactoryEntry Special(byte[] data, ref long p, string name, bool wideSize, bool xored) {
    var size = wideSize ? (long)U64(data, ref p) : U32(data, ref p);
    if (size < 0 || p + size > data.Length)
      throw new InvalidDataException($"{name} runs past the end of the installer");
    var entry = new SetupFactoryEntry(name, p, size, size, SetupFactoryCompression.None, 0, xored);
    p += size;
    return entry;
  }

  /// <summary>Unpacks one member and checks its size and CRC-32.</summary>
  /// <exception cref="InvalidDataException">The member does not decode, or not to its recorded size and CRC-32.</exception>
  public byte[] Extract(SetupFactoryEntry entry) => Decode(this._data, entry);

  private static byte[] Decode(byte[] data, SetupFactoryEntry entry) {
    if (entry.Offset < 0 || entry.Offset + entry.StoredSize > data.Length)
      throw new InvalidDataException($"{entry.Path} lies beyond the end of the installer");
    var packed = data.AsSpan((int)entry.Offset, (int)entry.StoredSize);
    // A file the builder could not shrink is kept as is although its record says compressed:
    // its stored size is then its size, and its recorded CRC-32 tells which reading is right.
    if (entry.Compression != SetupFactoryCompression.None && entry.Crc != 0 && entry.StoredSize == entry.Size &&
        Crc32.Compute(packed) == entry.Crc)
      return Decode(data, entry with { Compression = SetupFactoryCompression.None });
    byte[] content;
    if (packed.IsEmpty)
      content = [];
    else
      content = entry.Compression switch {
        SetupFactoryCompression.None => packed.ToArray(),
        SetupFactoryCompression.Pkware => PkwareDclDecoder.Decompress(packed),
        SetupFactoryCompression.Lzma => DecodeLzma(packed),
        SetupFactoryCompression.Lzma2 => DecodeLzma2(packed),
        _ => throw new InvalidDataException($"{entry.Path}: unknown compression"),
      };
    if (entry.Size >= 0 && content.LongLength != entry.Size)
      throw new InvalidDataException($"{entry.Path} unpacks to {content.LongLength} bytes, not {entry.Size}");
    if (entry.Crc != 0 && Crc32.Compute(content) != entry.Crc)
      throw new InvalidDataException($"{entry.Path} does not match its CRC-32");
    if (entry.Xored)
      for (var i = 0; i < Math.Min(XoredLength, content.Length); ++i)
        content[i] ^= XorKey;
    return content;
  }

  private static byte[] DecodeLzma(ReadOnlySpan<byte> packed) {
    if (packed.Length < 13)
      throw new InvalidDataException("LZMA member shorter than its header");
    var size = BinaryPrimitives.ReadInt64LittleEndian(packed[5..]);
    using var input = new MemoryStream(packed[13..].ToArray());
    return new LzmaDecoder(input, packed[..5].ToArray(), size).Decode();
  }

  private static byte[] DecodeLzma2(ReadOnlySpan<byte> packed) {
    if (packed.Length < 9)
      throw new InvalidDataException("LZMA2 member shorter than its header");
    int property = packed[0];
    if (property > 40)
      throw new InvalidDataException("LZMA2 dictionary property out of range");
    var dictionary = property == 40 ? int.MaxValue : (int)Math.Min(int.MaxValue, (2L | (property & 1)) << (property / 2 + 11));
    using var input = new MemoryStream(packed[9..].ToArray());
    return new Lzma2Decoder(input, dictionary).Decode();
  }

  private static string Join(string folder, string name) {
    var path = folder.Length == 0 ? name : folder.TrimEnd('\\') + "\\" + name;
    return string.Join('/', path.Split('\\', '/').Where(s => s.Length > 0 && s != "." && s != ".."));
  }

  private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);

  private static uint U32(byte[] data, ref long p) {
    if (p + 4 > data.Length)
      throw new InvalidDataException("the installer ends inside its header");
    var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)p));
    p += 4;
    return v;
  }

  private static ulong U64(byte[] data, ref long p) {
    if (p + 8 > data.Length)
      throw new InvalidDataException("the installer ends inside its header");
    var v = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)p));
    p += 8;
    return v;
  }

  /// <summary>End of the PE image's section data: where the overlay starts.</summary>
  private static long OverlayStart(Stream stream) {
    Span<byte> b = stackalloc byte[64];
    if (stream.Length < 64)
      return -1;
    stream.Position = 0;
    stream.ReadExactly(b);
    if (b[0] != 'M' || b[1] != 'Z')
      return -1;
    long pe = BinaryPrimitives.ReadUInt32LittleEndian(b[0x3C..]);
    if (pe + 24 > stream.Length)
      return -1;
    Span<byte> h = stackalloc byte[24];
    stream.Position = pe;
    stream.ReadExactly(h);
    if (h[0] != 'P' || h[1] != 'E' || h[2] != 0 || h[3] != 0)
      return -1;
    int sections = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
    int optional = BinaryPrimitives.ReadUInt16LittleEndian(h[20..]);
    long end = 0;
    Span<byte> s = stackalloc byte[40];
    for (var i = 0; i < sections; ++i) {
      var at = pe + 24 + optional + 40L * i;
      if (at + 40 > stream.Length)
        return -1;
      stream.Position = at;
      stream.ReadExactly(s);
      end = Math.Max(end, BinaryPrimitives.ReadUInt32LittleEndian(s[20..]) + (long)BinaryPrimitives.ReadUInt32LittleEndian(s[16..]));
    }
    return end;
  }

  /// <summary>Reads the script's length-prefixed strings and fixed fields; any overrun is an error.</summary>
  private sealed class Cursor(byte[] data, int position) {
    private int _p = position;

    public void Skip(int n) {
      if (n < 0 || this._p + n > data.Length)
        throw new InvalidDataException("the script ends inside a file record");
      this._p += n;
    }

    public byte U8() {
      this.Skip(1);
      return data[this._p - 1];
    }

    public ushort U16() {
      this.Skip(2);
      return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(this._p - 2));
    }

    public uint U32() {
      this.Skip(4);
      return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(this._p - 4));
    }

    public ulong U64() {
      this.Skip(8);
      return BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(this._p - 8));
    }

    public void SkipString() => this.Skip(this.U8());

    public string String() {
      int length = this.U8();
      this.Skip(length);
      var raw = data.AsSpan(this._p - length, length);
      var nul = raw.IndexOf((byte)0);
      return Ansi.GetString(nul < 0 ? raw : raw[..nul]);
    }

    public string ClassName() {
      int length = this.U16();
      this.Skip(length);
      var raw = data.AsSpan(this._p - length, Math.Min(length, 127));
      var nul = raw.IndexOf((byte)0);
      return Encoding.ASCII.GetString(nul < 0 ? raw : raw[..nul]);
    }
  }
}
