#pragma warning disable CS1591
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.FirmwareCommon;

namespace FileFormat.Seama;

/// <summary>
/// D-Link SEAMA firmware (single entity, or a seal of entities — binwalk's DLOB) as a pseudo-archive:
/// <c>image.bin</c> for a single entity, <c>entity_NN.bin</c> for each entity of a seal, and
/// <c>metadata.ini</c> carrying every meta string and the MD5 check.
/// </summary>
/// <remarks>
/// Create and edit write what OpenWrt's <c>seama</c> writes for the same image and meta — the
/// tests compare bytes with images it produced. An image whose MD5 is wrong, or which a rewrite
/// would not reproduce, is read but not edited.
/// </remarks>
public sealed class SeamaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, ISyntheticEntryNames {

  public const string ImageName = "image.bin", TrailerName = "trailer.bin", FullImage = "FULL.seama";

  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    FirmwareArchive.MetadataName, FullImage,
  };

  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;
  public string Id => "Seama";
  public string DisplayName => "SEAMA firmware";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".seama";
  public IReadOnlyList<string> Extensions => [".seama", ".dlob"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x5E, 0xA3, 0xA4, 0x17, 0x00, 0x00], Confidence: 0.85),
    new([0x17, 0xA4, 0xA3, 0x5E, 0x00, 0x00], Confidence: 0.8),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "D-Link SEAMA firmware: MD5-checked image with key=value meta strings, or a seal (DLOB) of several entities.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => FirmwareArchive.List(Entries(FirmwareArchive.ReadAll(stream)));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => FirmwareArchive.Extract(Entries(FirmwareArchive.ReadAll(stream)), outputDir, files);

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password) => FirmwareArchive.List(Entries(archive.ToArray()));

  void IArchiveFormatOperations.ExtractSpan(ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files)
    => FirmwareArchive.Extract(Entries(archive.ToArray()), outputDir, files);

  public static string EntityName(int index) => "entity_" + index.ToString("D2", CultureInfo.InvariantCulture) + ".bin";

  public static List<FirmwareEntry> Entries(byte[] data) {
    SeamaImage img;
    try {
      img = SeamaImage.Parse(data);
    } catch (InvalidDataException e) {
      return [new(FirmwareArchive.MetadataName, Encoding.UTF8.GetBytes($"[seama]\nparse_status = partial\nerror = {e.Message}\n")), new(FullImage, data)];
    }
    var entries = new List<FirmwareEntry> { new(FirmwareArchive.MetadataName, RenderMetadata(img, data)) };
    if (img.IsSeal)
      for (var i = 0; i < img.Entities.Count; ++i) entries.Add(new(EntityName(i), img.Entities[i].Image));
    else if (img.Entities[0].Image.Length > 0)
      entries.Add(new(ImageName, img.Entities[0].Image));
    if (img.Trailer.Length > 0) entries.Add(new(TrailerName, img.Trailer));
    return entries;
  }

  private static bool IsCanonical(SeamaImage img, byte[] data) {
    try {
      return img.Entities.All(e => e.Md5Valid) && img.ToBytes().AsSpan().SequenceEqual(data);
    } catch (InvalidOperationException) {
      return false;
    }
  }

  private static byte[] RenderMetadata(SeamaImage img, byte[] data) {
    var sb = new StringBuilder();
    void Line(string key, string value) => sb.Append(key).Append(" = ").Append(value).Append('\n');
    void Meta(List<string> meta) {
      for (var i = 0; i < meta.Count; ++i) Line("meta_" + i.ToString(CultureInfo.InvariantCulture), meta[i]);
    }
    sb.Append("[seama]\n");
    Line("kind", img.IsSeal ? "seal" : "entity");
    Line("endianness", img.BigEndian ? "big" : "little");
    if (img.SealMeta is { } seal) {
      Meta(seal);
      Line("entities", img.Entities.Count.ToString(CultureInfo.InvariantCulture));
    } else {
      Meta(img.Entities[0].Meta);
      RenderMd5(img.Entities[0]);
    }
    Line("canonical", IsCanonical(img, data) ? "true" : "false");
    Line("parse_status", "ok");
    if (img.IsSeal)
      for (var i = 0; i < img.Entities.Count; ++i) {
        sb.Append("[entity_").Append(i.ToString("D2", CultureInfo.InvariantCulture)).Append("]\n");
        Meta(img.Entities[i].Meta);
        RenderMd5(img.Entities[i]);
      }
    return Encoding.UTF8.GetBytes(sb.ToString());

    void RenderMd5(SeamaEntity e) {
      Line("image_size", e.Image.Length.ToString(CultureInfo.InvariantCulture));
      if (e.Image.Length == 0) return;
      Line("md5_stored", Convert.ToHexStringLower(e.StoredMd5));
      Line("md5_computed", Convert.ToHexStringLower(MD5.HashData(e.Image)));
      Line("md5_ok", e.Md5Valid ? "true" : "false");
    }
  }

  /// <summary>The meta strings of one INI section, in <c>meta_N</c> order, or null when the section sets none.</summary>
  private static List<string>? MetaOf(IReadOnlyDictionary<string, string> ini, string section) {
    var found = new SortedDictionary<int, string>();
    var prefix = section + ".meta_";
    foreach (var (key, value) in ini)
      if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
          && int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        found[index] = value;
    return found.Count == 0 ? null : [.. found.Values];
  }

  private static bool IsKnownName(string name)
    => TryEntityIndex(name, out _) || name.Equals(ImageName, StringComparison.OrdinalIgnoreCase) || name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase)
      || name.Equals(FirmwareArchive.MetadataName, StringComparison.OrdinalIgnoreCase) || name.Equals(FullImage, StringComparison.OrdinalIgnoreCase);

  private static bool TryEntityIndex(string name, out int index) {
    index = -1;
    return name.StartsWith("entity_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
      && int.TryParse(name.AsSpan(7, name.Length - 11), NumberStyles.None, CultureInfo.InvariantCulture, out index);
  }

  private static string Leaf(string name) => FirmwareArchive.LeafName(name);

  private static void ApplyMetadata(SeamaImage img, IReadOnlyDictionary<string, string> ini) {
    if (ini.TryGetValue("seama.endianness", out var endian)) img.BigEndian = !endian.Trim().Equals("little", StringComparison.OrdinalIgnoreCase);
    var top = MetaOf(ini, "seama");
    if (img.IsSeal) {
      if (top is not null) img.SealMeta = top;
      for (var i = 0; i < img.Entities.Count; ++i)
        if (MetaOf(ini, "entity_" + i.ToString("D2", CultureInfo.InvariantCulture)) is { } meta) img.Entities[i].Meta = meta;
    } else if (top is not null) img.Entities[0].Meta = top;
  }

  /// <summary>
  /// A single entity from <c>image.bin</c>, or a seal from <c>entity_00.bin</c>, <c>entity_01.bin</c>…;
  /// meta strings come from a <c>metadata.ini</c> (<c>meta_0</c>, <c>meta_1</c>… under <c>[seama]</c>
  /// for the image or the seal, under <c>[entity_NN]</c> for each sealed entity). A single input
  /// under any other name is the payload and becomes <c>image.bin</c>, as <c>seama -i</c> would take it.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    inputs = FirmwareArchive.FoldLonePayload(inputs, IsKnownName, ImageName);
    byte[]? single = null;
    byte[] trailer = [];
    var entities = new SortedDictionary<int, byte[]>();
    Dictionary<string, string>? ini = null;
    foreach (var input in inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.ArchiveName))) {
      var name = Leaf(input.ArchiveName);
      if (name.Equals(FirmwareArchive.MetadataName, StringComparison.OrdinalIgnoreCase)) ini = FirmwareArchive.ParseIni(input.ReadContent());
      else if (name.Equals(ImageName, StringComparison.OrdinalIgnoreCase)) single = input.ReadContent();
      else if (TryEntityIndex(name, out var index)) entities[index] = input.ReadContent();
      else if (name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase)) trailer = input.ReadContent();
      else throw new ArgumentException($"SEAMA: '{name}' is not a SEAMA member (image.bin, entity_NN.bin, trailer.bin, metadata.ini).");
    }
    if (single is not null && entities.Count > 0) throw new ArgumentException("SEAMA: give either image.bin (one entity) or entity_NN.bin (a seal), not both.");
    if (entities.Keys.Select((k, i) => k != i).Any(b => b)) throw new ArgumentException("SEAMA: entities must be numbered entity_00.bin, entity_01.bin, ... without gaps.");
    var sealRequested = entities.Count > 0 || ini is not null && ini.TryGetValue("seama.kind", out var kind) && kind.Trim().Equals("seal", StringComparison.OrdinalIgnoreCase);
    var img = new SeamaImage { Trailer = trailer };
    if (sealRequested) {
      img.SealMeta = [];
      img.Entities = [.. entities.Values.Select(e => new SeamaEntity { Image = e })];
    } else
      img.Entities = [new SeamaEntity { Image = single ?? [] }];
    if (ini is not null) ApplyMetadata(img, ini);
    output.Write(ToBytes(img));
  }

  private static byte[] ToBytes(SeamaImage img) {
    try {
      return img.ToBytes();
    } catch (InvalidOperationException e) {
      throw new ArgumentException(e.Message, e);
    }
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    var img = OpenForEdit(archive);
    Dictionary<string, string>? ini = null;
    foreach (var input in inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.ArchiveName))) {
      var name = Leaf(input.ArchiveName);
      if (name.Equals(FirmwareArchive.MetadataName, StringComparison.OrdinalIgnoreCase)) ini = FirmwareArchive.ParseIni(input.ReadContent());
      else if (name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase)) img.Trailer = input.ReadContent();
      else if (!img.IsSeal && name.Equals(ImageName, StringComparison.OrdinalIgnoreCase)) img.Entities[0].Image = input.ReadContent();
      else if (img.IsSeal && TryEntityIndex(name, out var index)) {
        if (index < img.Entities.Count) img.Entities[index].Image = input.ReadContent();
        else if (index == img.Entities.Count) img.Entities.Add(new SeamaEntity { Image = input.ReadContent() });
        else throw new ArgumentException($"SEAMA: {name} would leave a gap after {img.Entities.Count} entities.");
      } else throw new ArgumentException($"SEAMA: '{name}' is not a member of this {(img.IsSeal ? "seal" : "single-entity image")}.");
    }
    // Meta last, so it can name an entity added in the same call.
    if (ini is not null) ApplyMetadata(img, ini);
    Commit(archive, img);
  }

  public void Remove(Stream archive, string[] entryNames) {
    var names = (entryNames ?? []).Select(Leaf).Where(n => !Synthetic.Contains(n)).ToList();
    if (names.Count == 0) return;
    var img = OpenForEdit(archive);
    var drop = new List<int>();
    foreach (var name in names) {
      if (name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase)) img.Trailer = [];
      else if (!img.IsSeal && name.Equals(ImageName, StringComparison.OrdinalIgnoreCase)) img.Entities[0].Image = [];
      else if (img.IsSeal && TryEntityIndex(name, out var index) && index < img.Entities.Count) drop.Add(index);
    }
    foreach (var index in drop.Distinct().OrderDescending()) img.Entities.RemoveAt(index);
    Commit(archive, img);
  }

  /// <summary>Empties the image (single entity) or drops every entity (seal), keeping the meta strings.</summary>
  public void Purge(Stream archive) {
    var img = OpenForEdit(archive);
    if (img.IsSeal) img.Entities.Clear();
    else img.Entities[0].Image = [];
    img.Trailer = [];
    Commit(archive, img);
  }

  private static SeamaImage OpenForEdit(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("SEAMA editing requires a readable, writable, seekable stream.", nameof(archive));
    var data = FirmwareArchive.ReadAll(archive);
    var img = SeamaImage.Parse(data);
    if (!img.Entities.All(e => e.Md5Valid)) throw new NotSupportedException("SEAMA: a stored MD5 does not match its image; the image is not rewritten.");
    if (!IsCanonical(img, data)) throw new NotSupportedException("SEAMA: a rewrite would not reproduce this image's bytes (padding or reserved fields differ); it is not re-laid out.");
    return img;
  }

  private static void Commit(Stream archive, SeamaImage img) {
    var bytes = ToBytes(img);
    WholeImageRebuildCommitter.Replace(archive, c => c.Write(bytes), c => _ = SeamaImage.Parse(FirmwareArchive.ReadAll(c)));
  }
}
