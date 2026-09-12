using System.Text;
using Compression.Core.Streams;
using FileFormat.Ar;
using FileFormat.Bzip2;
using FileFormat.Gzip;
using FileFormat.Tar;
using FileFormat.Xz;
using FileFormat.Zstd;

namespace FileFormat.Deb;

/// <summary>
/// Writes Debian package (.deb) files.
/// </summary>
/// <remarks>
/// Creates a .deb file by writing an ar archive containing debian-binary,
/// control.tar.*, and data.tar.* members.
/// </remarks>
public sealed class DebWriter {
  private readonly Stream _output;
  private readonly DebCompression _compression;

  /// <summary>
  /// Initializes a new <see cref="DebWriter"/> writing to the given stream.
  /// </summary>
  /// <param name="output">The output stream for the .deb file.</param>
  /// <param name="compression">The compression format for tar members.</param>
  public DebWriter(Stream output, DebCompression compression = DebCompression.Gzip) {
    ArgumentNullException.ThrowIfNull(output);
    _output = output;
    _compression = compression;
  }

  /// <summary>
  /// Writes a complete .deb package.
  /// </summary>
  /// <param name="controlFiles">
  /// The control metadata files (e.g. "control", "postinst").
  /// Each entry's Path is the filename within the control archive.
  /// </param>
  /// <param name="dataFiles">
  /// The package data files. Each entry's Path is the installed path
  /// (e.g. "usr/bin/hello"). Directories should have IsDirectory=true.
  /// </param>
  public void Write(IReadOnlyList<DebEntry> controlFiles, IReadOnlyList<DebEntry> dataFiles) {
    ArgumentNullException.ThrowIfNull(controlFiles);
    ArgumentNullException.ThrowIfNull(dataFiles);

    var suffix = GetTarSuffix(_compression);
    var arEntries = new List<ArEntry>();

    // 1. debian-binary
    arEntries.Add(new ArEntry {
      Name = DebConstants.VersionMemberName,
      Data = Encoding.ASCII.GetBytes(DebConstants.VersionString),
    });

    // 2. control.tar.*
    arEntries.Add(new ArEntry {
      Name = "control.tar" + suffix,
      Data = BuildCompressedTar(controlFiles, _compression),
    });

    // 3. data.tar.*
    arEntries.Add(new ArEntry {
      Name = "data.tar" + suffix,
      Data = BuildCompressedTar(dataFiles, _compression),
    });

    var arWriter = new ArWriter(_output);
    arWriter.Write(arEntries);
  }

  private static string GetTarSuffix(DebCompression compression) => compression switch {
    DebCompression.Gzip => ".gz",
    DebCompression.Xz => ".xz",
    DebCompression.Zstd => ".zst",
    DebCompression.Bzip2 => ".bz2",
    _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, null),
  };

  private static byte[] BuildCompressedTar(
      IReadOnlyList<DebEntry> entries, DebCompression compression) {
    // Build tar in memory
    using var tarMs = new MemoryStream();
    using (var tarWriter = new TarWriter(tarMs)) {
      foreach (var entry in entries) {
        var tarEntry = new TarEntry {
          Name = entry.Path,
          TypeFlag = entry.IsDirectory ? (byte)'5' : (byte)'0',
          Size = entry.Data.Length,
        };
        tarWriter.AddEntry(tarEntry, entry.Data);
      }
    }

    // Compress the tar
    var tarData = tarMs.ToArray();
    using var outMs = new MemoryStream();
    using (var compressor = CreateCompressor(compression, outMs)) {
      compressor.Write(tarData, 0, tarData.Length);
    }

    return outMs.ToArray();
  }

  private static Stream CreateCompressor(DebCompression compression, MemoryStream output) =>
    compression switch {
      DebCompression.Gzip => new GzipStream(output, CompressionStreamMode.Compress, leaveOpen: true),
      DebCompression.Xz => new XzStream(output, CompressionStreamMode.Compress, leaveOpen: true),
      DebCompression.Zstd => new ZstdStream(output, CompressionStreamMode.Compress, leaveOpen: true),
      DebCompression.Bzip2 => new Bzip2Stream(output, CompressionStreamMode.Compress, leaveOpen: true),
      _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, null),
    };
}
