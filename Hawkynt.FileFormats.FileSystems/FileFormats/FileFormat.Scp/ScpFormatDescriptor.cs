#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Scp;

/// <summary>
/// SuperCard Pro flux image. Every track is listed as <c>tracks/track_NNN.bin</c> (its own TRK
/// block: revolution table plus flux, self-contained), the header as <c>metadata.ini</c>, the
/// extension area and footer verbatim. Tracks can be added, replaced and removed; the image is
/// rebuilt with a fresh track table and checksum.
/// </summary>
public sealed partial class ScpFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
    IArchiveModifiable, IArchiveWriteConstraints, ISyntheticEntryNames {
  public const string MetadataName = "metadata.ini";
  public const string ExtensionName = "extension.bin";
  public const string FooterName = "footer.bin";
  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MetadataName, FooterName };

  [GeneratedRegex(@"^(?:.*/)?track_(\d{1,3})\.bin$", RegexOptions.IgnoreCase)]
  private static partial Regex TrackName();

  public string Id => "Scp";
  public string DisplayName => "SuperCard Pro flux image";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".scp";
  public IReadOnlyList<string> Extensions => [".scp"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("SCP"u8.ToArray(), Confidence: 0.6)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("flux", "Flux transitions")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "SuperCard Pro (SCP) floppy flux image: per-track revolutions of 16-bit flux intervals";
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription => "accepts: tracks/track_NNN.bin (TRK flux blocks), metadata.ini, extension.bin";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = input.ArchiveName.Replace('\\', '/');
    var leaf = Path.GetFileName(name);
    if (TrackName().IsMatch(name) || leaf.Equals(MetadataName, StringComparison.OrdinalIgnoreCase) || leaf.Equals(ExtensionName, StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }
    reason = $"'{input.ArchiveName}' is not an SCP member; {this.AcceptedInputsDescription}";
    return false;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      return Entries(Read(stream)).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength, "flux", false, false, null)).ToList();
    } catch (InvalidDataException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.scp", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (name, data) in Entries(Read(stream)))
      if (files is not { Length: > 0 } || MatchesFilter(name, files))
        WriteFile(outputDir, name, data);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    Apply(new ScpImage(), inputs).Write(output);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    var image = Apply(Read(archive), inputs);
    WholeImageRebuildCommitter.Replace(archive, image.Write, candidate => Read(candidate));
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var image = Read(archive);
    var tracks = new SortedDictionary<int, byte[]>(image.Tracks);
    var extension = image.Extension;
    foreach (var raw in entryNames) {
      var name = raw.Replace('\\', '/');
      var m = TrackName().Match(name);
      if (m.Success) tracks.Remove(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
      else if (Path.GetFileName(name).Equals(ExtensionName, StringComparison.OrdinalIgnoreCase)) extension = [];
      else if (!Synthetic.Contains(Path.GetFileName(name))) throw new NotSupportedException($"SCP has no entry '{raw}'.");
    }
    var rebuilt = Clone(image, tracks, extension);
    WholeImageRebuildCommitter.Replace(archive, rebuilt.Write, candidate => Read(candidate));
  }

  private static ScpImage Clone(ScpImage s, SortedDictionary<int, byte[]> tracks, byte[] extension) => new() {
    Version = s.Version, DiskType = s.DiskType, Revolutions = s.Revolutions, Flags = s.Flags, BitCellWidth = s.BitCellWidth,
    Heads = s.Heads, Resolution = s.Resolution, Extension = extension, Tracks = tracks,
  };

  private ScpImage Apply(ScpImage image, IReadOnlyList<ArchiveInputInfo> inputs) {
    var tracks = new SortedDictionary<int, byte[]>(image.Tracks);
    var extension = image.Extension;
    byte[]? metadata = null;
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (!this.CanAccept(input, out var reason)) throw new ArgumentException(reason);
      var name = input.ArchiveName.Replace('\\', '/');
      var leaf = Path.GetFileName(name);
      if (leaf.Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) metadata = input.ReadContent();
      else if (leaf.Equals(ExtensionName, StringComparison.OrdinalIgnoreCase)) extension = input.ReadContent();
      else tracks[int.Parse(TrackName().Match(name).Groups[1].Value, CultureInfo.InvariantCulture)] = input.ReadContent();
    }
    var result = Clone(image, tracks, extension);
    if (metadata is not null) result = result.WithMetadata(metadata);
    if (result.Revolutions == 0 && tracks.Count > 0) {
      // A fresh image: the revolution count is what the first track's table holds.
      var first = tracks.Values.First();
      if (first.Length < 16) throw new ArgumentException("SCP: a track blob is shorter than one revolution entry.");
      var revs = (BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(12)) - 4) / 12;
      result = new ScpImage {
        Version = result.Version, DiskType = result.DiskType, Revolutions = (byte)revs, Flags = result.Flags,
        BitCellWidth = result.BitCellWidth, Heads = result.Heads, Resolution = result.Resolution, Extension = extension, Tracks = tracks,
      };
    }
    return result;
  }

  private static ScpImage Read(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ScpImage.Read(ms.ToArray());
  }

  private static IEnumerable<(string Name, byte[] Data)> Entries(ScpImage image) {
    yield return (MetadataName, image.RenderMetadata());
    if (image.Extension.Length > 0) yield return (ExtensionName, image.Extension);
    if (image.Footer.Length > 0) yield return (FooterName, image.Footer);
    foreach (var (t, blob) in image.Tracks)
      yield return ($"tracks/track_{t:D3}.bin", blob);
  }
}
