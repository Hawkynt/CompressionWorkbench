#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using FileFormat.FirmwareCommon;

namespace FileFormat.NetgearChk;

/// <summary>
/// NETGEAR <c>.chk</c> firmware as a pseudo-archive: <c>kernel.bin</c>, <c>rootfs.bin</c>,
/// <c>metadata.ini</c> with the board id, region and the four checksums checked.
/// </summary>
/// <remarks>
/// Create and edit write what OpenWrt's <c>mkchkimg</c> writes for the same parts, board id and
/// region (compared byte for byte in the tests); an image whose checksums do not verify, or that a
/// rewrite would not reproduce, is read but not edited.
/// </remarks>
public sealed class NetgearChkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, ISyntheticEntryNames {

  public const string KernelName = "kernel.bin", RootfsName = "rootfs.bin", TrailerName = "trailer.bin", FullImage = "FULL.chk";

  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    FirmwareArchive.MetadataName, FullImage,
  };

  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;
  public string Id => "NetgearChk";
  public string DisplayName => "CHK firmware header (NETGEAR)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".chk";
  public IReadOnlyList<string> Extensions => [".chk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x2A, 0x23, 0x24, 0x5E], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "NETGEAR .chk firmware: board id, region, kernel and rootfs with NETGEAR's Fletcher-style checksums.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => FirmwareArchive.List(Entries(FirmwareArchive.ReadAll(stream)));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => FirmwareArchive.Extract(Entries(FirmwareArchive.ReadAll(stream)), outputDir, files);

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password) => FirmwareArchive.List(Entries(archive.ToArray()));

  void IArchiveFormatOperations.ExtractSpan(ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files)
    => FirmwareArchive.Extract(Entries(archive.ToArray()), outputDir, files);

  public static List<FirmwareEntry> Entries(byte[] data) {
    NetgearChkImage img;
    try {
      img = NetgearChkImage.Parse(data);
    } catch (InvalidDataException e) {
      return [new(FirmwareArchive.MetadataName, Encoding.UTF8.GetBytes($"[chk]\nparse_status = partial\nerror = {e.Message}\n")), new(FullImage, data)];
    }
    var entries = new List<FirmwareEntry> { new(FirmwareArchive.MetadataName, RenderMetadata(img, data)) };
    if (img.Kernel.Length > 0) entries.Add(new(KernelName, img.Kernel));
    if (img.Rootfs.Length > 0) entries.Add(new(RootfsName, img.Rootfs));
    if (img.Trailer.Length > 0) entries.Add(new(TrailerName, img.Trailer));
    return entries;
  }

  private static bool IsCanonical(NetgearChkImage img, byte[] data) {
    try {
      return img.ChecksumsValid && img.ToBytes().AsSpan().SequenceEqual(data);
    } catch (InvalidOperationException) {
      return false;
    }
  }

  private static byte[] RenderMetadata(NetgearChkImage img, byte[] data) {
    var sb = new StringBuilder();
    void Line(string key, string value) => sb.Append(key).Append(" = ").Append(value).Append('\n');
    sb.Append("[chk]\n");
    Line("board_id", Encoding.Latin1.GetString(img.BoardId).TrimEnd('\0'));
    Line("region", img.Region.ToString(CultureInfo.InvariantCulture) + (img.Region switch { 1 => " (worldwide)", 2 => " (North America)", _ => "" }));
    Line("kernel_checksum", FirmwareArchive.Hex(img.StoredKernelChecksum));
    Line("rootfs_checksum", FirmwareArchive.Hex(img.StoredRootfsChecksum));
    Line("image_checksum", FirmwareArchive.Hex(img.StoredImageChecksum));
    Line("header_checksum", FirmwareArchive.Hex(img.StoredHeaderChecksum));
    Line("checksums_ok", img.ChecksumsValid ? "true" : "false");
    Line("canonical", IsCanonical(img, data) ? "true" : "false");
    Line("parse_status", "ok");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static void ApplyMetadata(NetgearChkImage img, IReadOnlyDictionary<string, string> ini) {
    var current = FirmwareArchive.ParseIni(RenderMetadata(img, []));
    foreach (var (key, value) in ini) {
      if (!key.StartsWith("chk.", StringComparison.OrdinalIgnoreCase)) continue;
      if (current.TryGetValue(key, out var existing) && existing == value) continue;
      switch (key[4..].ToLowerInvariant()) {
        case "board_id":
          var id = Encoding.Latin1.GetBytes(value);
          if (id.Length is 0 or > 64) throw new ArgumentException("metadata.ini: board_id must be 1 to 64 bytes.");
          img.BoardId = id;
          break;
        case "region":
          if (!FirmwareArchive.TryParseNumber(value, out var region) || region > 255) throw new ArgumentException($"metadata.ini: region '{value}' is not a byte.");
          img.Region = (byte)region;
          break;
      }
    }
  }

  private static string Leaf(string name) => name.Replace('\\', '/').TrimEnd('/').Split('/')[^1];

  /// <summary>An image from <c>kernel.bin</c>, an optional <c>rootfs.bin</c> and the board id and region of a <c>metadata.ini</c> (mkchkimg's defaults otherwise).</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var img = new NetgearChkImage();
    Dictionary<string, string>? ini = null;
    foreach (var input in inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.ArchiveName))) {
      var name = Leaf(input.ArchiveName);
      if (name.Equals(FirmwareArchive.MetadataName, StringComparison.OrdinalIgnoreCase)) ini = FirmwareArchive.ParseIni(input.ReadContent());
      else if (name.Equals(KernelName, StringComparison.OrdinalIgnoreCase)) img.Kernel = input.ReadContent();
      else if (name.Equals(RootfsName, StringComparison.OrdinalIgnoreCase)) img.Rootfs = input.ReadContent();
      else if (name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase)) img.Trailer = input.ReadContent();
      else throw new ArgumentException($"CHK: '{name}' is not a CHK member (kernel.bin, rootfs.bin, trailer.bin, metadata.ini).");
    }
    if (ini is not null) ApplyMetadata(img, ini);
    output.Write(ToBytes(img));
  }

  private static byte[] ToBytes(NetgearChkImage img) {
    try {
      return img.ToBytes();
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
      else if (name.Equals(KernelName, StringComparison.OrdinalIgnoreCase)) img.Kernel = input.ReadContent();
      else if (name.Equals(RootfsName, StringComparison.OrdinalIgnoreCase)) img.Rootfs = input.ReadContent();
      else if (name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase)) img.Trailer = input.ReadContent();
      else throw new ArgumentException($"CHK: '{name}' is not a CHK member.");
    }
    Commit(archive, img);
  }

  public void Remove(Stream archive, string[] entryNames) {
    var names = (entryNames ?? []).Select(Leaf).Where(n => !Synthetic.Contains(n)).ToList();
    if (names.Count == 0) return;
    var img = OpenForEdit(archive);
    foreach (var name in names) {
      if (name.Equals(KernelName, StringComparison.OrdinalIgnoreCase)) img.Kernel = [];
      else if (name.Equals(RootfsName, StringComparison.OrdinalIgnoreCase)) img.Rootfs = [];
      else if (name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase)) img.Trailer = [];
    }
    Commit(archive, img);
  }

  public void Purge(Stream archive) {
    var img = OpenForEdit(archive);
    img.Kernel = img.Rootfs = img.Trailer = [];
    Commit(archive, img);
  }

  private static NetgearChkImage OpenForEdit(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("CHK editing requires a readable, writable, seekable stream.", nameof(archive));
    var data = FirmwareArchive.ReadAll(archive);
    var img = NetgearChkImage.Parse(data);
    if (!img.ChecksumsValid) throw new NotSupportedException("CHK: the stored checksums do not verify; the image is not rewritten.");
    if (!IsCanonical(img, data)) throw new NotSupportedException("CHK: a rewrite would not reproduce this image's bytes; it is not re-laid out.");
    return img;
  }

  private static void Commit(Stream archive, NetgearChkImage img) {
    var bytes = ToBytes(img);
    WholeImageRebuildCommitter.Replace(archive, c => c.Write(bytes), c => {
      if (!NetgearChkImage.Parse(FirmwareArchive.ReadAll(c)).ChecksumsValid) throw new InvalidDataException("CHK: rewritten image fails its own checksums.");
    });
  }
}
