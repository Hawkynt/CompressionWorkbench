namespace FileFormat.PyInstaller;

/// <summary>
/// A single entry in a PyInstaller CArchive table of contents.
/// </summary>
/// <param name="Name">Entry name (module name, binary filename, or data path).</param>
/// <param name="TypeCode">
/// CArchive type code: 'z'/'Z' PYZ archive, 'm' module, 'M' package module,
/// 's' pyc source, 'b' binary, 'x' data, 'o' runtime option, 'd' dependency.
/// </param>
/// <param name="IsCompressed">True when the stored bytes are zlib-compressed.</param>
/// <param name="DataOffset">Absolute byte offset of the entry data within the source stream.</param>
/// <param name="CompressedLength">Number of stored bytes.</param>
/// <param name="UncompressedLength">Length after inflation (equals <see cref="CompressedLength"/> when stored).</param>
public sealed record PyInstallerEntry(
  string Name,
  char TypeCode,
  bool IsCompressed,
  long DataOffset,
  long CompressedLength,
  long UncompressedLength
);
