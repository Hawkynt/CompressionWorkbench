#pragma warning disable CS1591
using System.Formats.Asn1;

namespace FileFormat.Pkcs12;

/// <summary>
/// Shallow ASN.1 walker for PKCS#12 / PFX bundles (RFC 7292).  We never decrypt
/// anything here — the aim is simply to enumerate each top-level SafeBag by its
/// OID so the contents can be extracted verbatim.  Encrypted / password-protected
/// <c>EncryptedData</c> envelopes are reported but left as opaque DER blobs.
/// </summary>
public static class Pkcs12Parser {

  // OIDs defined in RFC 7292 § 4.2 and PKCS#7 § 9.
  public const string OidData              = "1.2.840.113549.1.7.1";
  public const string OidSignedData        = "1.2.840.113549.1.7.2";
  public const string OidEncryptedData     = "1.2.840.113549.1.7.6";
  public const string OidKeyBag            = "1.2.840.113549.1.12.10.1.1";
  public const string OidShroudedKeyBag    = "1.2.840.113549.1.12.10.1.2";
  public const string OidCertBag           = "1.2.840.113549.1.12.10.1.3";
  public const string OidCrlBag            = "1.2.840.113549.1.12.10.1.4";
  public const string OidSecretBag         = "1.2.840.113549.1.12.10.1.5";
  public const string OidSafeContentsBag   = "1.2.840.113549.1.12.10.1.6";
  public const string OidX509Cert          = "1.2.840.113549.1.9.22.1";

  public enum BagKind {
    Cert,
    Key,
    ShroudedKey,
    Crl,
    Secret,
    Nested,
    EncryptedContent,
    Unknown,
  }

  public sealed record Bag(int Index, BagKind Kind, string BagOid, byte[] ValueDer);

  /// <summary>
  /// Walk a PFX blob, producing one <see cref="Bag"/> per SafeBag discovered
  /// (nested ContentInfo containers are flattened).  Encrypted SafeContents
  /// blocks are emitted as a single opaque <see cref="BagKind.EncryptedContent"/>.
  /// </summary>
  public static IReadOnlyList<Bag> Walk(ReadOnlySpan<byte> der) {
    var bags = new List<Bag>();
    // Outer PFX structure:
    //   PFX ::= SEQUENCE { version, authSafe ContentInfo, macData OPTIONAL }
    var reader = new AsnReader(der.ToArray(), AsnEncodingRules.BER);
    var pfx = reader.ReadSequence();
    _ = pfx.ReadInteger();          // version
    var authSafe = pfx.ReadSequence();  // ContentInfo sequence
    ParseContentInfo(authSafe, bags, depth: 0);
    return bags;
  }

  /// <summary>
  /// Parse a ContentInfo: { contentType OID, content [0] EXPLICIT ANY }.
  /// </summary>
  private static void ParseContentInfo(AsnReader contentInfo, List<Bag> bags, int depth) {
    if (depth > 4) return; // safety against pathological nesting
    var oid = contentInfo.ReadObjectIdentifier();
    // [0] EXPLICIT context-specific tag wraps the actual content.
    var ctxTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
    if (!contentInfo.HasData) return;
    var contentReader = contentInfo.ReadSequence(ctxTag);

    switch (oid) {
      case OidData: {
        // inner is OCTET STRING containing a DER-encoded SEQUENCE OF SafeBag.
        var octets = contentReader.ReadOctetString();
        ParseSafeContents(octets, bags);
        break;
      }
      case OidEncryptedData: {
        // EncryptedData is a whole sub-structure; we don't decrypt.  Emit as one blob.
        var rawBody = contentReader.ReadEncodedValue().ToArray();
        bags.Add(new Bag(bags.Count, BagKind.EncryptedContent, oid, rawBody));
        break;
      }
      default: {
        var rawBody = contentReader.HasData
          ? contentReader.ReadEncodedValue().ToArray()
          : Array.Empty<byte>();
        bags.Add(new Bag(bags.Count, BagKind.Unknown, oid, rawBody));
        break;
      }
    }
  }

  /// <summary>
  /// Parse a SafeContents (SEQUENCE OF SafeBag) blob and append every bag found.
  /// </summary>
  private static void ParseSafeContents(byte[] safeContents, List<Bag> bags) {
    AsnReader reader;
    try {
      reader = new AsnReader(safeContents, AsnEncodingRules.BER);
    } catch {
      return;
    }
    AsnReader seq;
    try {
      seq = reader.ReadSequence();
    } catch {
      return;
    }

    while (seq.HasData) {
      AsnReader bag;
      try {
        bag = seq.ReadSequence();
      } catch {
        return;
      }

      string bagId;
      try {
        bagId = bag.ReadObjectIdentifier();
      } catch {
        continue;
      }

      // [0] EXPLICIT wraps the bag value.
      var ctxTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
      byte[] valueDer;
      try {
        var bagValue = bag.ReadSequence(ctxTag);
        valueDer = bagValue.ReadEncodedValue().ToArray();
      } catch {
        continue;
      }

      var kind = bagId switch {
        OidCertBag => BagKind.Cert,
        OidKeyBag => BagKind.Key,
        OidShroudedKeyBag => BagKind.ShroudedKey,
        OidCrlBag => BagKind.Crl,
        OidSecretBag => BagKind.Secret,
        OidSafeContentsBag => BagKind.Nested,
        _ => BagKind.Unknown,
      };

      // For certBag we can dig one level deeper to extract the raw X.509 DER.
      if (kind == BagKind.Cert) {
        var rawCert = TryExtractCertFromCertBag(valueDer);
        if (rawCert != null) valueDer = rawCert;
      }

      bags.Add(new Bag(bags.Count, kind, bagId, valueDer));
    }
  }

  /// <summary>
  /// CertBag ::= SEQUENCE { certId OID, certValue [0] EXPLICIT ANY }.
  /// When certId == x509Certificate, certValue is an OCTET STRING containing the DER cert.
  /// </summary>
  private static byte[]? TryExtractCertFromCertBag(byte[] certBagDer) {
    try {
      var reader = new AsnReader(certBagDer, AsnEncodingRules.BER);
      var seq = reader.ReadSequence();
      var certId = seq.ReadObjectIdentifier();
      var ctxTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
      var inner = seq.ReadSequence(ctxTag);
      if (certId != OidX509Cert) return null;
      return inner.ReadOctetString();
    } catch {
      return null;
    }
  }
}
