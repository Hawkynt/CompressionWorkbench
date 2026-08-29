using NUnit.Framework;

namespace Compression.Tests.Checksums;

[TestFixture]
public sealed class JavaScriptChecksumCoverageTests {
  private readonly record struct Coverage(string Source, string ManagedCounterpart);

  private static readonly Coverage[] Inventory = [
    new("aba-routing.js", "AbaRouting"),
    new("adler.js", "Adler; compatibility Adler32"),
    new("bsd-checksum.js", "BsdChecksum"),
    new("check-digit.js", "Luhn / Verhoeff / Damm"),
    new("complement.js", "ComplementChecksum"),
    new("constant-weight.js", "ConstantWeight"),
    new("crc.js", "Crc / Crc128 / preset catalogs"),
    new("cusip-checksum.js", "Cusip"),
    new("damm.js", "Damm"),
    new("ean13-checksum.js", "Gtin EAN-13 helpers"),
    new("ean8-checksum.js", "Gtin EAN-8 helpers"),
    new("fletcher.js", "Fletcher"),
    new("gtin-checksum.js", "Gtin"),
    new("iban-checksum.js", "Iban"),
    new("iccid-checksum.js", "Iccid"),
    new("imei-checksum.js", "Imei"),
    new("internet-checksum.js", "InternetChecksum"),
    new("isbn.js", "Isbn"),
    new("isin-checksum.js", "Isin"),
    new("issn-checksum.js", "Issn"),
    new("lrc.js", "Lrc"),
    new("luhn.js", "Luhn"),
    new("modulo.js", "ModuloCheckDigit"),
    new("nmea-0183.js", "Nmea0183"),
    new("npi-checksum.js", "Npi"),
    new("parity.js", "Parity"),
    new("planet-checksum.js", "PostalBarcode PLANET helpers"),
    new("postnet-checksum.js", "PostalBarcode POSTNET helpers"),
    new("sedol-checksum.js", "Sedol"),
    new("sum-checksum.js", "SumChecksum"),
    new("sysv-checksum.js", "SysVChecksum"),
    new("unix-sum.js", "BsdChecksum / SysVChecksum variants"),
    new("upc-ean.js", "Gtin"),
    new("upca-checksum.js", "Gtin UPC-A helpers"),
    new("verhoeff.js", "Verhoeff"),
    new("vin-checksum.js", "Vin"),
    new("xor-checksum.js", "XorChecksum")
  ];

  [Test]
  public void AllJavaScriptChecksumImplementationsHaveManagedCounterparts() {
    Assert.Multiple(() => {
      Assert.That(Inventory, Has.Length.EqualTo(37));
      Assert.That(Inventory.Select(static item => item.Source).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(37));
      Assert.That(Inventory.All(static item => item.Source.EndsWith(".js", StringComparison.Ordinal)), Is.True);
      Assert.That(Inventory.All(static item => !string.IsNullOrWhiteSpace(item.ManagedCounterpart)), Is.True);
      Assert.That(Inventory.All(static item => !item.ManagedCounterpart.Contains("JS-only", StringComparison.OrdinalIgnoreCase)), Is.True,
        "The checksum source inventory may not hide missing managed implementations behind JS-only bookkeeping rows.");
    });
  }
}
