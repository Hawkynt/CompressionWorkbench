#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using FileFormat.FirmwareCommon;

namespace FileFormat.Trx;

/// <summary>
/// Broadcom TRX firmware image (<c>HDR0</c>) as a pseudo-archive: <c>partition_N.bin</c> per
/// partition (the names binwalk's extractor gives them), <c>metadata.ini</c> for the header and
/// CRC check, <c>trailer.bin</c> for anything after the declared length.
/// </summary>
/// <remarks>
/// Create and edit lay partitions out at their alignment (4 bytes for new ones, the original
/// power of two for existing ones) and recompute the CRC; binwalk only recognises a TRX whose CRC
/// verifies, which is what the oracle test asks of our output. An image whose CRC is wrong, or
/// whose layout a rewrite would not reproduce, is read but not edited.
/// </remarks>
public sealed class TrxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, ISyntheticEntryNames {

  public const string TrailerName = "trailer.bin", FullImage = "FULL.trx";

  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    FirmwareArchive.MetadataName, FullImage,
  };

  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;
  public string Id => "Trx";
  public string DisplayName => "TRX firmware";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".trx";
  public IReadOnlyList<string> Extensions => [".trx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("HDR0"u8.ToArray(), Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Broadcom TRX firmware (HDR0): CRC-32-checked header with up to three (v1) or four (v2) partitions.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => FirmwareArchive.List(Entries(FirmwareArchive.ReadAll(stream)));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => FirmwareArchive.Extract(Entries(FirmwareArchive.ReadAll(stream)), outputDir, files);

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password) => FirmwareArchive.List(Entries(archive.ToArray()));

  void IArchiveFormatOperations.ExtractSpan(ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files)
    => FirmwareArchive.Extract(Entries(archive.ToArray()), outputDir, files);

  public static string PartitionName(int index) => "partition_" + index.ToString(CultureInfo.InvariantCulture) + ".bin";

  public static List<FirmwareEntry> Entries(byte[] data) {
    TrxImage img;
    try {
      img = TrxImage.Parse(data);
    } catch (InvalidDataException e) {
      return [new(FirmwareArchive.MetadataName, Encoding.UTF8.GetBytes($"[trx]\nparse_status = partial\nerror = {e.Message}\n")), new(FullImage, data)];
    }
    var entries = new List<FirmwareEntry> { new(FirmwareArchive.MetadataName, RenderMetadata(img, data)) };
    for (var i = 0; i < img.Partitions.Count; ++i) entries.Add(new(PartitionName(i), img.Partitions[i]));
    if (img.Trailer.Length > 0) entries.Add(new(TrailerName, img.Trailer));
    return entries;
  }

  private static bool IsCanonical(TrxImage img, byte[] data) => img.CrcValid && img.ToBytes().AsSpan().SequenceEqual(data);

  private static byte[] RenderMetadata(TrxImage img, byte[] data) {
    var sb = new StringBuilder();
    void Line(string key, string value) => sb.Append(key).Append(" = ").Append(value).Append('\n');
    sb.Append("[trx]\n");
    Line("version", img.Version.ToString(CultureInfo.InvariantCulture));
    Line("flags", FirmwareArchive.Hex(img.Flags));
    Line("length", img.Length.ToString(CultureInfo.InvariantCulture));
    Line("crc_stored", FirmwareArchive.Hex(img.StoredCrc));
    Line("crc_computed", FirmwareArchive.Hex(img.ComputedCrc));
    Line("crc_ok", img.CrcValid ? "true" : "false");
    Line("offsets", string.Join(',', img.Offsets.Select(o => FirmwareArchive.Hex(o))));
    Line("length_alignment", img.LengthAlignment.ToString(CultureInfo.InvariantCulture));
    for (var i = 0; i < img.Alignments.Count; ++i) Line("alignment_" + i.ToString(CultureInfo.InvariantCulture), img.Alignments[i].ToString(CultureInfo.InvariantCulture));
    Line("canonical", IsCanonical(img, data) ? "true" : "false");
    Line("parse_status", "ok");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static void ApplyMetadata(TrxImage img, IReadOnlyDictionary<string, string> ini) {
    foreach (var (qualified, value) in ini) {
      if (!qualified.StartsWith("trx.", StringComparison.OrdinalIgnoreCase)) continue;
      var key = qualified[4..].ToLowerInvariant();
      if (key == "version") img.Version = (ushort)Number(key, value, 2, min: 1);
      else if (key == "flags") img.Flags = (ushort)Number(key, value, ushort.MaxValue);
      else if (key == "length_alignment") {
        var align = (int)Number(key, value, 0x100000, min: 4);
        if ((align & (align - 1)) != 0) throw new ArgumentException($"metadata.ini: {key} {align} is not a power of two.");
        img.LengthAlignment = align;
      } else if (key.StartsWith("alignment_", StringComparison.Ordinal)
               && int.TryParse(key.AsSpan("alignment_".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < 4) {
        var align = (int)Number(key, value, 0x100000, min: 1);
        if ((align & (align - 1)) != 0) throw new ArgumentException($"metadata.ini: {key} {align} is not a power of two.");
        while (img.Alignments.Count <= index) img.Alignments.Add(4);
        img.Alignments[index] = align;
      }
    }
  }

  private static ulong Number(string key, string value, ulong max, ulong min = 0)
    => FirmwareArchive.TryParseNumber(value, out var n) && n <= max && n >= min ? n : throw new ArgumentException($"metadata.ini: {key} '{value}' is out of range.");

  private static bool TryPartitionIndex(string name, out int index) {
    index = -1;
    return name.StartsWith("partition_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
      && int.TryParse(name.AsSpan(10, name.Length - 14), NumberStyles.None, CultureInfo.InvariantCulture, out index);
  }

  private static string Leaf(string name) => name.Replace('\\', '/').TrimEnd('/').Split('/')[^1];

  /// <summary>Builds an image from <c>partition_0.bin</c>… (contiguous from 0), an optional <c>trailer.bin</c> and the version, flags and alignments of a <c>metadata.ini</c>.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var img = new TrxImage();
    var partitions = new SortedDictionary<int, byte[]>();
    Dictionary<string, string>? ini = null;
    foreach (var input in inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.ArchiveName))) {
      var name = Leaf(input.ArchiveName);
      if (name.Equals(FirmwareArchive.MetadataName, StringComparison.OrdinalIgnoreCase)) ini = FirmwareArchive.ParseIni(input.ReadContent());
      else if (TryPartitionIndex(name, out var index)) partitions[index] = input.ReadContent();
      else if (name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase)) img.Trailer = input.ReadContent();
      else throw new ArgumentException($"TRX: '{name}' is not a TRX member (partition_N.bin, trailer.bin, metadata.ini).");
    }
    if (partitions.Keys.Select((k, i) => k != i).Any(b => b))
      throw new ArgumentException("TRX: partitions must be numbered partition_0.bin, partition_1.bin, ... without gaps.");
    img.Partitions = [.. partitions.Values];
    if (ini is not null) ApplyMetadata(img, ini);
    Write(output, img);
  }

  private static void Write(Stream output, TrxImage img) {
    try {
      output.Write(img.ToBytes());
    } catch (InvalidOperationException e) {
      throw new ArgumentException(e.Message, e);
    }
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    var img = OpenForEdit(archive);
    foreach (var input in inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.ArchiveName))) {
      var name = Leaf(input.ArchiveName);
      if (name.Equals(FirmwareArchive.MetadataName, StringComparison.OrdinalIgnoreCase)) ApplyMetadata(img, FirmwareArchive.ParseIni(input.ReadContent()));
      else if (TryPartitionIndex(name, out var index)) {
        if (index < img.Partitions.Count) img.Partitions[index] = input.ReadContent();
        else if (index == img.Partitions.Count) {
          img.Partitions.Add(input.ReadContent());
          if (img.Alignments.Count < img.Partitions.Count) img.Alignments.Add(4);
        } else throw new ArgumentException($"TRX: {name} would leave a gap after {img.Partitions.Count} partitions.");
      } else if (name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase)) img.Trailer = input.ReadContent();
      else throw new ArgumentException($"TRX: '{name}' is not a TRX member.");
    }
    Commit(archive, img);
  }

  public void Remove(Stream archive, string[] entryNames) {
    var names = (entryNames ?? []).Select(Leaf).Where(n => !Synthetic.Contains(n)).ToList();
    if (names.Count == 0) return;
    var img = OpenForEdit(archive);
    var drop = new List<int>();
    foreach (var name in names) {
      if (TryPartitionIndex(name, out var index) && index < img.Partitions.Count) drop.Add(index);
      else if (name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase)) img.Trailer = [];
    }
    foreach (var index in drop.Distinct().OrderDescending()) {
      img.Partitions.RemoveAt(index);
      if (index < img.Alignments.Count) img.Alignments.RemoveAt(index);
    }
    Commit(archive, img);
  }

  /// <summary>A TRX must carry partition data — its length has to exceed the header — so there is no empty image to purge to.</summary>
  public bool CanPurgeToEmpty => false;

  public void Purge(Stream archive)
    => throw new NotSupportedException("TRX: an image needs at least one non-empty partition; there is no empty TRX to purge to.");

  private static TrxImage OpenForEdit(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("TRX editing requires a readable, writable, seekable stream.", nameof(archive));
    var data = FirmwareArchive.ReadAll(archive);
    var img = TrxImage.Parse(data);
    if (!img.CrcValid) throw new NotSupportedException("TRX: the stored CRC does not match; the image is not rewritten.");
    if (!IsCanonical(img, data)) throw new NotSupportedException("TRX: a rewrite would move partitions this image places elsewhere; it is not re-laid out.");
    return img;
  }

  private static void Commit(Stream archive, TrxImage img) {
    byte[] bytes;
    try {
      bytes = img.ToBytes();
    } catch (InvalidOperationException e) {
      throw new ArgumentException(e.Message, e);
    }
    WholeImageRebuildCommitter.Replace(archive, c => c.Write(bytes), c => {
      if (!TrxImage.Parse(FirmwareArchive.ReadAll(c)).CrcValid) throw new InvalidDataException("TRX: rewritten image fails its own CRC.");
    });
  }
}
