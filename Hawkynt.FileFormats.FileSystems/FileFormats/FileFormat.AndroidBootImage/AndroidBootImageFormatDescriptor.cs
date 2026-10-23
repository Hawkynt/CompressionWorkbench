#pragma warning disable CS1591
using System.Globalization;
using Compression.Registry;
using FileFormat.FirmwareCommon;

namespace FileFormat.AndroidBootImage;

/// <summary>
/// Android boot image — <c>boot.img</c> / <c>recovery.img</c> (magic <c>ANDROID!</c>, header
/// v0–v4) and <c>vendor_boot.img</c> (magic <c>VNDRBOOT</c>, header v3–v4) — as a pseudo-archive
/// whose members are the components <c>unpack_bootimg</c> extracts, under the names it gives them:
/// <c>kernel</c>, <c>ramdisk</c>, <c>second</c>, <c>recovery_dtbo</c>, <c>dtb</c>,
/// <c>boot_signature</c>, <c>vendor_ramdisk</c> / <c>vendor_ramdiskNN</c>, <c>bootconfig</c>.
/// <c>metadata.ini</c> renders the header; bytes after the last component (partition padding, an
/// AVB footer) surface as <c>trailer.bin</c>.
/// </summary>
/// <remarks>
/// <para>Create and edit write exactly what AOSP <c>mkbootimg</c> writes for the same inputs and
/// header fields, image id included; the oracle tests compare the bytes. An edit refuses an image
/// whose content is signed — a v4 boot signature or an AVB footer would go stale — unless the edit
/// is removing that signature, and it refuses a layout <c>mkbootimg</c> could not have produced.</para>
/// <para>Reference: <c>https://source.android.com/docs/core/architecture/bootloader/boot-image-header</c>;
/// AOSP <c>system/tools/mkbootimg</c> (Apache-2.0), converted.</para>
/// </remarks>
public sealed class AndroidBootImageFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, ISyntheticEntryNames {

