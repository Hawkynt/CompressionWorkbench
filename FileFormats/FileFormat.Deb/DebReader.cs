using Compression.Core.Streams;
using FileFormat.Ar;
using FileFormat.Gzip;
using FileFormat.Tar;
using FileFormat.Xz;
using FileFormat.Zstd;

namespace FileFormat.Deb;

/// <summary>
/// Reads Debian package (.deb) files.
/// </summary>
/// <remarks>
/// A .deb file is an ar archive containing:
/// <list type="bullet">
///   <item><c>debian-binary</c> — format version string ("2.0\n")</item>
///   <item><c>control.tar.*</c> — package metadata (compressed tar)</item>
///   <item><c>data.tar.*</c> — package files (compressed tar)</item>
/// </list>
/// </remarks>
public sealed class DebReader : IDisposable {
  private readonly ArReader _ar;
  private bool _disposed;

  /// <summary>
  /// Opens a .deb package from a seekable stream.
  /// </summary>
  /// <param name="stream">The stream containing the .deb file.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the package is not a valid .deb file.
  /// </exception>
  public DebReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);

    _ar = new ArReader(stream);

    // Validate: first member must be debian-binary with correct version
    var versionMember = _ar.Entries.FirstOrDefault(
      e => e.Name == DebConstants.VersionMemberName);
    if (versionMember is null)
      throw new InvalidDataException("Not a valid .deb file: missing debian-binary member.");

    var versionText = System.Text.Encoding.ASCII.GetString(versionMember.Data).TrimEnd();
    if (!versionText.StartsWith("2."))
      throw new InvalidDataException($"Unsupported .deb format version: {versionText}");
  }

  /// <summary>
  /// Gets the raw ar archive entries.
  /// </summary>
  public IReadOnlyList<ArEntry> RawEntries => _ar.Entries;

  /// <summary>
  /// Extracts control metadata files from the control.tar.* member.
  /// </summary>
  /// <returns>A list of control file entries.</returns>
  public IReadOnlyList<DebEntry> ReadControlEntries() =>
    ReadTarMember(DebConstants.ControlPrefix);

  /// <summary>
  /// Extracts data files from the data.tar.* member.
  /// </summary>
  /// <returns>A list of data file entries.</returns>
  public IReadOnlyList<DebEntry> ReadDataEntries() =>
    ReadTarMember(DebConstants.DataPrefix);

  /// <summary>
  /// Gets the control file text (the "control" file inside control.tar.*).
  /// </summary>
  /// <returns>The control file contents as a string, or null if not found.</returns>
  public string? GetControlText() {
    var entries = ReadControlEntries();
    var control = entries.FirstOrDefault(
      e => e.Path is "control" or "./control" or "/control");
    return control is null ? null : System.Text.Encoding.UTF8.GetString(control.Data);
  }

  private IReadOnlyList<DebEntry> ReadTarMember(string prefix) {
    var member = _ar.Entries.FirstOrDefault(e => e.Name.StartsWith(prefix));
    if (member is null)
      return [];

    using var compressedStream = new MemoryStream(member.Data);
    using var decompressedStream = DecompressByExtension(member.Name, compressedStream);

    var result = new List<DebEntry>();
    using var tarReader = new TarReader(decompressedStream);
    while (true) {
      var entry = tarReader.GetNextEntry();
      if (entry is null) break;

      byte[] data;
      if (entry.IsDirectory) {
        data = [];
      } else {
        using var entryStream = tarReader.GetEntryStream();
        using var entryMs = new MemoryStream();
        entryStream.CopyTo(entryMs);
        data = entryMs.ToArray();
      }
      result.Add(new DebEntry(entry.Name, data, entry.IsDirectory));
    }

    return result;
  }

  private static Stream DecompressByExtension(string memberName, Stream compressed) {
    var decompressed = new MemoryStream();

    if (memberName.EndsWith(".gz")) {
      using var gz = new GzipStream(compressed, CompressionStreamMode.Decompress, leaveOpen: true);
      gz.CopyTo(decompressed);
    } else if (memberName.EndsWith(".xz")) {
      using var xz = new XzStream(compressed, CompressionStreamMode.Decompress, leaveOpen: true);
      xz.CopyTo(decompressed);
    } else if (memberName.EndsWith(".zst")) {
      using var zst = new ZstdStream(compressed, CompressionStreamMode.Decompress, leaveOpen: true);
      zst.CopyTo(decompressed);
    } else if (memberName.EndsWith(".bz2")) {
      using var bz = new FileFormat.Bzip2.Bzip2Stream(
        compressed, CompressionStreamMode.Decompress, leaveOpen: true);
      bz.CopyTo(decompressed);
    } else {
      // Uncompressed tar
      compressed.CopyTo(decompressed);
    }

    decompressed.Position = 0;
    return decompressed;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!_disposed) {
      _ar.Dispose();
      _disposed = true;
    }
  }
}
