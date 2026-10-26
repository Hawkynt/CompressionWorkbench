#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using FileFormat.FirmwareCommon;

namespace FileFormat.AndroidVbmeta;

/// <summary>
/// Android Verified Boot 2.0 <c>vbmeta</c> image (magic <c>AVB0</c>) as a pseudo-archive:
/// <c>metadata.ini</c> renders the header, the verification result and every descriptor's fields;
/// each descriptor is a member under <c>descriptors/</c> as its exact encoded bytes; the public
/// key, its metadata and the signature are members of their own.
/// </summary>
/// <remarks>
/// <para>Listing verifies: the hash over header and auxiliary block is recomputed, and an RSA
/// signature is checked against the embedded public key, as <c>avbtool verify_image</c> does for
/// the vbmeta blob itself.</para>
/// <para>Writing is limited to what can be written honestly without a private key: unsigned
/// (<c>NONE</c>) images, created from descriptor blobs and header fields, or edited by adding,
/// replacing and removing descriptors. The bytes equal <c>avbtool make_vbmeta_image --algorithm
/// NONE</c>'s for the same descriptors. A signed image is read and verified, never rewritten.</para>
/// <para>Reference: AOSP <c>platform/external/avb</c> (<c>avbtool.py</c>, <c>libavb</c>), MIT /
/// Apache-2.0, converted.</para>
/// </remarks>
public sealed class AndroidVbmetaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, IArchiveWriteConstraints, ISyntheticEntryNames {