  public const string Kernel = "kernel", Ramdisk = "ramdisk", Second = "second", RecoveryDtbo = "recovery_dtbo",
    Dtb = "dtb", BootSignature = "boot_signature", VendorRamdisk = "vendor_ramdisk", Bootconfig = "bootconfig",
    Trailer = "trailer.bin", FullImage = "FULL.img";

  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    FirmwareArchive.MetadataName, FullImage,
  };

  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;
  public string Id => "AndroidBootImage";
  public string DisplayName => "Android boot image";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".bootimg";
  /// <remarks><c>.img</c> belongs to every disk-image format; detection is by magic.</remarks>
  public IReadOnlyList<string> Extensions => [".bootimg"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ANDROID!"u8.ToArray(), Confidence: 0.95),
    new("VNDRBOOT"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Android boot / recovery (header v0-v4) and vendor_boot (v3-v4) image: kernel, ramdisks, second stage, DTB, " +
    "recovery DTBO, bootconfig and boot signature, laid out and identified exactly as mkbootimg does.";

  // ── read ─────────────────────────────────────────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => FirmwareArchive.List(Entries(FirmwareArchive.ReadAll(stream)));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => FirmwareArchive.Extract(Entries(FirmwareArchive.ReadAll(stream)), outputDir, files);

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password)
    => FirmwareArchive.List(Entries(archive.ToArray()));

  void IArchiveFormatOperations.ExtractSpan(ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files)
    => FirmwareArchive.Extract(Entries(archive.ToArray()), outputDir, files);

  /// <summary>The members of <paramref name="data"/>; an image that does not parse lists as itself plus the reason.</summary>
  public static List<FirmwareEntry> Entries(byte[] data) {
    AndroidBootImage img;
    try {
      img = AndroidBootImage.Parse(data);
    } catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException) {
      return [new(FirmwareArchive.MetadataName, AndroidBootImageMetadata.RenderPartial(e.Message)), new(FullImage, data)];
    }
    var entries = new List<FirmwareEntry> { new(FirmwareArchive.MetadataName, AndroidBootImageMetadata.Render(img)) };
    void Add(string name, byte[] part) {
      if (part.Length > 0) entries.Add(new(name, part));
    }
    if (img.IsVendor) {
      if (img.HeaderVersion < 4) Add(VendorRamdisk, img.VendorRamdisks.Count > 0 ? img.VendorRamdisks[0].Data : []);
      else
        for (var i = 0; i < img.VendorRamdisks.Count; ++i)
          entries.Add(new(AndroidBootImageMetadata.RamdiskEntryName(i), img.VendorRamdisks[i].Data));
      Add(Dtb, img.Dtb);
      Add(Bootconfig, img.Bootconfig);
    } else {
      Add(Kernel, img.Kernel);
      Add(Ramdisk, img.Ramdisk);
      Add(Second, img.Second);
      Add(RecoveryDtbo, img.RecoveryDtbo);
      Add(Dtb, img.Dtb);
      Add(BootSignature, img.BootSignature);
    }
    Add(Trailer, img.Trailer);
    return entries;
  }

  // ── create ───────────────────────────────────────────────────────────

  /// <summary>
  /// Builds an image from component files named as <c>unpack_bootimg</c> names them, with the
  /// header fields taken from a <c>metadata.ini</c> among the inputs (the rendering this
  /// descriptor lists) and <c>mkbootimg</c>'s defaults for every field it does not set.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.ArchiveName))
      .Select(i => (Name: Leaf(i.ArchiveName), Input: i)).ToList();
    foreach (var (name, _) in files)
      if (!IsComponentName(name) && !name.Equals(FirmwareArchive.MetadataName, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException($"Android boot image: '{name}' is not a boot image component " +
          "(kernel, ramdisk, second, recovery_dtbo, dtb, boot_signature, vendor_ramdisk[NN], bootconfig, trailer.bin, metadata.ini).");

    var metadata = files.FirstOrDefault(f => f.Name.Equals(FirmwareArchive.MetadataName, StringComparison.OrdinalIgnoreCase)).Input;
    var ini = metadata is null ? null : FirmwareArchive.ParseIni(metadata.ReadContent());
    var vendorHint = files.Any(f => f.Name.StartsWith(VendorRamdisk, StringComparison.OrdinalIgnoreCase) || f.Name.Equals(Bootconfig, StringComparison.OrdinalIgnoreCase));
    var img = AndroidBootImageMetadata.NewImage(ini, vendorHint);

    var parts = files.Where(f => IsComponentName(f.Name)).Select(f => (f.Name, Data: f.Input.ReadContent())).ToList();
    ApplyComponents(img, parts, creating: true);
    // mkbootimg writes a zero load address for an absent ramdisk or second stage (boot v0-v2).
    if (!img.IsVendor && img.HeaderVersion < 3) {
      if (img.Ramdisk.Length == 0) img.RamdiskAddress = 0;
      if (img.Second.Length == 0) img.SecondAddress = 0;
    }
    if (ini is not null) AndroidBootImageMetadata.Apply(img, ini);
    Write(output, img, recomputeImageId: true);
  }

  // ── modify ───────────────────────────────────────────────────────────

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var (img, original) = OpenForEdit(archive);
    var files = inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.ArchiveName))
      .Select(i => (Name: Leaf(i.ArchiveName), Input: i)).ToList();
    var parts = new List<(string Name, byte[] Data)>();
    Dictionary<string, string>? ini = null;
    foreach (var (name, input) in files) {
      if (name.Equals(FirmwareArchive.MetadataName, StringComparison.OrdinalIgnoreCase)) ini = FirmwareArchive.ParseIni(input.ReadContent());
      else if (name.Equals(FullImage, StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException($"Android boot image: {FullImage} is a view of the whole image, not a member.");
      else if (IsComponentName(name)) parts.Add((name, input.ReadContent()));
      else throw new ArgumentException($"Android boot image: '{name}' is not a boot image component.");
    }
    if (parts.Count == 0 && ini is null) return;
    RefuseSignedEdit(img, touchesContent: true);
    ApplyComponents(img, parts, creating: false);
    if (ini is not null) AndroidBootImageMetadata.Apply(img, ini);
    Commit(archive, img, original);
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    var names = (entryNames ?? []).Select(Leaf).Where(n => !Synthetic.Contains(n)).ToList();
    if (names.Count == 0) return;
    var (img, original) = OpenForEdit(archive);
    var dropsOnlySignatures = names.All(n => n.Equals(BootSignature, StringComparison.OrdinalIgnoreCase) || n.Equals(Trailer, StringComparison.OrdinalIgnoreCase));
    RefuseSignedEdit(img, touchesContent: !dropsOnlySignatures);

    // Remove vendor ramdisk fragments from the highest index down so earlier indices keep their meaning.
    var fragments = new List<int>();
    foreach (var name in names) {
      if (TryRamdiskIndex(name, out var index)) {
        if (index < img.VendorRamdisks.Count) fragments.Add(index);
        continue;
      }
      switch (name.ToLowerInvariant()) {
        case Kernel: img.Kernel = []; break;
        case Ramdisk: img.Ramdisk = []; break;
        case Second: img.Second = []; break;
        case RecoveryDtbo: img.RecoveryDtbo = []; break;
        case Dtb: img.Dtb = []; break;
        case BootSignature: img.BootSignature = []; break;
        case Bootconfig: img.Bootconfig = []; break;
        case Trailer: img.Trailer = []; break;
        case VendorRamdisk:
          if (img.IsVendor && img.HeaderVersion < 4) img.VendorRamdisks.Clear();
          break;
      }
    }
    foreach (var index in fragments.Distinct().OrderDescending()) img.VendorRamdisks.RemoveAt(index);
    Commit(archive, img, original);
  }

  /// <summary>Empties every component and the trailer, keeping the header fields: a valid image with nothing in it.</summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    var (img, original) = OpenForEdit(archive);
    img.Kernel = img.Ramdisk = img.Second = img.RecoveryDtbo = img.Dtb = img.BootSignature = img.Bootconfig = img.Trailer = [];
    img.VendorRamdisks.Clear();
    Commit(archive, img, original);
  }

  private static (AndroidBootImage Image, AndroidBootImage Original) OpenForEdit(Stream archive) {
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("Android boot image editing requires a readable, writable, seekable stream.", nameof(archive));
    var data = FirmwareArchive.ReadAll(archive);
    var img = AndroidBootImage.Parse(data);
    if (!img.IsCanonical)
      throw new NotSupportedException($"Android boot image: {img.NonCanonicalReason}; this layout is not one mkbootimg writes, so it is not re-laid out.");
    return (img, AndroidBootImage.Parse(data));
  }

  private static void RefuseSignedEdit(AndroidBootImage img, bool touchesContent) {
    if (!touchesContent) return;
    if (img.BootSignature.Length > 0)
      throw new NotSupportedException("Android boot image: the v4 boot signature covers this image and would go stale; remove boot_signature first.");
    if (img.HasAvbFooter)
      throw new NotSupportedException("Android boot image: an AVB footer covers this image and would go stale; remove trailer.bin first.");
  }

  private static void Commit(Stream archive, AndroidBootImage img, AndroidBootImage original)
    => WholeImageRebuildCommitter.Replace(archive,
      candidate => Write(candidate, img, recomputeImageId: original.ImageIdValid),
      candidate => _ = AndroidBootImage.Parse(FirmwareArchive.ReadAll(candidate)));

  private static void Write(Stream output, AndroidBootImage img, bool recomputeImageId) {
    try {
      img.Write(output, recomputeImageId);
    } catch (InvalidOperationException e) {
      throw new ArgumentException(e.Message, e);
    }
  }

  // ── components ───────────────────────────────────────────────────────

  private static void ApplyComponents(AndroidBootImage img, List<(string Name, byte[] Data)> parts, bool creating) {
    foreach (var (name, data) in parts.Where(p => !TryRamdiskIndex(p.Name, out _))) {
      switch (name.ToLowerInvariant()) {
        case Kernel: img.Kernel = data; break;
        case Ramdisk: img.Ramdisk = data; break;
        case Second: img.Second = data; break;
        case RecoveryDtbo: img.RecoveryDtbo = data; break;
        case Dtb: img.Dtb = data; break;
        case BootSignature: img.BootSignature = data; break;
        case Bootconfig: img.Bootconfig = data; break;
        case Trailer: img.Trailer = data; break;
        case VendorRamdisk:
          if (img.IsVendor && img.HeaderVersion >= 4)
            throw new ArgumentException("Android vendor_boot v4: name ramdisk fragments vendor_ramdisk00, vendor_ramdisk01, ...");
          if (img.VendorRamdisks.Count == 0) img.VendorRamdisks.Add(new VendorRamdisk { Type = AndroidBootImage.VendorRamdiskTypePlatform });
          img.VendorRamdisks[0].Data = data;
          break;
      }
    }

    var fragments = parts.Where(p => TryRamdiskIndex(p.Name, out _))
      .Select(p => (Index: RamdiskIndex(p.Name), p.Data)).OrderBy(p => p.Index).ToList();
    if (fragments.Count > 0 && !(img.IsVendor && img.HeaderVersion >= 4))
      throw new ArgumentException("Android boot image: numbered vendor_ramdiskNN fragments exist only in a vendor_boot v4 image.");
    foreach (var (index, data) in fragments) {
      if (index < img.VendorRamdisks.Count) {
        img.VendorRamdisks[index].Data = data;
        continue;
      }
      if (index != img.VendorRamdisks.Count)
        throw new ArgumentException($"Android vendor_boot: {AndroidBootImageMetadata.RamdiskEntryName(index)} would leave a gap after {img.VendorRamdisks.Count} fragments.");
      // mkbootimg's --vendor_ramdisk becomes an unnamed platform fragment; later fragments need a name of their own.
      var first = img.VendorRamdisks.Count == 0 && creating;
      var name = new byte[AndroidBootImage.VendorRamdiskNameSize];
      if (!first) System.Text.Encoding.ASCII.GetBytes("ramdisk" + index.ToString("D2", CultureInfo.InvariantCulture)).CopyTo(name, 0);
      img.VendorRamdisks.Add(new VendorRamdisk {
        Data = data,
        Type = first ? AndroidBootImage.VendorRamdiskTypePlatform : 0,
        Name = name,
      });
    }
  }

  private static bool IsComponentName(string name)
    => TryRamdiskIndex(name, out _) || name.ToLowerInvariant() is Kernel or Ramdisk or Second or RecoveryDtbo or Dtb
      or BootSignature or VendorRamdisk or Bootconfig or Trailer;

  private static bool TryRamdiskIndex(string name, out int index) {
    index = -1;
    return name.Length == VendorRamdisk.Length + 2
      && name.StartsWith(VendorRamdisk, StringComparison.OrdinalIgnoreCase)
      && int.TryParse(name.AsSpan(VendorRamdisk.Length), NumberStyles.None, CultureInfo.InvariantCulture, out index);
  }

  private static int RamdiskIndex(string name) => TryRamdiskIndex(name, out var i) ? i : -1;

  private static string Leaf(string name) => name.Replace('\\', '/').TrimEnd('/').Split('/')[^1];
}
