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
/// </summary>
public sealed class Pkcs12FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Pkcs12";
  public string DisplayName => "PKCS#12 (PFX)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".p12";
  public IReadOnlyList<string> Extensions => [".p12", ".pfx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // ASN.1 SEQUENCE tag is universally 0x30 — very weak on its own, so we keep
  // the confidence low.  Extension is what really disambiguates.
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x30], Confidence: 0.10)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PKCS#12 certificate + key bundle (RFC 7292), shallow SafeBag extraction.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: e.Encrypted,
      LastModified: null, Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

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
