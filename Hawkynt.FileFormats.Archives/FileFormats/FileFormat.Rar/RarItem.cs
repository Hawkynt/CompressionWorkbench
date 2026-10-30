namespace FileFormat.Rar;

/// <summary>How a RAR entry refers to something else instead of holding data.</summary>
public enum RarLinkKind {
  /// <summary>An ordinary file or directory.</summary>
  None = 0,
  /// <summary>A Unix symbolic link (RAR 5 redirection type 1; RAR 4 Unix link).</summary>
  UnixSymlink = 1,
  /// <summary>A Windows symbolic link (RAR 5 redirection type 2).</summary>
  WindowsSymlink = 2,
  /// <summary>A Windows junction (RAR 5 redirection type 3).</summary>
  WindowsJunction = 3,
  /// <summary>A hard link to an earlier entry (RAR 5 redirection type 4).</summary>
  HardLink = 4,
  /// <summary>A copy of an earlier entry, stored once (RAR 5 redirection type 5, <c>rar -oi</c>).</summary>
  FileCopy = 5,
}

/// <summary>The RAR 5 file encryption record (technote, "File encryption record").</summary>
internal sealed record Rar5CryptInfo(int KdfLog, byte[] Salt, byte[] Iv, byte[]? Check, bool UseMac);

/// <summary>
/// One file, directory or service record of a RAR archive, with every part it has in the
/// volumes of its set.
/// </summary>
internal sealed class RarItem {
  public string Name = "";
  public bool IsDirectory;
  public bool IsService;
  public string? ServiceName;
  /// <summary>Unpacked size; -1 when the header says it is unknown.</summary>
  public long Size;
  public readonly List<RarPart> Parts = [];
  public bool SplitBefore;
  /// <summary>The last part known says the file continues in a volume that is missing.</summary>
  public bool SplitAfter;
  public int HostOs;
  public uint Attributes;
  public DateTimeOffset? Modified;
  public DateTimeOffset? Created;
  public DateTimeOffset? Accessed;
  public uint? Crc;
  public byte[]? Blake2;
  public bool IsEncrypted;
  public bool Solid;
  /// <summary>0 store, 1-5 compressed.</summary>
  public int Method;
  public RarLinkKind Link;
  public string? LinkTarget;
  public bool LinkTargetIsDirectory;
  public long? FileVersion;
  /// <summary>Name of the NTFS stream, for an <c>STM</c> service record.</summary>
  public string? StreamName;
  /// <summary>Index into the item list of the file a service record belongs to.</summary>
  public int Owner = -1;

  // RAR 5
  public bool IsRar5;
  public long DictionarySize;
  public int AlgorithmVersion;
  public Rar5CryptInfo? Crypt;

  // RAR 1.5 - 4.x
  public int UnpackVersion;
  /// <summary>Cipher generation for RAR 1.x archives: 13 (RAR 1.3/1.4) or the unpack version.</summary>
  public int CryptVersion;
  /// <summary>RAR 1.4's 16-bit file checksum, for archives in that format.</summary>
  public ushort? Checksum14;
  public int WindowBits;
  public byte[]? Salt;

  public long PackedSize => this.Parts.Sum(p => p.Length);
}

/// <summary>What the main header (and end header) of a set says about the archive.</summary>
internal sealed class RarArchiveInfo {
  public int Format;            // 4 or 5
  public long SfxSize;          // bytes before the signature in the first volume
  public bool IsVolume;
  public bool IsSolid;
  public bool IsLocked;
  public bool HasRecoveryRecord;
  public bool HeadersEncrypted;
  public bool NewVolumeNaming;
  public bool FirstVolume = true;
  public int VolumeNumber;
  public string? Comment;
  public bool HasQuickOpen;
  /// <summary>A header after the main one was damaged or cut off; the walk stopped there.</summary>
  public bool Truncated;
  public int VolumeCount;
  public bool LastVolumeMissing;
  public string? MissingVolume;
}
