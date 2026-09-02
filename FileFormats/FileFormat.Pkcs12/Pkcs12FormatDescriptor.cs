#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pkcs12;

/// <summary>
/// Descriptor for PKCS#12 / PFX certificate bundles.  Performs a shallow ASN.1
/// walk and surfaces each top-level SafeBag as its own entry:
/// certificates as <c>cert_NN.der</c> (plus PEM side-copy), plain keys as
/// <c>key_NN.der</c>, encrypted/shrouded keys as <c>encrypted_key_NN.der</c>,
/// and any password-encrypted ContentInfo as a single opaque DER blob.
/// No decryption is attempted — this is strictly a structural view.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc7292</c> — RFC 7292 — PKCS #12 v1.1: Personal Information Exchange Syntax</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/PKCS_12</c> — Wikipedia overview</description></item>
///   <item><description>OpenSSL <c>openssl pkcs12</c> — de-facto reference implementation</description></item>
/// </list>
/// </summary>
public sealed class Pkcs12FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Pkcs12";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "PKCS#12 (PFX)";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".p12";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".p12", ".pfx"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // ASN.1 SEQUENCE tag is universally 0x30 — very weak on its own, so we keep
  // the confidence low.  Extension is what really disambiguates.
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x30], Confidence: 0.10)];
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
public string Description => "PKCS#12 certificate + key bundle (RFC 7292), shallow SafeBag extraction.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: e.Encrypted,
      LastModified: null, Kind: e.Kind)).ToList();

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. Each entry's
  /// decoded byte buffer is produced by <see cref="BuildEntries"/> and
  /// wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    foreach (var e in BuildEntries(archive)) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(e.Data, writable: false), e.Data.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

    /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  // ── Builder ─────────────────────────────────────────────────────────────

  private static IReadOnlyList<(string Name, string Kind, bool Encrypted, byte[] Data)>
      BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var bags = Pkcs12Parser.Walk(blob);
    var result = new List<(string, string, bool, byte[])>(bags.Count * 2 + 1);

    // Always emit a manifest so users can correlate bag indices with OIDs.
    result.Add(("manifest.ini", "Tag", false, BuildManifest(bags)));

    var certIdx = 0;
    var keyIdx = 0;
    var encKeyIdx = 0;
    var otherIdx = 0;

    foreach (var bag in bags) {
      switch (bag.Kind) {
        case Pkcs12Parser.BagKind.Cert:
          result.Add(($"cert_{certIdx:D2}.der", "Payload", false, bag.ValueDer));
          result.Add(($"cert_{certIdx:D2}.pem", "Payload", false, ToPem("CERTIFICATE", bag.ValueDer)));
          certIdx++;
          break;
        case Pkcs12Parser.BagKind.Key:
          result.Add(($"key_{keyIdx:D2}.der", "Payload", false, bag.ValueDer));
          keyIdx++;
          break;
        case Pkcs12Parser.BagKind.ShroudedKey:
          result.Add(($"encrypted_key_{encKeyIdx:D2}.der", "Payload", true, bag.ValueDer));
          encKeyIdx++;
          break;
        case Pkcs12Parser.BagKind.EncryptedContent:
          result.Add(($"encrypted_content_{otherIdx:D2}.der", "Payload", true, bag.ValueDer));
          otherIdx++;
          break;
        default:
          result.Add(($"bag_{otherIdx:D2}_{SafeOid(bag.BagOid)}.der", "Payload", false, bag.ValueDer));
          otherIdx++;
          break;
      }
    }

    return result;
  }

  private static byte[] BuildManifest(IReadOnlyList<Pkcs12Parser.Bag> bags) {
    var sb = new StringBuilder();
    sb.AppendLine("[pkcs12]");
    sb.Append("bag_count = ").Append(bags.Count).AppendLine();
    for (var i = 0; i < bags.Count; i++) {
      var b = bags[i];
      sb.Append('[').Append("bag").Append(i.ToString("D2")).AppendLine("]");
      sb.Append("kind = ").AppendLine(b.Kind.ToString());
      sb.Append("oid = ").AppendLine(b.BagOid);
      sb.Append("size = ").Append(b.ValueDer.Length).AppendLine();
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] ToPem(string label, byte[] der) {
    var sb = new StringBuilder();
    sb.Append("-----BEGIN ").Append(label).Append("-----\n");
    var base64 = Convert.ToBase64String(der);
    for (var i = 0; i < base64.Length; i += 64)
      sb.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
    sb.Append("-----END ").Append(label).Append("-----\n");
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  private static string SafeOid(string oid) => oid.Replace('.', '_');
}
