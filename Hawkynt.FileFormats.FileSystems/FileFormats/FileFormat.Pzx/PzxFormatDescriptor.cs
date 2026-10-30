#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pzx;

/// <summary>
/// PZX ("Perfect ZX Tape") ZX Spectrum tape image. Lists every block losslessly as
/// <c>blocks/NNN_TAG.bin</c>, the data blocks that use the standard ROM loader encoding as a
/// ready-to-use <c>tape.tap</c>, and the header as <c>metadata.ini</c>. Creates tapes from a ZX
/// TAP file (<c>*.tap</c>) or from single raw data blocks, encoded exactly as pzxtools'
/// <c>tap2pzx</c> encodes them.
/// </summary>
/// <remarks>Specification: Patrik Rak, PZX format 1.0 (pzxtools <c>docs/pzx_format.txt</c>).</remarks>
public sealed class PzxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, ISyntheticEntryNames {
  public const string MetadataName = "metadata.ini";
  public const string TapName = "tape.tap";
  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MetadataName, TapName };

  public string Id => "Pzx";
  public string DisplayName => "PZX (ZX Spectrum tape)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".pzx";
  public IReadOnlyList<string> Extensions => [".pzx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("PZXT"u8.ToArray(), Confidence: 0.9)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("pulses", "Pulse/data blocks")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PZX 1.0 ZX Spectrum tape (pulse, data, pause, browse and stop blocks); standard-loader blocks as TAP";
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      return Entries(Read(stream)).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null)).ToList();
    } catch (InvalidDataException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.pzx", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (name, data) in Entries(Read(stream)))
      if (files is not { Length: > 0 } || MatchesFilter(name, files))
        WriteFile(outputDir, name, data);
  }

  /// <summary>
  /// <c>*.tap</c> inputs contribute their blocks; any other file is one data block (flag byte,
  /// payload, checksum — written as given). Blocks are written in input order.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    var blocks = new List<byte[]>();
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      var leaf = Path.GetFileName(input.ArchiveName.Replace('\\', '/'));
      if (leaf.Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) continue;
      var data = input.ReadContent();
      if (leaf.EndsWith(".tap", StringComparison.OrdinalIgnoreCase)) blocks.AddRange(PzxTape.SplitTap(data));
      else if (data.Length > 0) blocks.Add(data);
    }
    PzxTape.WriteStandard(output, blocks);
  }

  private static List<PzxBlock> Read(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return PzxTape.Read(ms.ToArray());
  }

  private static IEnumerable<(string Name, byte[] Data)> Entries(List<PzxBlock> blocks) {
    yield return (MetadataName, Metadata(blocks));
    var standard = blocks.Select(PzxTape.StandardData).Where(d => d is not null).Select(d => d!).ToList();
    if (standard.Count > 0) yield return (TapName, PzxTape.JoinTap(standard));
    for (var i = 0; i < blocks.Count; ++i)
      yield return ($"blocks/{i:D3}_{SafeTag(blocks[i].Tag)}.bin", blocks[i].Data);
  }

  private static string SafeTag(string tag) => new(tag.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

  private static byte[] Metadata(List<PzxBlock> blocks) {
    var inv = CultureInfo.InvariantCulture;
    var sb = new StringBuilder("[pzx]\n");
    var (major, minor, title, info) = PzxTape.Header(blocks[0]);
    sb.Append(inv, $"version={major}.{minor}\n");
    if (title is not null) sb.Append("title=").Append(title).Append('\n');
    foreach (var (k, v) in info) sb.Append(k.ToLowerInvariant()).Append('=').Append(v).Append('\n');
    sb.Append(inv, $"blocks={blocks.Count}\nstandard_data_blocks={blocks.Count(b => PzxTape.StandardData(b) is not null)}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
