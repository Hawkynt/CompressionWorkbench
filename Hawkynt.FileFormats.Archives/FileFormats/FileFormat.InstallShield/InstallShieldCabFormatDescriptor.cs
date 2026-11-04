#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.InstallShield;

/// <summary>
/// InstallShield cabinet set: data1.hdr (the header) with data1.cab, data2.cab, ... (the
/// volumes), all starting with "ISc(". Open the .hdr (or a data1.cab that carries the header);
/// the volumes are found beside it.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/twogood/unshield</c> — unshield (MIT), the reader this one was converted from and is checked against</description></item>
/// </list>
/// </summary>
public sealed class InstallShieldCabFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "InstallShieldCab";
  public string DisplayName => "InstallShield cabinet";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".hdr";
  // ".cab" belongs to Microsoft cabinets; an "ISc(" .cab is told apart by FormatDetector.
  public IReadOnlyList<string> Extensions => [".hdr"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("ISc("u8.ToArray(), Offset: 0, Confidence: 0.9)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate"), new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "InstallShield cabinet set (data1.hdr + dataN.cab), InstallShield 5 and later";

  /// <summary>Opens a cabinet set; volumes are looked up beside a file stream's path.</summary>
  public static InstallShieldCabReader Open(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var path = stream is FileStream fs ? Path.GetFullPath(fs.Name) : null;
    var headerIndex = 1;
    Func<int, Stream?> openVolume;
    Func<string, Stream?>? openExternal = null;
    if (path != null) {
      var dir = Path.GetDirectoryName(path)!;
      var name = Path.GetFileName(path);
      // unshield's pattern: the name up to its first dot or digit, then "<n>.cab".
      var cut = name.IndexOfAny(['.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9']);
      var prefix = cut < 0 ? name : name[..cut];
      var digits = new string(name.Skip(prefix.Length).TakeWhile(char.IsAsciiDigit).ToArray());
      if (digits.Length > 0 && int.TryParse(digits, out var n))
        headerIndex = n;
      openVolume = i => {
        var want = prefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".cab";
        var match = Directory.EnumerateFiles(dir).FirstOrDefault(p => string.Equals(Path.GetFileName(p), want, StringComparison.OrdinalIgnoreCase));
        return match == null ? null : File.OpenRead(match);
      };
      openExternal = relative => {
        // Each segment matched case-insensitively, as unshield does for its volume names.
        var current = dir;
        foreach (var segment in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)) {
          if (segment is "." or "..")
            return null;
          var next = Directory.EnumerateFileSystemEntries(current).FirstOrDefault(p => string.Equals(Path.GetFileName(p), segment, StringComparison.OrdinalIgnoreCase));
          if (next == null)
            return null;
          current = next;
        }
        return File.Exists(current) ? File.OpenRead(current) : null;
      };
    } else {
      // Without a path only the stream itself is reachable: a data1.cab that carries its header.
      openVolume = i => i == 1 ? new NonClosing(stream) : null;
    }
    return new InstallShieldCabReader(stream, headerIndex, openVolume, openExternal);
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    InstallShieldCabReader reader;
    try {
      reader = Open(stream);
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException) {
      var note = Encoding.UTF8.GetBytes($"format=installshield-cab\nparse_status=partial\nerror={ex.Message.Replace('\n', ' ')}\n");
      return [new ArchiveEntryInfo(0, "metadata.ini", note.Length, note.Length, "stored", false, false, null, Kind: "metadata")];
    }
    return reader.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.Size, e.CompressedSize,
      e.Size == e.CompressedSize ? "stored" : "deflate", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = Open(stream);
    foreach (var e in reader.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files))
        continue;
      WriteFile(outputDir, e.FileName, reader.Extract(e));
    }
  }

  /// <summary>A view of a stream that leaves it open when disposed.</summary>
  private sealed class NonClosing(Stream inner) : Stream {
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
