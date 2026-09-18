namespace FileFormat.Cpio;

/// <summary>
/// The per-variant layout rules every CPIO code path needs: how long the fixed
/// header is, and what boundary the pathname and the payload are padded to.
/// </summary>
/// <remarks>
/// Kept in one place because the reader, the writer, the in-place modifier and
/// the layout map all have to agree on them; a variant whose alignment is
/// described differently in two of those is an archive nobody can read back.
/// </remarks>
internal static class CpioLayout {

  /// <summary>Length of the fixed-size header that precedes the pathname.</summary>
  public static int HeaderSize(CpioArchiveFormat format) => format switch {
    CpioArchiveFormat.NewAscii or CpioArchiveFormat.NewCrc => CpioConstants.NewAsciiHeaderSize,
    CpioArchiveFormat.PortableAscii => CpioConstants.PortableAsciiHeaderSize,
    CpioArchiveFormat.BinaryLittleEndian or CpioArchiveFormat.BinaryBigEndian => CpioConstants.BinaryHeaderSize,
    _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown CPIO variant."),
  };

  /// <summary>
  /// Boundary the pathname and the payload are padded to. <c>odc</c> is the odd
  /// one out: it packs everything end to end, so its alignment is 1.
  /// </summary>
  public static int Alignment(CpioArchiveFormat format) => format switch {
    CpioArchiveFormat.NewAscii or CpioArchiveFormat.NewCrc => 4,
    CpioArchiveFormat.PortableAscii => 1,
    CpioArchiveFormat.BinaryLittleEndian or CpioArchiveFormat.BinaryBigEndian => 2,
    _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown CPIO variant."),
  };

  /// <summary>Padding after the header+pathname block.</summary>
  public static int NamePadding(CpioArchiveFormat format, int nameSize)
    => Pad(HeaderSize(format) + (long)nameSize, Alignment(format));

  /// <summary>Padding after the payload.</summary>
  public static int DataPadding(CpioArchiveFormat format, long fileSize)
    => Pad(fileSize, Alignment(format));

  /// <summary>Total on-disk length of one entry, header through payload padding.</summary>
  public static long EntrySize(CpioArchiveFormat format, int nameSize, long fileSize)
    => HeaderSize(format) + (long)nameSize + NamePadding(format, nameSize)
       + fileSize + DataPadding(format, fileSize);

  private static int Pad(long length, int alignment)
    => alignment <= 1 ? 0 : (int)((alignment - length % alignment) % alignment);
}
