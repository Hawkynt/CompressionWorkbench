#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Imd;

/// <summary>
/// ImageDisk (IMD) floppy image. ASCII header line
/// <c>IMD v.vv: dd/mm/yyyy hh:mm:ss\r\n</c> followed by a free-form comment block
/// terminated by 0x1A (EOF). After that come binary track records:
/// mode(u8), cylinder(u8), head(u8), sectorCount(u8), sectorSizeCode(u8), then a
/// sector-numbering map of <c>sectorCount</c> bytes, optional cylinder map (when
/// head bit 0x80 is set) and optional head map (when head bit 0x40 is set), then
/// per-sector data records. Each sector data record starts with a type byte:
/// 0 = unavailable, 1 = normal (sectorSize bytes), 2 = compressed (one fill byte),
/// and odd/even higher values for deleted/error variants.
///
/// <para>Surfaces <c>FULL.imd</c> verbatim, a <c>metadata.ini</c> (version,
/// comment, track count, geometry) and per-sector decoded data under
/// <c>tracks/</c>. Fully tractable — read-only. Malformed input degrades to
/// FULL + partial metadata without throwing.</para>
///
/// References:
/// <list type="bullet">
///   <item><description>IMD.TXT — ImageDisk documentation by Dave Dunfield, the canonical format description shipped with the tool</description></item>
///   <item><description><c>https://github.com/jfdelnero/HxCFloppyEmulator</c> — HxC floppy tooling — maintained independent reader/converter for IMD images</description></item>
/// </list>
/// </summary>
public sealed class ImdFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Imd";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "ImageDisk (IMD)";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".imd";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".imd"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("IMD "u8.ToArray(), Confidence: 0.90),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "ImageDisk (IMD) floppy image: ASCII header + comment + per-track/sector decoded data.";

  private static readonly int[] SectorSizes = [128, 256, 512, 1024, 2048, 4096, 8192];

  private sealed record ImdInfo(
    string HeaderLine,
    string Comment,
    int TrackDataOffset,
    int TrackCount,
    bool Valid);

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var data = ReadAll(stream);
    var info = Parse(data, out var sectors);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.imd", fullSize, fullSize, "Stored", false, false, null),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null),
    };
    if (info.Valid) {
      var idx = 2;
      foreach (var (name, bytes) in sectors)
        entries.Add(new ArchiveEntryInfo(idx++, name, bytes.Length, bytes.Length, "Stored", false, false, null, "Sector"));
    }
    return entries;
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    if (Wants(files, "FULL.imd"))
      WriteFile(outputDir, "FULL.imd", data);

    var info = Parse(data, out var sectors);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(info)));

    if (info.Valid) {
      foreach (var (name, bytes) in sectors) {
        if (Wants(files, name))
          WriteFile(outputDir, name, bytes);
      }
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static ImdInfo Parse(byte[] data, out List<(string Name, byte[] Data)> sectors) {
    sectors = [];
    try {
      // Header line: must start with "IMD ".
      if (data.Length < 4 || data[0] != 'I' || data[1] != 'M' || data[2] != 'D' || data[3] != ' ')
        return new ImdInfo("", "", 0, 0, Valid: false);

      // Header line ends at first \r\n.
      var nl = IndexOf(data, (byte)'\n', 0);
      if (nl < 0) return new ImdInfo("", "", 0, 0, Valid: false);
      var headerLine = Encoding.Latin1.GetString(data, 0, nl).TrimEnd('\r', '\n');

      // Comment runs from after the header line to the 0x1A terminator.
      var commentStart = nl + 1;
      var eof = IndexOf(data, 0x1A, commentStart);
      if (eof < 0) return new ImdInfo(headerLine, "", 0, 0, Valid: false);
      var comment = Encoding.Latin1.GetString(data, commentStart, eof - commentStart);

      var pos = eof + 1;
      var trackCount = 0;
      var guard = 0;
      while (pos + 5 <= data.Length && guard++ < 4096) {
        var head = data[pos + 2];
        var sectorCount = data[pos + 3];
        var sizeCode = data[pos + 4];
        var cyl = data[pos + 1];
        // mode = data[pos] is informational.
        pos += 5;

        var sectorSize = sizeCode < SectorSizes.Length ? SectorSizes[sizeCode] : 0;
        var hasCylMap = (head & 0x80) != 0;
        var hasHeadMap = (head & 0x40) != 0;
        var physHead = head & 0x3F;

        if (pos + sectorCount > data.Length) break;
        var sectorMap = new byte[sectorCount];
        Array.Copy(data, pos, sectorMap, 0, sectorCount);
        pos += sectorCount;

        if (hasCylMap) {
          if (pos + sectorCount > data.Length) break;
          pos += sectorCount; // skip cylinder map
        }
        if (hasHeadMap) {
          if (pos + sectorCount > data.Length) break;
          pos += sectorCount; // skip head map
        }

        for (var s = 0; s < sectorCount; ++s) {
          if (pos >= data.Length) { return Finalize(headerLine, comment, eof + 1, trackCount); }
          var type = data[pos++];
          byte[] sectorData;
          switch (type) {
            case 0: // unavailable
              sectorData = [];
              break;
            case 1: case 3: case 5: case 7: // normal data (3/5/7 = deleted/error variants)
              if (sectorSize <= 0 || pos + sectorSize > data.Length)
                return Finalize(headerLine, comment, eof + 1, trackCount);
              sectorData = new byte[sectorSize];
              Array.Copy(data, pos, sectorData, 0, sectorSize);
              pos += sectorSize;
              break;
            case 2: case 4: case 6: case 8: // compressed: single fill byte
              if (pos >= data.Length) return Finalize(headerLine, comment, eof + 1, trackCount);
              var fill = data[pos++];
              if (sectorSize <= 0) { sectorData = [fill]; break; }
              sectorData = new byte[sectorSize];
              Array.Fill(sectorData, fill);
              break;
            default:
              return Finalize(headerLine, comment, eof + 1, trackCount);
          }
          var sNum = s < sectorMap.Length ? sectorMap[s] : (byte)s;
          var name = string.Create(CultureInfo.InvariantCulture,
            $"tracks/c{cyl:D2}_h{physHead}_s{sNum:D2}.bin");
          sectors.Add((name, sectorData));
          if (sectors.Count > 8192) return Finalize(headerLine, comment, eof + 1, trackCount);
        }
        ++trackCount;
      }
      return Finalize(headerLine, comment, eof + 1, trackCount);
    } catch {
      return new ImdInfo("", "", 0, 0, Valid: false);
    }

    static ImdInfo Finalize(string h, string c, int off, int tc) => new(h, c, off, tc, Valid: true);
  }

  private static int IndexOf(byte[] data, byte value, int start) {
    for (var i = start; i < data.Length; ++i)
      if (data[i] == value) return i;
    return -1;
  }

  private static string BuildMetadataIni(ImdInfo info) {
    var sb = new StringBuilder();
    sb.Append("[Imd]\n");
    sb.Append(CultureInfo.InvariantCulture, $"valid={(info.Valid ? 1 : 0)}\n");
    if (!info.Valid) {
      sb.Append("parse_status=partial\n");
      return sb.ToString();
    }
    sb.Append(CultureInfo.InvariantCulture, $"header={info.HeaderLine}\n");
    sb.Append(CultureInfo.InvariantCulture, $"track_count={info.TrackCount}\n");
    var oneLineComment = info.Comment.Replace("\r", " ").Replace("\n", " ").Trim();
    sb.Append(CultureInfo.InvariantCulture, $"comment={oneLineComment}\n");
    sb.Append("parse_status=ok\n");
    return sb.ToString();
  }

  private static long SafeLength(Stream s) => s.CanSeek ? s.Length : 0;

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
