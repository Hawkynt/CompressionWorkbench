using System.Formats.Asn1;
using FileFormat.Pkcs12;

namespace Compression.Tests.Pkcs12;

[TestFixture]
public class Pkcs12Tests {

  // ── Fixture builder ──────────────────────────────────────────────────────
  //
  // Constructs a minimal (but structurally valid) PFX blob:
  //   PFX ::= SEQUENCE {
  //     version INTEGER (3),
  //     authSafe ContentInfo -- type = data, content = OCTET STRING wrapping
  //                            --   SEQUENCE OF SafeBag (one certBag, one shroudedKeyBag)
  //   }
  //
  // No macData, no integrity protection — we only exercise the shallow walker.

  private static byte[] BuildSyntheticPfx() {
    var certDer = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
    var shroudedKeyDer = new byte[] { 0x01, 0x02, 0x03 };

    // ── Build inner safe-contents (SEQUENCE OF SafeBag) ────────────────────
    var safeContents = new AsnWriter(AsnEncodingRules.DER);
    using (safeContents.PushSequence()) {
      // certBag
      using (safeContents.PushSequence()) {
        safeContents.WriteObjectIdentifier(Pkcs12Parser.OidCertBag);
        using (safeContents.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))) {
          // CertBag ::= SEQUENCE { certId OID, certValue [0] EXPLICIT OCTET STRING }
          using (safeContents.PushSequence()) {
            safeContents.WriteObjectIdentifier(Pkcs12Parser.OidX509Cert);
            using (safeContents.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))) {
              safeContents.WriteOctetString(certDer);
            }
          }
        }
      }
      // shroudedKeyBag
      using (safeContents.PushSequence()) {
        safeContents.WriteObjectIdentifier(Pkcs12Parser.OidShroudedKeyBag);
        using (safeContents.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))) {
          using (safeContents.PushSequence()) {
            safeContents.WriteOctetString(shroudedKeyDer);
          }
        }
      }
    }
    var safeContentsDer = safeContents.Encode();

    // ── Wrap safe-contents in an OCTET STRING inside a ContentInfo (type=Data) ─
    var authSafe = new AsnWriter(AsnEncodingRules.DER);
    using (authSafe.PushSequence()) {
      authSafe.WriteObjectIdentifier(Pkcs12Parser.OidData);
      using (authSafe.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))) {
        authSafe.WriteOctetString(safeContentsDer);
      }
    }
    var authSafeDer = authSafe.Encode();

    // ── Outer PFX SEQUENCE ────────────────────────────────────────────────
    var pfx = new AsnWriter(AsnEncodingRules.DER);
    using (pfx.PushSequence()) {
      pfx.WriteInteger(3);
      pfx.WriteEncodedValue(authSafeDer);
    }
    return pfx.Encode();
  }

  // ── Tests ────────────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Walk_FindsCertAndShroudedKey() {
    var pfx = BuildSyntheticPfx();
    var bags = Pkcs12Parser.Walk(pfx);

    Assert.That(bags, Has.Count.EqualTo(2));
    Assert.That(bags[0].Kind, Is.EqualTo(Pkcs12Parser.BagKind.Cert));
    Assert.That(bags[1].Kind, Is.EqualTo(Pkcs12Parser.BagKind.ShroudedKey));
    Assert.That(bags[0].ValueDer, Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE }));
  }

  [Category("HappyPath")]
  [Test]
  public void List_EmitsCertDerPemAndEncryptedKeyEntries() {
    var pfx = BuildSyntheticPfx();
    using var ms = new MemoryStream(pfx);
    var desc = new Pkcs12FormatDescriptor();
    var entries = desc.List(ms, null);

    Assert.That(entries.Any(e => e.Name == "manifest.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "cert_00.der"), Is.True);
    Assert.That(entries.Any(e => e.Name == "cert_00.pem"), Is.True);
    Assert.That(entries.Any(e => e.Name == "encrypted_key_00.der"), Is.True);
    var encKey = entries.First(e => e.Name == "encrypted_key_00.der");
    Assert.That(encKey.IsEncrypted, Is.True);
  }

  [Category("HappyPath"), Category("RoundTrip")]
  [Test]
  public void Extract_WritesPemWithCertificateBoundaries() {
    var pfx = BuildSyntheticPfx();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(pfx);
      var desc = new Pkcs12FormatDescriptor();
      desc.Extract(ms, tmp, null, null);

      var pem = File.ReadAllText(Path.Combine(tmp, "cert_00.pem"));
      Assert.That(pem, Does.StartWith("-----BEGIN CERTIFICATE-----"));
      Assert.That(pem.TrimEnd(), Does.EndWith("-----END CERTIFICATE-----"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }
}
