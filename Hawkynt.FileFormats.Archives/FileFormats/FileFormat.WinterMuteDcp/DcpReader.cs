using System.Buffers.Binary;
using System.Text;
using FileFormat.Zlib;

namespace FileFormat.WinterMuteDcp;

/// <summary>One file of a WinterMute package.</summary>
/// <param name="Name">Path inside the package, '/'-separated (the package stores backslashes).</param>
/// <param name="Offset">Absolute offset of its stored bytes.</param>
/// <param name="Length">Decoded length.</param>
/// <param name="CompressedLength">Stored zlib length, or 0 when the file is stored uncompressed.</param>
/// <param name="Flags">Entry flags (the packagers write 0).</param>
/// <param name="TimeDate1">v2: the source file's modification time (32-bit Unix time).</param>
/// <param name="TimeDate2">v2: unused, written as 0.</param>
public sealed record DcpEntry(string Name, uint Offset, uint Length, uint CompressedLength, uint Flags, uint TimeDate1, uint TimeDate2) {
  /// <summary>Whether the stored bytes are a zlib stream.</summary>
  public bool IsCompressed => this.CompressedLength != 0;

  /// <summary>Bytes the file occupies in the package.</summary>
  public uint StoredLength => this.IsCompressed ? this.CompressedLength : this.Length;
}

/// <summary>One directory of a package: a package name, the CD it lives on and its files.</summary>
public sealed record DcpDirectory(string Name, byte Cd, IReadOnlyList<DcpEntry> Entries);

/// <summary>
/// Reads a WinterMute Engine (WME 1.x) resource package (<c>.dcp</c>).
/// </summary>
/// <remarks>
/// <para>Layout (little-endian), per WME's <c>dcpackage.h</c> and <c>CBFileManager::RegisterPackage</c>:
/// a 128-byte header (UInt32 0xDEC0ADDE, "JUNK", UInt32 package version 0x100 or 0x200, UInt32 game
/// version, Byte priority, Byte CD, Byte master-index flag, one pad byte, Int32 creation time, 100-byte
/// description, UInt32 directory count). Version 0x200 follows it with a UInt32 directory offset;
/// version 0x100 puts the directories right after the header. Each directory: Byte length + name
/// (NUL included), Byte CD, UInt32 file count, then per file Byte length + name (NUL included; XORed
/// with 'D' in 0x200), UInt32 offset, length, compressed length (0 = stored) and flags, and in 0x200
/// two UInt32 timestamps. A compressed file is a zlib stream.</para>
/// <para>A directory names the package file that holds its data (<c>&lt;name&gt;.dcp</c>): an ordinary
/// package has one directory naming itself, a master index lists several packages' files without
/// holding their data.</para>
/// <para>Converted from WME 1.x's <c>engine_core/wme_base/BFileManager.cpp</c> and <c>dcpackage.h</c>
/// (MIT, Jan Nedoma); see THIRD-PARTY-NOTICE.WinterMute.txt.</para>
/// </remarks>
public sealed class DcpReader {

  /// <summary>First magic word, 0xDEC0ADDE.</summary>
  public const uint Magic1 = 0xDEC0ADDE;

  /// <summary>Second magic word, "JUNK".</summary>
  public const uint Magic2 = 0x4B4E554A;

  /// <summary>The original package version.</summary>
  public const uint Version1 = 0x100;

  /// <summary>The package version of WME 1.x's ProjectMan (directory offset, XORed names, timestamps).</summary>
  public const uint Version2 = 0x200;

  /// <summary>Size of the fixed header.</summary>
  public const int HeaderSize = 0x80;

  /// <summary>Entry names are XORed with this byte in version 0x200.</summary>
  public const byte NameKey = (byte)'D';

  /// <summary>The single-byte code page names are stored in (WME uses the ANSI code page; Latin-1 keeps every byte).</summary>
  internal static readonly Encoding NameEncoding = Encoding.Latin1;

  private readonly Stream _stream;

  /// <summary>Parses the header and every directory.</summary>
  /// <exception cref="InvalidDataException">The stream is not a well-formed WinterMute package.</exception>
  public DcpReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    var length = stream.Length;
    if (length < HeaderSize)
      throw new InvalidDataException("Too short for a WinterMute package.");

