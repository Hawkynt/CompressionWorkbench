#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dms;

/// <summary>
/// Amiga Disk Masher System (DMS) — track-based floppy-disk archiver ubiquitous in the Amiga scene.
///
/// References:
/// <list type="bullet">
///   <item><description>libxad DMS client (Dirk Stöcker, LGPL-2.1-or-later) — converted for the decoders; see THIRD-PARTY-NOTICE.libxad.txt</description></item>
///   <item><description>xDMS (public domain) and unar — black-box oracles in the tests</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Disk_Masher_System</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class DmsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable {

  /// <summary>Rebuild-based defrag: extracts the disk image then re-emits the DMS file.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts the disk image then re-emits the DMS file.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new DmsReader(stream, leaveOpen: true);
        var disk = r.ExtractDisk();
        return [("disk.adf", disk)];
      },
      buildImage: files => {
        if (files.Count == 0)
          throw new InvalidOperationException("DMS defrag requires a disk image to be present.");
        using var ms = new MemoryStream();
        using (var w = new DmsWriter(ms, leaveOpen: true))
          w.WriteDisk(files[0].Data, compressionMode: 1);
        return ms.ToArray();
      });
  }
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Dms";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "DMS";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".dms";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".dms"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'D', (byte)'M', (byte)'S', (byte)'!'], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("dms", "DMS")];
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
  public string Description => "Amiga Disk Masher System, floppy disk archiver";

  /// <summary>
  /// Lists <c>metadata.ini</c>, <c>disk.adf</c> (the data tracks in file order) and any banner or
  /// FILE_ID.DIZ text tracks. Never throws: an unreadable archive lists as <c>FULL.dms</c>.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      using var r = new DmsReader(stream, leaveOpen: true, password);
      var data = r.DataTracks.ToList();
      var result = new List<ArchiveEntryInfo> {
        new(0, "metadata.ini", 0, 0, "stored", false, false, null),
        new(1, "disk.adf", data.Sum(t => (long)t.UnpackedSize), data.Sum(t => (long)t.PackedSize), Method(data), false, r.Header.IsEncrypted, null),
      };
      var banner = 0;
      foreach (var t in r.Entries.Where(t => t.IsText)) {
        var name = t.Number == 80 ? "FILE_ID.DIZ" : banner++ == 0 ? "banner.txt" : $"banner{banner}.txt";
        result.Add(new(result.Count, name, t.UnpackedSize, t.PackedSize, $"Mode {t.Mode}", false, r.Header.IsEncrypted, null));
      }
      return result;
    } catch (InvalidDataException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.dms", length, length, "stored", false, false, null)];
    }
  }

  private static string Method(List<DmsTrackHeader> tracks)
    => tracks.Count == 0 ? "stored" : string.Join('+', tracks.Select(t => ModeName(t.Mode)).Distinct());

  private static string ModeName(int mode) => mode switch {
    0 => "none", 1 => "simple", 2 => "quick", 3 => "medium", 4 => "deep", 5 => "heavy1", 6 => "heavy2", _ => $"mode{mode}",
  };

  /// <summary>Decodes the disk image (every track CRC- and sum-checked), the texts and the metadata.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new DmsReader(stream, leaveOpen: true, password);
    if (files is not { Length: > 0 } || MatchesFilter("disk.adf", files))
      WriteFile(outputDir, "disk.adf", r.ExtractDisk());
    foreach (var (name, data) in r.TextEntries())
      if (files is not { Length: > 0 } || MatchesFilter(name, files))
        WriteFile(outputDir, name, data);
    if (files is not { Length: > 0 } || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", System.Text.Encoding.UTF8.GetBytes(Metadata(r)));
  }

  private static string Metadata(DmsReader r) {
    var h = r.Header;
    var data = r.DataTracks.ToList();
    const char nl = '\n';
    var sb = new System.Text.StringBuilder("[dms]").Append(nl);
    var range = data.Count == 0 ? "none" : $"{data.Min(t => t.Number)}-{data.Max(t => t.Number)}";
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    sb.Append(inv, $"tracks={range}").Append(nl);
    sb.Append(inv, $"track_count={data.Count}").Append(nl);
    sb.Append(inv, $"encrypted={(h.IsEncrypted ? "yes" : "no")}").Append(nl);
    sb.Append(inv, $"info_flags=0x{h.InfoFlags:X8}").Append(nl);
    sb.Append(inv, $"creator_version={h.CreatorVersion}").Append(nl);
    sb.Append(inv, $"compression={Method(data)}").Append(nl);
    return sb.ToString();
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileInputs = inputs.Where(i => !i.IsDirectory &&
      !Path.GetFileName(i.ArchiveName).Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)).ToArray();
    if (fileInputs.Length != 1)
      throw new ArgumentException("DMS format requires exactly one input file (disk image).");
    var data = fileInputs[0].ReadContent();
    using var w = new DmsWriter(output, leaveOpen: true);
    w.WriteDisk(data, compressionMode: 1);
  }
}