  public const string PublicKeyName = "public_key.bin", PublicKeyMetadataName = "public_key_metadata.bin",
    SignatureName = "signature.bin", TrailerName = "trailer.bin", FullImage = "FULL.img", DescriptorFolder = "descriptors/";

  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    FirmwareArchive.MetadataName, FullImage,
  };

  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;
  public string Id => "AndroidVbmeta";
  public string DisplayName => "Android Verified Boot (vbmeta)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".avb";
  public IReadOnlyList<string> Extensions => [".avb"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("AVB0"u8.ToArray(), Confidence: 0.9)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Android Verified Boot 2.0 vbmeta image: header, hash/signature (verified on read) and descriptors " +
    "(hash, hashtree, kernel cmdline, chain partition, property); unsigned images written as avbtool does.";

  // ── read ─────────────────────────────────────────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => FirmwareArchive.List(Entries(FirmwareArchive.ReadAll(stream)));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => FirmwareArchive.Extract(Entries(FirmwareArchive.ReadAll(stream)), outputDir, files);

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password)
    => FirmwareArchive.List(Entries(archive.ToArray()));

  void IArchiveFormatOperations.ExtractSpan(ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files)
    => FirmwareArchive.Extract(Entries(archive.ToArray()), outputDir, files);

  public static List<FirmwareEntry> Entries(byte[] data) {
    AvbVbmeta v;
    try {
      v = AvbVbmeta.Parse(data);
    } catch (InvalidDataException e) {
      return [
        new(FirmwareArchive.MetadataName, Encoding.UTF8.GetBytes($"[vbmeta]\nparse_status = partial\nerror = {e.Message}\n")),
        new(FullImage, data),
      ];
    }
    var entries = new List<FirmwareEntry> { new(FirmwareArchive.MetadataName, RenderMetadata(v)) };
    for (var i = 0; i < v.Descriptors.Count; ++i) entries.Add(new(AvbDescriptorText.EntryName(i, v.Descriptors[i]), v.Descriptors[i].Encoded));
    if (v.PublicKey.Length > 0) entries.Add(new(PublicKeyName, v.PublicKey));
    if (v.PublicKeyMetadata.Length > 0) entries.Add(new(PublicKeyMetadataName, v.PublicKeyMetadata));
    if (v.Signature.Length > 0) entries.Add(new(SignatureName, v.Signature));
    if (v.Trailer.Length > 0) entries.Add(new(TrailerName, v.Trailer));
    return entries;
  }

  public static byte[] RenderMetadata(AvbVbmeta v) {
    var sb = new StringBuilder();
    void Line(string key, string value) => sb.Append(key).Append(" = ").Append(value).Append('\n');
    sb.Append("[vbmeta]\n");
    Line("required_libavb_version", $"{v.VersionMajor}.{v.VersionMinor}");
    Line("algorithm", AvbVbmeta.AlgorithmName(v.AlgorithmType));
    Line("rollback_index", v.RollbackIndex.ToString(CultureInfo.InvariantCulture));
    Line("flags", v.Flags.ToString(CultureInfo.InvariantCulture));
    Line("rollback_index_location", v.RollbackIndexLocation.ToString(CultureInfo.InvariantCulture));
    Line("release_string", AvbVbmeta.ReleaseText(v.ReleaseString));
    Line("hash", Convert.ToHexStringLower(v.Hash));
    Line("hash_valid", v.HashValid is { } h ? (h ? "true" : "false") : "n/a");
    Line("signature_valid", v.SignatureValid is { } s ? (s ? "true" : "false") : "n/a");
    if (v.PublicKey.Length > 0) Line("public_key_sha1", Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(v.PublicKey)));
    Line("descriptors", v.Descriptors.Count.ToString(CultureInfo.InvariantCulture));
    Line("trailer_size", v.Trailer.Length.ToString(CultureInfo.InvariantCulture));
    Line("canonical", v.IsCanonical ? "true" : "false");
    if (v.NonCanonicalReason is { } reason) Line("canonical_issue", reason);
    Line("parse_status", "ok");
    for (var i = 0; i < v.Descriptors.Count; ++i) {
      sb.Append("[descriptor_").Append(i.ToString("D2", CultureInfo.InvariantCulture)).Append("]\n");
      AvbDescriptorText.Render(sb, v.Descriptors[i]);
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  // ── create ───────────────────────────────────────────────────────────

  /// <summary>
  /// Writes an unsigned vbmeta image from descriptor blobs (<c>descriptors/NN_*.bin</c>, in
  /// <c>NN</c> order), an optional <c>public_key_metadata.bin</c>, an optional <c>trailer.bin</c>
  /// and the header fields of a <c>metadata.ini</c> — required libavb version, rollback index and
  /// location, flags and release string. A public key or signature among the inputs is refused:
  /// signing needs the private key.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var v = new AvbVbmeta();
    Encoding.ASCII.GetBytes("avbtool 1.3.0").CopyTo(v.ReleaseString, 0);
    var descriptors = new SortedDictionary<int, AvbDescriptor>();
    Dictionary<string, string>? ini = null;
    foreach (var input in inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.ArchiveName))) {
      var name = input.ArchiveName.Replace('\\', '/').TrimStart('/');
      if (name.Equals(FirmwareArchive.MetadataName, StringComparison.OrdinalIgnoreCase)) ini = FirmwareArchive.ParseIni(input.ReadContent());
      else if (TryDescriptorIndex(name, out var index)) {
        if (!descriptors.TryAdd(index, AvbVbmeta.FromEncoded(input.ReadContent())))
          throw new ArgumentException($"vbmeta: two descriptors claim index {index}.");
      } else if (name.Equals(PublicKeyMetadataName, StringComparison.OrdinalIgnoreCase)) v.PublicKeyMetadata = input.ReadContent();
      else if (name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase)) v.Trailer = input.ReadContent();
      else if (name.Equals(PublicKeyName, StringComparison.OrdinalIgnoreCase) || name.Equals(SignatureName, StringComparison.OrdinalIgnoreCase))
        throw new NotSupportedException("vbmeta: only unsigned (NONE) images can be written; signing needs the private key.");
      else throw new ArgumentException($"vbmeta: '{name}' is not a vbmeta member (descriptors/NN_*.bin, public_key_metadata.bin, trailer.bin, metadata.ini).");
    }
    v.Descriptors = [.. descriptors.Values];
    if (ini is not null) ApplyHeader(v, ini, creating: true);
    v.Write(output);
  }

  // ── write constraints ────────────────────────────────────────────────

  /// <summary>
  /// The members <see cref="Create"/> writes. A vbmeta holds structured AVB descriptors, not
  /// files, so an arbitrary blob has nowhere to go; a public key or signature needs the private key.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    reason = null;
    if (input.IsDirectory) return true;
    var name = input.ArchiveName.Replace('\\', '/').TrimStart('/');
    if (name.Equals(FirmwareArchive.MetadataName, StringComparison.OrdinalIgnoreCase) || TryDescriptorIndex(name, out _)
        || name.Equals(PublicKeyMetadataName, StringComparison.OrdinalIgnoreCase) || name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase))
      return true;
    reason = name.Equals(PublicKeyName, StringComparison.OrdinalIgnoreCase) || name.Equals(SignatureName, StringComparison.OrdinalIgnoreCase)
      ? "vbmeta: only unsigned (NONE) images can be written; signing needs the private key."
      : $"vbmeta: '{name}' is not a vbmeta member — {this.AcceptedInputsDescription}.";
    return false;
  }

  public long? MaxTotalArchiveSize => null;

  public string AcceptedInputsDescription => "accepts: descriptors/NN_*.bin, public_key_metadata.bin, trailer.bin, metadata.ini";

  private static void ApplyHeader(AvbVbmeta v, IReadOnlyDictionary<string, string> ini, bool creating) {
    var current = FirmwareArchive.ParseIni(RenderMetadata(v));
    foreach (var (qualified, value) in ini) {
      if (!qualified.StartsWith("vbmeta.", StringComparison.OrdinalIgnoreCase)) continue;
      if (current.TryGetValue(qualified, out var existing) && existing == value) continue;
      var key = qualified["vbmeta.".Length..].ToLowerInvariant();
      switch (key) {
        case "required_libavb_version": {
          var parts = value.Split('.');
          if (parts.Length != 2 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
              || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
            throw new ArgumentException($"metadata.ini: required_libavb_version '{value}' is not major.minor.");
          (v.VersionMajor, v.VersionMinor) = (major, minor);
          break;
        }
        case "rollback_index": v.RollbackIndex = Number(key, value); break;
        case "flags": v.Flags = checked((uint)Number(key, value)); break;
        case "rollback_index_location": v.RollbackIndexLocation = checked((uint)Number(key, value)); break;
        case "release_string": {
          var bytes = Encoding.UTF8.GetBytes(value);
          if (bytes.Length > AvbVbmeta.ReleaseStringSize - 1) throw new ArgumentException($"metadata.ini: release_string is longer than {AvbVbmeta.ReleaseStringSize - 1} bytes.");
          v.ReleaseString = new byte[AvbVbmeta.ReleaseStringSize];
          bytes.CopyTo(v.ReleaseString, 0);
          break;
        }
        case "algorithm":
          if (!value.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"vbmeta: algorithm {value} needs the private key; only NONE can be written.");
          break;
        default:
          // The remaining keys (hash, verification results, counts) are renderings, not fields.
          if (!creating && key is not ("hash" or "hash_valid" or "signature_valid" or "public_key_sha1" or "descriptors" or "trailer_size" or "canonical" or "canonical_issue" or "parse_status"))
            throw new ArgumentException($"metadata.ini: '{key}' is not a vbmeta header field.");
          break;
      }
    }
  }

  private static ulong Number(string key, string value)
    => FirmwareArchive.TryParseNumber(value, out var n) ? n : throw new ArgumentException($"metadata.ini: {key} '{value}' is not a number.");

  private static bool TryDescriptorIndex(string name, out int index) {
    index = -1;
    if (!name.StartsWith(DescriptorFolder, StringComparison.OrdinalIgnoreCase)) return false;
    var leaf = name[DescriptorFolder.Length..];
    var underscore = leaf.IndexOf('_');
    var digits = underscore < 0 ? Path.GetFileNameWithoutExtension(leaf) : leaf[..underscore];
    return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index);
  }

  // ── modify ───────────────────────────────────────────────────────────

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var v = OpenForEdit(archive);
    foreach (var input in inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.ArchiveName))) {
      var name = input.ArchiveName.Replace('\\', '/').TrimStart('/');
      if (name.Equals(FirmwareArchive.MetadataName, StringComparison.OrdinalIgnoreCase)) ApplyHeader(v, FirmwareArchive.ParseIni(input.ReadContent()), creating: false);
      else if (TryDescriptorIndex(name, out var index)) {
        var desc = AvbVbmeta.FromEncoded(input.ReadContent());
        if (index < v.Descriptors.Count) v.Descriptors[index] = desc;
        else if (index == v.Descriptors.Count) v.Descriptors.Add(desc);
        else throw new ArgumentException($"vbmeta: descriptor {index} would leave a gap after {v.Descriptors.Count} descriptors.");
      } else if (name.Equals(PublicKeyMetadataName, StringComparison.OrdinalIgnoreCase)) v.PublicKeyMetadata = input.ReadContent();
      else if (name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase)) v.Trailer = input.ReadContent();
      else if (name.Equals(PublicKeyName, StringComparison.OrdinalIgnoreCase) || name.Equals(SignatureName, StringComparison.OrdinalIgnoreCase))
        throw new NotSupportedException("vbmeta: only unsigned (NONE) images can be written; signing needs the private key.");
      else throw new ArgumentException($"vbmeta: '{name}' is not a vbmeta member.");
    }
    Commit(archive, v);
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    var names = (entryNames ?? []).Select(n => n.Replace('\\', '/').TrimStart('/')).Where(n => !Synthetic.Contains(n)).ToList();
    if (names.Count == 0) return;
    var v = OpenForEdit(archive);
    var drop = new List<int>();
    foreach (var name in names) {
      if (TryDescriptorIndex(name, out var index) && index < v.Descriptors.Count) drop.Add(index);
      else if (name.Equals(PublicKeyMetadataName, StringComparison.OrdinalIgnoreCase)) v.PublicKeyMetadata = [];
      else if (name.Equals(TrailerName, StringComparison.OrdinalIgnoreCase)) v.Trailer = [];
    }
    foreach (var index in drop.Distinct().OrderDescending()) v.Descriptors.RemoveAt(index);
    Commit(archive, v);
  }

  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    var v = OpenForEdit(archive);
    v.Descriptors.Clear();
    v.PublicKeyMetadata = [];
    v.Trailer = [];
    Commit(archive, v);
  }

  private static AvbVbmeta OpenForEdit(Stream archive) {
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("vbmeta editing requires a readable, writable, seekable stream.", nameof(archive));
    var v = AvbVbmeta.Parse(FirmwareArchive.ReadAll(archive));
    if (v.AlgorithmType != 0)
      throw new NotSupportedException($"vbmeta: this image is signed ({AvbVbmeta.AlgorithmName(v.AlgorithmType)}); an edit would need the private key to re-sign it.");
    if (!v.IsCanonical)
      throw new NotSupportedException($"vbmeta: {v.NonCanonicalReason}; it is not re-laid out.");
    return v;
  }

  private static void Commit(Stream archive, AvbVbmeta v)
    => WholeImageRebuildCommitter.Replace(archive, v.Write, candidate => _ = AvbVbmeta.Parse(FirmwareArchive.ReadAll(candidate)));
}
