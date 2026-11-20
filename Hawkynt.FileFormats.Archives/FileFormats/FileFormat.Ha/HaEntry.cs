namespace FileFormat.Ha;

/// <summary>One member record of an HA archive (docs/HA-ON-DISK.md §1.1).</summary>
public sealed class HaEntry {
  /// <summary>Stored data (CPY).</summary>
  public const int MethodCpy = 0;

  /// <summary>LZ77 with arithmetic-coded tokens (ASC).</summary>
  public const int MethodAsc = 1;

  /// <summary>Order-4 finite-context model (HSC).</summary>
  public const int MethodHsc = 2;

  /// <summary>Directory record.</summary>
  public const int MethodDirectory = 14;

  /// <summary>Unix special file: a device, FIFO, socket or symbolic link (docs/HA-ON-DISK.md §1.4).</summary>
  public const int MethodSpecial = 15;

  /// <summary>First machine-information byte of a record written on Unix.</summary>
  public const byte MachineUnix = 2;

  /// <summary>Gets the full name, directories separated by '/'.</summary>
  public string FileName { get; init; } = string.Empty;

  /// <summary>Gets the record type (method): 0 CPY, 1 ASC, 2 HSC, 14 directory, 15 special.</summary>
  public int Method { get; init; }

  /// <summary>Gets the method's name as HA lists it.</summary>
  public string MethodName => this.Method switch {
    MethodCpy => "CPY",
    MethodAsc => "ASC",
    MethodHsc => "HSC",
    MethodDirectory => "DIR",
    MethodSpecial => "SPC",
    _ => $"Method{this.Method}",
  };

  /// <summary>Gets the original length.</summary>
  public uint OriginalSize { get; init; }

  /// <summary>Gets the stored (compressed) length.</summary>
  public uint CompressedSize { get; init; }

  /// <summary>Gets the CRC-32 of the original data (0 for directories).</summary>
  public uint Crc32 { get; init; }

  /// <summary>Gets the modification time (stored as Unix seconds), in UTC.</summary>
  public DateTime LastModified { get; init; }

  /// <summary>Gets the machine-specific information bytes (first byte: 1 MS-DOS, 2 Unix).</summary>
  public byte[] MachineInfo { get; init; } = [];

  /// <summary>Gets whether this is a directory record.</summary>
  public bool IsDirectory => this.Method == MethodDirectory;

  /// <summary>Gets whether this is a Unix special-file record (read it with <see cref="HaReader.ReadSpecial"/>).</summary>
  public bool IsSpecial => this.Method == MethodSpecial;

  /// <summary>Gets the Unix mode (file type and permission bits) of a record written on Unix, or null.</summary>
  public ushort? UnixMode => this.MachineInfo is [MachineUnix, var lo, var hi, _, _, _, _, ..] ? (ushort)(lo | (hi << 8)) : null;

  /// <summary>Gets the Unix owner id of a record written on Unix, or null.</summary>
  public ushort? UnixUserId => this.MachineInfo is [MachineUnix, _, _, var lo, var hi, _, _, ..] ? (ushort)(lo | (hi << 8)) : null;

  /// <summary>Gets the Unix group id of a record written on Unix, or null.</summary>
  public ushort? UnixGroupId => this.MachineInfo is [MachineUnix, _, _, _, _, var lo, var hi, ..] ? (ushort)(lo | (hi << 8)) : null;

  /// <summary>
  /// Gets what a special-file record holds, from the file-type bits of its Unix mode; null for any
  /// other record, <see cref="HaSpecialKind.Unknown"/> when the record carries no Unix mode.
  /// </summary>
  public HaSpecialKind? SpecialKind => !this.IsSpecial ? null : (this.UnixMode & 0xF000) switch {
    0x1000 => HaSpecialKind.Fifo,
    0x2000 => HaSpecialKind.CharacterDevice,
    0x6000 => HaSpecialKind.BlockDevice,
    0xA000 => HaSpecialKind.SymbolicLink,
    0xC000 => HaSpecialKind.Socket,
    _ => HaSpecialKind.Unknown,
  };

  /// <summary>Offset of the record's first byte.</summary>
  internal long RecordOffset { get; init; }

  /// <summary>Length of the record header (everything before the data).</summary>
  internal int HeaderLength { get; init; }

  /// <summary>Offset of the member's data.</summary>
  internal long DataOffset => this.RecordOffset + this.HeaderLength;
}