    var header = new byte[HeaderSize];
    stream.Position = 0;
    stream.ReadExactly(header);
    if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic1 || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != Magic2)
      throw new InvalidDataException("Not a WinterMute package (missing DEADC0DE JUNK magic).");
    this.Version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
    if (this.Version is not (Version1 or Version2))
      throw new InvalidDataException($"WinterMute package version 0x{this.Version:X} is not 0x100 or 0x200.");
    this.GameVersion = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
    this.Priority = header[16];
    this.Cd = header[17];
    this.IsMasterIndex = header[18] != 0;
    this.Pad = header[19];
    this.CreationTime = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
    var desc = header.AsSpan(24, 100);
    var nul = desc.IndexOf((byte)0);
    this.Description = NameEncoding.GetString(nul < 0 ? desc : desc[..nul]);
    var dirCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(124));

    long dirOffset = HeaderSize;
    if (this.Version == Version2) {
      dirOffset = this.ReadUInt32();
      if (dirOffset < HeaderSize + 4 || dirOffset >= length)
        throw new InvalidDataException($"WinterMute directory offset {dirOffset} lies outside the package.");
    }

    stream.Position = dirOffset;
    var dirs = new List<DcpDirectory>();
    for (var d = 0u; d < dirCount; ++d) {
      var name = this.ReadName(xor: false);
      var cd = this.ReadByte();
      var count = this.ReadUInt32();
      if (count > (length - stream.Position) / 17)
        throw new InvalidDataException("WinterMute directory claims more files than the package can hold.");
      var entries = new List<DcpEntry>((int)count);
      for (var i = 0u; i < count; ++i) {
        var fileName = this.ReadName(xor: this.Version == Version2).Replace('\\', '/');
        var offset = this.ReadUInt32();
        var len = this.ReadUInt32();
        var comp = this.ReadUInt32();
        var flags = this.ReadUInt32();
        uint t1 = 0, t2 = 0;
        if (this.Version == Version2) {
          t1 = this.ReadUInt32();
          t2 = this.ReadUInt32();
        }
        entries.Add(new DcpEntry(fileName, offset, len, comp, flags, t1, t2));
      }
      dirs.Add(new DcpDirectory(name, cd, entries));
    }
    this.Directories = dirs;
    this.HoldsItsOwnData = !this.IsMasterIndex && dirs.Count == 1;
    if (this.HoldsItsOwnData)
      foreach (var e in dirs[0].Entries)
        if (e.Offset < HeaderSize || (ulong)e.Offset + e.StoredLength > (ulong)length)
          throw new InvalidDataException($"WinterMute file '{e.Name}' points outside the package.");
  }

  /// <summary>0x100 or 0x200.</summary>
  public uint Version { get; }

  /// <summary>The game version the package was built for.</summary>
  public uint GameVersion { get; }

  /// <summary>Package priority (patches use higher values).</summary>
  public byte Priority { get; }

  /// <summary>The CD the package belongs to (0 = hard disk).</summary>
  public byte Cd { get; }

  /// <summary>Whether this is a master index listing other packages' files.</summary>
  public bool IsMasterIndex { get; }

  /// <summary>The header's pad byte (0 from ProjectMan, 0xFF from the 2003 PackMaker).</summary>
  public byte Pad { get; }

  /// <summary>Creation time (32-bit Unix time).</summary>
  public uint CreationTime { get; }

  /// <summary>The package description.</summary>
  public string Description { get; }

  /// <summary>Directories in file order.</summary>
  public IReadOnlyList<DcpDirectory> Directories { get; }

  /// <summary>Whether the files' data are in this file (one directory, not a master index).</summary>
  public bool HoldsItsOwnData { get; }

  /// <summary>Every file of every directory.</summary>
  public IEnumerable<DcpEntry> Entries => this.Directories.SelectMany(d => d.Entries);

  /// <summary>Returns a file's bytes exactly as stored.</summary>
  public byte[] ReadStored(DcpEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (!this.HoldsItsOwnData)
      throw new NotSupportedException("WinterMute master index: the files' data live in the packages it lists, not in this file.");
    var data = new byte[entry.StoredLength];
    this._stream.Position = entry.Offset;
    this._stream.ReadExactly(data);
    return data;
  }

  /// <summary>Decodes a file.</summary>
  /// <exception cref="InvalidDataException">The zlib stream is damaged or decodes to the wrong length.</exception>
  public byte[] Extract(DcpEntry entry) {
    var stored = this.ReadStored(entry);
    if (!entry.IsCompressed)
      return stored;
    var data = ZlibStream.Decompress(stored);
    if (data.Length != entry.Length)
      throw new InvalidDataException($"WinterMute file '{entry.Name}' decoded to {data.Length} bytes, the directory says {entry.Length}.");
    return data;
  }

  private string ReadName(bool xor) {
    var length = this.ReadByte();
    var bytes = new byte[length];
    this._stream.ReadExactly(bytes);
    if (xor)
      for (var i = 0; i < bytes.Length; ++i)
        bytes[i] ^= NameKey;
    var end = Array.IndexOf(bytes, (byte)0);
    return NameEncoding.GetString(bytes, 0, end < 0 ? bytes.Length : end);
  }

  private byte ReadByte() {
    var b = this._stream.ReadByte();
    return b < 0 ? throw new InvalidDataException("Truncated WinterMute directory.") : (byte)b;
  }

  private uint ReadUInt32() {
    Span<byte> b = stackalloc byte[4];
    this._stream.ReadExactly(b);
    return BinaryPrimitives.ReadUInt32LittleEndian(b);
  }
}
