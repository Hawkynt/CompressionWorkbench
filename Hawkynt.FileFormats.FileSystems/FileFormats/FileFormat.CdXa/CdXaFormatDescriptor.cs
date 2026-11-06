#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.CdXa;

/// <summary>
/// RIFF CDXA file (CD-ROM XA Mode 2 sectors, typically a Video CD <c>AVSEQnn.DAT</c>). The <c>.dat</c>
/// extension is generic, so the file is detected by its <c>RIFF</c>…<c>CDXA</c> header. Lists
/// <c>metadata.ini</c> and <c>track.mpg</c>, the user data from the first to the last non-empty sector.
/// When the sectors follow the Video CD layout (<see cref="CdXaFile.DetectLayout"/>), the track can be
/// replaced and the file rebuilt from it; files are created from <c>track.mpg</c>, with
/// <c>metadata.ini</c> choosing addresses and margins.
/// </summary>
public sealed class CdXaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
    IArchiveModifiable, IArchiveWriteConstraints, ISyntheticEntryNames {
  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metadata.ini" };

  /// <summary>Defaults for a new file: the margins and first address Video CD authoring uses for track 2.</summary>
  private static readonly CdXaFile.Layout NewLayout = new(StartFrame: 8 * 75, FileNumber: 1, LeadSectors: 30, TrailSectors: 45, CdXaFile.RecordEnds.LastSector);

  public string Id => "CdXa";
  public string DisplayName => "CD-ROM XA (RIFF CDXA)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dat";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'C', (byte)'D', (byte)'X', (byte)'A'],
      Confidence: 0.95, Mask: [0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF]),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("mode2form2", "CD-ROM XA Mode 2 Form 2 sectors")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "RIFF CDXA: CD-ROM XA Mode 2 sectors of a Video CD MPEG track (AVSEQnn.DAT)";
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;
  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription => "accepts: track.mpg (an MPEG program stream with packs of at most 2324 bytes), metadata.ini";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName.Replace('\\', '/'));
    if (name.Equals("track.mpg", StringComparison.OrdinalIgnoreCase) || name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }
    reason = $"'{input.ArchiveName}' is not a CD-XA member; {this.AcceptedInputsDescription}";
    return false;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      return Entries(Read(stream)).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null)).ToList();
    } catch (InvalidDataException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.dat", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (name, data) in Entries(Read(stream)))
      if (files is not { Length: > 0 } || MatchesFilter(name, files))
        WriteFile(outputDir, name, data);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    var (track, layout, fmt) = this.Apply(null, NewLayout, null, inputs);
    if (track is null) throw new ArgumentException("CD-XA: track.mpg is required.");
    Build(track, layout, fmt).Write(output);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    var file = Read(archive);
    var layout = file.DetectLayout()
      ?? throw new NotSupportedException("CD-XA: the sectors do not follow the Video CD layout, so the file cannot be rebuilt losslessly.");
    var (track, newLayout, fmt) = this.Apply(file.Track(), layout, file.Fmt, inputs);
    WholeImageRebuildCommitter.Replace(archive, Build(track!, newLayout, fmt).Write, candidate => Read(candidate));
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var raw in entryNames)
      if (!Synthetic.Contains(Path.GetFileName(raw.Replace('\\', '/'))))
        throw new NotSupportedException($"CD-XA: '{raw}' cannot be removed; replace track.mpg instead.");
  }

  private static CdXaFile Build(byte[] track, CdXaFile.Layout layout, byte[]? fmt)
    => new() { Fmt = fmt ?? CdXaFile.DefaultFmt(layout.FileNumber), Data = CdXaFile.Build(track, layout) };

  private (byte[]? Track, CdXaFile.Layout Layout, byte[]? Fmt) Apply(byte[]? track, CdXaFile.Layout layout, byte[]? fmt, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (!this.CanAccept(input, out var reason)) throw new ArgumentException(reason);
      var content = input.ReadContent();
      if (Path.GetFileName(input.ArchiveName.Replace('\\', '/')).Equals("track.mpg", StringComparison.OrdinalIgnoreCase)) {
        try {
          CdXaFile.SplitPacks(content);
        } catch (InvalidDataException ex) {
          throw new ArgumentException(ex.Message, ex);
        }
        track = content;
        continue;
      }
      foreach (var line in Encoding.UTF8.GetString(content).Split('\n')) {
        var eq = line.IndexOf('=');
        if (eq <= 0) continue;
        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].TrimEnd('\r').Trim();
        layout = key switch {
          "first_sector_msf" => layout with { StartFrame = CdXaFile.ParseMsf(value) },
          "file_number" => layout with { FileNumber = byte.Parse(value, CultureInfo.InvariantCulture) },
          "lead_sectors" => layout with { LeadSectors = int.Parse(value, CultureInfo.InvariantCulture) },
          "trail_sectors" => layout with { TrailSectors = int.Parse(value, CultureInfo.InvariantCulture) },
          "record_ends" => layout with {
            RecordEnds = value switch {
              "last_sector" => CdXaFile.RecordEnds.LastSector,
              "last_of_each_stream" => CdXaFile.RecordEnds.LastOfEachStream,
              _ => throw new ArgumentException($"CD-XA: record_ends '{value}' is neither last_sector nor last_of_each_stream."),
            },
          },
          _ => layout,
        };
        if (key == "fmt") fmt = Convert.FromHexString(value) is { Length: CdXaFile.FmtSize } f ? f : throw new ArgumentException("CD-XA: fmt must be 16 bytes of hex.");
      }
    }
    if (layout.LeadSectors < 0 || layout.TrailSectors < 1) throw new ArgumentException("CD-XA: lead_sectors must be at least 0 and trail_sectors at least 1.");
    return (track, layout, fmt);
  }

  private static CdXaFile Read(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return CdXaFile.Read(ms.ToArray());
  }

  private static IEnumerable<(string Name, byte[] Data)> Entries(CdXaFile file) {
    yield return ("metadata.ini", Encoding.UTF8.GetBytes(file.RenderMetadata(file.DetectLayout())));
    yield return ("track.mpg", file.Track());
  }
}
