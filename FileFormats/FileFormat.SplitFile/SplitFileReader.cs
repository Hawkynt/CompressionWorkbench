#pragma warning disable CS1591
using System.Text.RegularExpressions;

namespace FileFormat.SplitFile;

/// <summary>
/// Reads and joins split file parts (.001, .002, ...) into a single logical file.
/// </summary>
/// <remarks>
/// Split files use three-digit numeric extensions (.001, .002, etc.) where each part
/// contains a sequential chunk of the original file. This reader discovers all parts
/// by scanning the directory and joins them in order.
/// </remarks>
public sealed partial class SplitFileReader {
  private readonly string[] _parts;
  private readonly string _baseName;
  private readonly long _totalSize;

  public SplitFileEntry Entry { get; }

  /// <summary>
  /// Opens a split file set from any one of the parts (typically .001).
  /// </summary>
  /// <param name="anyPartPath">Path to any part file (e.g., "archive.001").</param>
  /// <exception cref="FileNotFoundException">Thrown when the part file doesn't exist.</exception>
  /// <exception cref="InvalidDataException">Thrown when the filename doesn't match split file pattern.</exception>
  public SplitFileReader(string anyPartPath) {
    if (!File.Exists(anyPartPath))
      throw new FileNotFoundException("Split file part not found.", anyPartPath);

    var fileName = Path.GetFileName(anyPartPath);
    var match = SplitPattern().Match(fileName);
    if (!match.Success)
      throw new InvalidDataException($"Not a split file: {fileName}");

    _baseName = match.Groups[1].Value;
    var dir = Path.GetDirectoryName(anyPartPath) ?? ".";

    // Discover all parts
    var parts = new SortedDictionary<int, string>();
    foreach (var file in Directory.GetFiles(dir, _baseName + ".*")) {
      var fn = Path.GetFileName(file);
      var m = SplitPattern().Match(fn);
      if (m.Success && m.Groups[1].Value == _baseName) {
        if (int.TryParse(m.Groups[2].Value, out var num))
          parts[num] = file;
      }
    }

    if (parts.Count == 0)
      throw new InvalidDataException("No split file parts found.");

    _parts = [.. parts.Values];

    // Calculate total size
    _totalSize = 0;
    foreach (var part in _parts)
      _totalSize += new FileInfo(part).Length;

    Entry = new SplitFileEntry {
      Name = _baseName,
      Size = _totalSize,
      PartCount = _parts.Length,
    };
  }

  /// <summary>
  /// Creates a <see cref="SplitFileReader"/> from pre-ordered part streams.
  /// </summary>
  public SplitFileReader(string baseName, Stream[] partStreams) {
    _baseName = baseName;
    _parts = [];
    _totalSize = 0;
    foreach (var s in partStreams)
      _totalSize += s.Length;

    Entry = new SplitFileEntry {
      Name = baseName,
      Size = _totalSize,
      PartCount = partStreams.Length,
    };
    _partStreams = partStreams;
  }

  private Stream[]? _partStreams;

  /// <summary>
  /// Extracts (joins) all parts into a single byte array.
  /// </summary>
  public byte[] Extract() {
    using var output = new MemoryStream();

    if (_partStreams != null) {
      // Stream-based (testing)
      foreach (var s in _partStreams) {
        s.Position = 0;
        s.CopyTo(output);
      }
    } else {
      // File-based
      foreach (var part in _parts) {
        using var fs = File.OpenRead(part);
        fs.CopyTo(output);
      }
    }

    return output.ToArray();
  }

  /// <summary>
  /// Writes the joined file to the specified output stream.
  /// </summary>
  public void ExtractTo(Stream output) {
    if (_partStreams != null) {
      foreach (var s in _partStreams) {
        s.Position = 0;
        s.CopyTo(output);
      }
    } else {
      foreach (var part in _parts) {
        using var fs = File.OpenRead(part);
        fs.CopyTo(output);
      }
    }
  }

  [GeneratedRegex(@"^(.+)\.(\d{3})$")]
  private static partial Regex SplitPattern();
}
