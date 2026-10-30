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

  /// <summary>Unix special file (device, FIFO or symlink data).</summary>
  public const int MethodSpecial = 15;

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
    MethodSpecial => "SPECIAL",
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

  /// <summary>Offset of the record's first byte.</summary>
  internal long RecordOffset { get; init; }

  /// <summary>Length of the record header (everything before the data).</summary>
  internal int HeaderLength { get; init; }

  /// <summary>Offset of the member's data.</summary>
  internal long DataOffset => this.RecordOffset + this.HeaderLength;
}
