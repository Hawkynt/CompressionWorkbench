namespace FileFormat.Tar;

/// <summary>
/// Constants for the TAR archive format (POSIX.1-1988 UStar and GNU extensions).
/// </summary>
internal static class TarConstants {
  /// <summary>Size of a TAR block in bytes.</summary>
  public const int BlockSize = 512;

  /// <summary>Length of the name field in the header.</summary>
  public const int NameLength = 100;

  /// <summary>Length of the mode field in the header.</summary>
  public const int ModeLength = 8;

  /// <summary>Length of the uid field in the header.</summary>
  public const int UidLength = 8;

  /// <summary>Length of the gid field in the header.</summary>
  public const int GidLength = 8;

  /// <summary>Length of the size field in the header.</summary>
  public const int SizeLength = 12;

  /// <summary>Length of the mtime field in the header.</summary>
  public const int MtimeLength = 12;

  /// <summary>Length of the checksum field in the header.</summary>
  public const int ChecksumLength = 8;

  /// <summary>Length of the linkname field in the header.</summary>
  public const int LinkNameLength = 100;

  /// <summary>Length of the magic field in the header.</summary>
  public const int MagicLength = 6;

  /// <summary>Length of the version field in the header.</summary>
  public const int VersionLength = 2;

  /// <summary>Length of the uname field in the header.</summary>
  public const int UnameLength = 32;

  /// <summary>Length of the gname field in the header.</summary>
  public const int GnameLength = 32;

  /// <summary>Length of the prefix field in the header.</summary>
  public const int PrefixLength = 155;

  /// <summary>Type flag for a regular file.</summary>
  public const byte TypeRegular = (byte)'0';

  /// <summary>Alternate type flag for a regular file (NUL byte, pre-POSIX).</summary>
  public const byte TypeRegularAlt = 0;

  /// <summary>Type flag for a hard link.</summary>
  public const byte TypeHardLink = (byte)'1';

  /// <summary>Type flag for a symbolic link.</summary>
  public const byte TypeSymLink = (byte)'2';

  /// <summary>Type flag for a character device.</summary>
  public const byte TypeCharDevice = (byte)'3';

  /// <summary>Type flag for a block device.</summary>
  public const byte TypeBlockDevice = (byte)'4';

  /// <summary>Type flag for a directory.</summary>
  public const byte TypeDirectory = (byte)'5';

  /// <summary>Type flag for a FIFO (named pipe).</summary>
  public const byte TypeFifo = (byte)'6';

  /// <summary>GNU extension type flag for a long file name.</summary>
  public const byte TypeGnuLongName = (byte)'L';

  /// <summary>GNU extension type flag for a long link name.</summary>
  public const byte TypeGnuLongLink = (byte)'K';

  /// <summary>PAX extended header type flag for the next entry.</summary>
  public const byte TypePaxHeader = (byte)'x';

  /// <summary>PAX global extended header type flag.</summary>
  public const byte TypePaxGlobal = (byte)'g';

  /// <summary>GNU extension type flag for a multi-volume continuation entry.</summary>
  public const byte TypeGnuMultiVolume = (byte)'M';

  /// <summary>GNU extension type flag for a sparse file.</summary>
  public const byte TypeGnuSparse = (byte)'S';

  /// <summary>UStar magic string.</summary>
  public const string UstarMagic = "ustar";
}
