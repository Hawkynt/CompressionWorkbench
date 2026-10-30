using System.Diagnostics;
using System.Text;
using System.Reflection;
using Compression.Analysis.ExternalTools;
using Compression.Core.Crypto;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// RAR 1.5-4.x encryption against archives other software wrote: RAR 2.0-cipher archives from
/// WinRAR 2.90's console rar, and a RAR 2.9 AES archive from libarchive's test suite. The writer's
/// encrypted output is checked by 7-Zip when it is installed. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class RarEncryptionVectorTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"RarVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded RAR vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly byte[] ATxt = "hello rar 2.0 encryption\n"u8.ToArray();
  private static readonly byte[] BBin = [.. Enumerable.Repeat(Enumerable.Range(0, 256).Select(i => (byte)i), 16).SelectMany(b => b)];

  [TestCase("winrar290-rar20-crypt.rar")]
  [TestCase("winrar290-rar20-crypt-store.rar")]
  [Category("ThirdPartyVector")]
  public void Given_WinRar290EncryptedArchive_When_ExtractedWithThePassword_Then_ContentsMatch(string file) {
    using var r = new RarReader(new MemoryStream(Vector(file)), password: "password");
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "a.txt", "b.bin" }));
    Assert.That(r.Entries.All(e => e.IsEncrypted), Is.True);
    Assert.That(r.Extract(0), Is.EqualTo(ATxt));
    Assert.That(r.Extract(1), Is.EqualTo(BBin));
  }

  private static readonly byte[] CTxt = Encoding.ASCII.GetBytes(string.Concat(
    Enumerable.Range(0, 120).Select(i => $"line {i}: the quick brown fox jumps over the lazy dog\n")));

  [Test, Category("ThirdPartyVector")]
  public void Given_WinRar290PlainArchive_When_Extracted_Then_ContentsMatch() {
    using var r = new RarReader(new MemoryStream(Vector("winrar290-plain.rar")), password: null);
    Assert.That(r.Extract(0), Is.EqualTo(ATxt));
    Assert.That(r.Extract(2), Is.EqualTo(CTxt));
    Assert.That(r.Extract(1), Is.EqualTo(BBin));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_WinRar290EncryptedTextArchive_When_Extracted_Then_ContentsMatch() {
    using var r = new RarReader(new MemoryStream(Vector("winrar290-rar20-crypt-text.rar")), password: "password");
    Assert.That(r.Extract(0), Is.EqualTo(ATxt));
    Assert.That(r.Extract(1), Is.EqualTo(CTxt));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_LibarchiveRar4Archive_When_TheEncryptedEntryIsExtracted_Then_ContentsMatch() {
    using var r = new RarReader(new MemoryStream(Vector("libarchive-rar4-encrypted.rar")), password: "password");
    Assert.That(r.Extract(0), Is.EqualTo("This is from a.txt"u8.ToArray()));
    Assert.That(r.Extract(1), Is.EqualTo("This is from b.txt"u8.ToArray()));
  }

  [TestCase(RarConstants.Rar4MethodNormal)]
  [Category("ExternalTool")]
  public void Given_OurUnencryptedCompressedRar4Archive_When_SevenZipExtractsIt_Then_ContentMatches(byte method) {
    var sevenZip = ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");
    if (sevenZip == null) Assert.Ignore("7-Zip not found.");
    var dir = Path.Combine(Path.GetTempPath(), "rarplain_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "ours.rar");
      using (var fs = File.Create(path))
      using (var w = new Rar4Writer(fs, method: method)) {
        w.AddFile("b.bin", BBin);
        w.AddFile("c.txt", CTxt);
      }
      var psi = new ProcessStartInfo(sevenZip, ["x", "-y", "-o" + Path.Combine(dir, "out"), path]) {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
      };
      using var p = Process.Start(psi)!;
      var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
      p.WaitForExit();
      Assert.That(p.ExitCode, Is.Zero, output);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_WinRar290EncryptedArchive_When_ExtractedWithAWrongPassword_Then_TheCrcCheckRefusesIt() {
    using var r = new RarReader(new MemoryStream(Vector("winrar290-rar20-crypt-store.rar")), password: "Password");
    Assert.Throws<InvalidDataException>(() => r.Extract(0));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_WinRar290EncryptedArchive_When_ExtractedWithoutPassword_Then_Refused() {
    using var r = new RarReader(new MemoryStream(Vector("winrar290-rar20-crypt.rar")), password: null);
    Assert.Throws<InvalidOperationException>(() => r.Extract(0));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_LibarchiveAesArchive_When_ExtractedWithThePassword_Then_ContentsMatch() {
    using var r = new RarReader(new MemoryStream(Vector("libarchive-rar3-aes.rar")), password: "12345678");
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "foo.txt", "bar.txt" }));
    Assert.That(r.Extract(0), Is.EqualTo("data of foo.txt\n"u8.ToArray()));
    Assert.That(r.Extract(1), Is.EqualTo("data of bar.txt\n"u8.ToArray()));
  }

  [Test, Category("EdgeCase")]
  public void Given_Rar15EncryptedEntry_When_Extracted_Then_RefusedAsUnsupported() {
    var bytes = Vector("winrar290-rar20-crypt-store.rar");
    // first file header starts at 0x14; UNP_VER is at +24
    Assert.That(bytes[0x14 + 24], Is.EqualTo(20));
    bytes[0x14 + 24] = 15;
    using var r = new RarReader(new MemoryStream(bytes), password: "password");
    Assert.Throws<NotSupportedException>(() => r.Extract(0));
  }

  [Test, Category("Boundary")]
  public void Given_Rar20Cipher_When_EncryptedThenDecrypted_Then_RoundTripsAndLeavesAPartialTailAlone() {
    var plain = Enumerable.Range(0, 37).Select(i => (byte)(i * 7)).ToArray();
    var data = (byte[])plain.Clone();
    new Rar20Cipher("pw"u8).Encrypt(data);
    Assert.That(data[..32], Is.Not.EqualTo(plain[..32]));
    Assert.That(data[32..], Is.EqualTo(plain[32..]));
    new Rar20Cipher("pw"u8).Decrypt(data);
    Assert.That(data, Is.EqualTo(plain));
  }

  [TestCase(127)]
  [TestCase(200)]
  [Category("Boundary")]
  public void Given_LongPassword_When_Keyed_Then_OnlyThe127ByteKeyScheduleLimitCounts(int length) {
    var a = Enumerable.Repeat((byte)'x', length).ToArray();
    var b = Enumerable.Repeat((byte)'x', 127).ToArray();
    var blockA = new byte[16];
    var blockB = new byte[16];
    new Rar20Cipher(a).Encrypt(blockA);
    new Rar20Cipher(b).Encrypt(blockB);
    Assert.That(blockA, Is.EqualTo(blockB));
  }

  [Test, Category("Boundary")]
  public void Given_EmptyPassword_When_Keyed_Then_CipherStillRoundTrips() {
    var data = new byte[16];
    new Rar20Cipher([]).Encrypt(data);
    new Rar20Cipher([]).Decrypt(data);
    Assert.That(data, Is.EqualTo(new byte[16]));
  }

  [TestCase(RarConstants.Rar4MethodStore)]
  [TestCase(RarConstants.Rar4MethodNormal)]
  [Category("ExternalTool")]
  public void Given_OurEncryptedRar4Archive_When_SevenZipTestsIt_Then_ItDecryptsWithThePassword(byte method) {
    var sevenZip = ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");
    if (sevenZip == null) Assert.Ignore("7-Zip not found.");
    var dir = Path.Combine(Path.GetTempPath(), "rarenc_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "ours.rar");
      using (var fs = File.Create(path))
      using (var w = new Rar4Writer(fs, method: method, password: "secret")) {
        w.AddFile("a.txt", ATxt);
        w.AddFile("b.bin", BBin);
      }
      var psi = new ProcessStartInfo(sevenZip, ["x", "-y", "-psecret", "-o" + Path.Combine(dir, "out"), path]) {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
      };
      using var p = Process.Start(psi)!;
      var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
      p.WaitForExit();
      Assert.That(p.ExitCode, Is.Zero, output);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "out", "a.txt")), Is.EqualTo(ATxt));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "out", "b.bin")), Is.EqualTo(BBin));
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
