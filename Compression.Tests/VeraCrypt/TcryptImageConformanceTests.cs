using System.Buffers.Binary;
using FileFormat.VeraCrypt;

namespace Compression.Tests.VeraCrypt;

/// <summary>
/// cryptsetup's TCRYPT test images (<c>tests/tcrypt-images.tar.xz</c>, made with TrueCrypt 1–7.1
/// and VeraCrypt) as the oracle: every container holds a FAT filesystem whose serial number is
/// DEAD-BABE (CAFE-BABE in a hidden volume), so a volume is decrypted correctly exactly when that
/// serial appears. Set <c>CWB_TCRYPT_IMAGES</c> to the extracted <c>tcrypt-images</c> directory;
/// skipped without it unless <c>CWB_REQUIRE_TCRYPT_IMAGES=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class TcryptImageConformanceTests {
  private const string Password = "aaaaaaaaaaaa", HiddenPassword = "bbbbbbbbbbbb";

  private static string? Directory => Environment.GetEnvironmentVariable("CWB_TCRYPT_IMAGES") is { Length: > 0 } d && System.IO.Directory.Exists(d) ? d : null;

  private static string RequireImages() {
    if (Directory is { } d) return d;
    const string hint = "CWB_TCRYPT_IMAGES is not set to the extracted cryptsetup tests/tcrypt-images directory.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_TCRYPT_IMAGES") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
    return "";
  }

  public static IEnumerable<TestCaseData> Images() {
    if (Directory is not { } d) {
      yield return new TestCaseData("(images not available)").SetName("TcryptImages_NotAvailable");
      yield break;
    }
    foreach (var f in System.IO.Directory.GetFiles(d).Select(Path.GetFileName).Order(StringComparer.Ordinal))
      if (f!.StartsWith("tc_", StringComparison.Ordinal) || f.StartsWith("vc_", StringComparison.Ordinal))
        yield return new TestCaseData(f).SetName("TcryptImage_" + f.Replace('-', '_'));
  }

  /// <summary>The PRF named in an image file name (<c>tc_3-ripemd160-xts-aes</c> → ripemd160).</summary>
  internal static string Prf(string name) => name.Split('-')[1] switch {
    "blake2s" => "blake2s-256",
    "stribog" => "stribog512",
    var p => p,
  };

  internal static uint FatSerial(ReadOnlySpan<byte> bootSector) {
    // FAT32 keeps the serial at 0x43 behind a 0x29 signature at 0x42; FAT12/16 at 0x27 behind 0x26.
    if (bootSector[0x42] == 0x29) return BinaryPrimitives.ReadUInt32LittleEndian(bootSector[0x43..]);
    if (bootSector[0x26] == 0x29) return BinaryPrimitives.ReadUInt32LittleEndian(bootSector[0x27..]);
    return 0;
  }

  [TestCaseSource(nameof(Images))]
  public void GivenATcryptImage_WhenOpenedWithItsPassword_ThenTheFatSerialAppears(string name) {
    var dir = RequireImages();
    var hidden = name.EndsWith("-hidden", StringComparison.Ordinal);
    using var stream = File.OpenRead(Path.Combine(dir, name));
    var volume = TcVolume.Open(stream, hidden ? HiddenPassword : Password, new TcUnlockOptions {
      Prf = Prf(name), Header = hidden ? TcHeaderKind.Hidden : TcHeaderKind.Standard,
    });
    Assert.That(volume, Is.Not.Null, "no derivation and chain opened the header");
    TestContext.Out.WriteLine($"{volume!.Kdf.Prf}/{volume.Kdf.Iterations} {volume.Chain.Mode} {volume.Chain.Name} v{volume.Version} data@{volume.DataOffset} iv{volume.IvBase} len {volume.DataLength}");
    var raw = new byte[512];
    stream.Position = volume.DataOffset;
    stream.ReadExactly(raw);
    if (raw.All(static b => b == 0)) {
      // cryptsetup wiped the data of the containers the kernel cannot map (Blowfish, CBC
      // cascades); their sectors are covered by TcLegacyVectorTests instead.
      Assert.Pass($"header opens as {volume.Chain.Mode} {volume.Chain.Name}; data area wiped in the test image");
    }
    using var device = volume.OpenBlockDevice(stream, writable: false);
    var boot = new byte[512];
    device.ReadBlocks(0, boot);
    Assert.That(FatSerial(boot).ToString("X8"), Is.EqualTo(hidden ? "CAFEBABE" : "DEADBABE"));
  }
}
