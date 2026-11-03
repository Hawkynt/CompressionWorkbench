using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Core.Crypto;
using Compression.Registry;
using FileFormat.Qcow2;

namespace Compression.Tests.Qcow2;

/// <summary>
/// Encrypted QCOW2 images: legacy AES (crypt_method 1) and LUKS (crypt_method 2) images qemu-img
/// wrote (ReferenceVectors/README.md) decrypt to the guest disk qemu-img decrypts them to; our
/// LUKS images decrypt in qemu-img; wrong or missing passwords and edits are refused.
/// </summary>
[TestFixture]
public sealed class Qcow2EncryptionTests {
  private const string Password = "Secr3t";

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Qcow2Vectors." + name)
      ?? throw new InvalidOperationException($"Missing embedded vector {name}.");
    using var copy = new MemoryStream();
    if (name.EndsWith(".gz", StringComparison.Ordinal)) {
      using var gzip = new GZipStream(stream, CompressionMode.Decompress);
      gzip.CopyTo(copy);
    } else
      stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static Dictionary<string, string> Manifest()
    => System.Text.Encoding.UTF8.GetString(Resource("qemu-encrypted.sha256"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split("  "))
      .ToDictionary(f => f[1], f => f[0]);

  private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  private static byte[] Guest(int size, int seed) {
    var guest = new byte[size];
    var text = "luks inside qcow2 "u8;
    for (var i = 0; i < size / 2; ++i)
      guest[i] = text[i % text.Length];
    new Random(seed).NextBytes(guest.AsSpan(size * 3 / 4));
    return guest;
  }

  private static byte[] WriteOurs(byte[] guest, string password, int iterations = 1000) {
    var writer = new Qcow2Writer();
    writer.SetDiskImage(guest);
    writer.SetPassword(password, iterations);
    using var ms = new MemoryStream();
    writer.WriteTo(ms);
    return ms.ToArray();
  }

  [TestCase("enc-aes.qcow2")]
  [TestCase("enc-luks-cbc-essiv.qcow2")]
  [Category("ThirdPartyProof")]
  public void Given_AQemuEncryptedImage_When_ReadWithThePassword_Then_ItMatchesQemuImgsDecryption(string image) {
    using var reader = new Qcow2Reader(new MemoryStream(Resource(image + ".gz")), Password);
    Assert.That(reader.IsEncrypted);
    Assert.That(Sha256(reader.ExtractDisk()), Is.EqualTo(Manifest()[image]));
  }

  [Test]
  [Category("ThirdPartyProof")]
  public void Given_AQemuLuksImage_When_StreamedInOddPieces_Then_EveryByteMatches() {
    using var reader = new Qcow2Reader(new MemoryStream(Resource("enc-luks-cbc-essiv.qcow2.gz")), Password);
    var whole = reader.ExtractDisk();
    using var guest = Qcow2Stream.Open(new MemoryStream(Resource("enc-luks-cbc-essiv.qcow2.gz")), Password);
    var pieces = new byte[whole.Length];
    var at = 0;
    foreach (var step in Enumerable.Repeat(new[] { 1, 511, 513, 4095, 7 }, 1000).SelectMany(s => s)) {
      if (at >= pieces.Length)
        break;
      var n = Math.Min(step, pieces.Length - at);
      guest.ReadExactly(pieces, at, n);
      at += n;
    }
    Assert.That(pieces, Is.EqualTo(whole));
  }

  [TestCase("enc-aes.qcow2")]
  [TestCase("enc-luks-cbc-essiv.qcow2")]
  public void Given_AnEncryptedImage_When_ListedWithoutPassword_Then_TheDiskIsListedAsEncrypted(string image) {
    var descriptor = new Qcow2FormatDescriptor();
    var list = descriptor.List(new MemoryStream(Resource(image + ".gz")), null);
    Assert.That(list, Has.Count.EqualTo(1));
    Assert.That(list[0].IsEncrypted);
    Assert.That(list[0].OriginalSize, Is.EqualTo(64 * 1024));
  }

  [TestCase("enc-aes.qcow2")]
  [TestCase("enc-luks-cbc-essiv.qcow2")]
  public void Given_AnEncryptedImage_When_ExtractedWithoutPassword_Then_APasswordIsAskedFor(string image) {
    using var reader = new Qcow2Reader(new MemoryStream(Resource(image + ".gz")));
    Assert.Throws<UnauthorizedAccessException>(() => reader.ExtractDisk());
  }

  [Test]
  public void Given_ALuksImage_When_TheWrongPasswordIsGiven_Then_ItIsRefused() {
    using var reader = new Qcow2Reader(new MemoryStream(Resource("enc-luks-cbc-essiv.qcow2.gz")), "wrong");
    Assert.Throws<UnauthorizedAccessException>(() => reader.ExtractDisk());
  }

  [Test]
  public void Given_AnEncryptedImage_When_ExtractedThroughTheDescriptor_Then_TheDiskIsDecrypted() {
    var dir = Directory.CreateTempSubdirectory("cwb-qcow2-enc-").FullName;
    try {
      new Qcow2FormatDescriptor().Extract(new MemoryStream(Resource("enc-luks-cbc-essiv.qcow2.gz")), dir, Password, null);
      Assert.That(Sha256(File.ReadAllBytes(Path.Combine(dir, "disk.img"))), Is.EqualTo(Manifest()["enc-luks-cbc-essiv.qcow2"]));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_AnEncryptedImage_When_MaintenanceIsAsked_Then_ItIsRefusedAndTheImageUnchanged() {
    var image = Resource("enc-luks-cbc-essiv.qcow2.gz");
    using var stream = new MemoryStream();
    stream.Write(image);
    var descriptor = new Qcow2FormatDescriptor();
    Assert.Throws<NotSupportedException>(() => descriptor.Defragment(stream));
    Assert.That(stream.ToArray(), Is.EqualTo(image));
    stream.Position = 0;
    using (var guest = Qcow2Stream.Open(stream, Password))
      Assert.That(guest.CanWrite, Is.False);
    Assert.That(Qcow2LayoutMap.Enumerate(new MemoryStream(image)), Is.Empty);
  }

  [Test]
  public void Given_OurLuksImage_When_ReadBack_Then_TheGuestRoundTrips() {
    var guest = Guest(300_000, 3);
    var image = WriteOurs(guest, "pässwörd");
    using var reader = new Qcow2Reader(new MemoryStream(image), "pässwörd");
    Assert.That(reader.ExtractDisk(), Is.EqualTo(guest));
    using var wrong = new Qcow2Reader(new MemoryStream(image), "passwort");
    Assert.Throws<UnauthorizedAccessException>(() => wrong.ExtractDisk());
  }

  [Test]
  public void Given_OurLuksImage_When_Inspected_Then_TheGuestDataIsNotStoredInTheClear() {
    var guest = Guest(200_000, 4);
    var image = WriteOurs(guest, Password);
    var text = "luks inside qcow2 "u8.ToArray();
    Assert.That(image.AsSpan().IndexOf(text), Is.EqualTo(-1));
  }

  [Test]
  public void Given_ADescriptorCreateWithPassword_When_ReadBack_Then_TheImageIsEncrypted() {
    var descriptor = new Qcow2FormatDescriptor();
    using var ms = new MemoryStream();
    descriptor.Create(ms, [ArchiveInputInfo.InMemory("a.txt", "hello"u8.ToArray())], new FormatCreateOptions { Password = Password });
    ms.Position = 0;
    using var reader = new Qcow2Reader(ms, Password);
    Assert.That(reader.IsEncrypted);
    Assert.That(reader.ExtractDisk().AsSpan().IndexOf("hello"u8), Is.GreaterThan(0));
  }

  [Test]
  public void Given_TheLuksSplitter_When_SplitAndMerged_Then_TheKeyReturns() {
    var key = RandomNumberGenerator.GetBytes(64);
    foreach (var hash in new[] { HashAlgorithmName.SHA1, HashAlgorithmName.SHA256, HashAlgorithmName.SHA512 }) {
      var material = AntiForensicSplitter.Split(key, 17, hash);
      Assert.That(AntiForensicSplitter.Merge(material, 64, 17, hash), Is.EqualTo(key), hash.Name);
    }
  }

  [TestCase("serpent", "xts-plain64")]
  [TestCase("aes", "ctr-plain64")]
  [TestCase("aes", "cbc-benbi")]
  public void Given_AnUnsupportedLuksCipher_When_Created_Then_ItIsRefusedByName(string cipher, string mode)
    => Assert.Throws<NotSupportedException>(() => Luks1SectorCipher.Create(cipher, mode, new byte[32]));

  private static void RequireQemu() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("qemu-img"))
      Assert.Ignore("qemu-img is needed in WSL (sudo apt install -y qemu-utils).");
  }

  /// <summary>qemu-img must decrypt our LUKS image to the guest disk we encrypted, and find it consistent.</summary>
  [Test]
  [Category("ExternalInterop")]
  public void Given_OurLuksImage_When_QemuImgDecryptsIt_Then_TheGuestMatches() {
    RequireQemu();
    var dir = Directory.CreateTempSubdirectory("cwb-qcow2-enc-").FullName;
    try {
      var guest = Guest(1 << 20, 5);
      File.WriteAllBytes(Path.Combine(dir, "guest.raw"), guest);
      File.WriteAllBytes(Path.Combine(dir, "ours.qcow2"), WriteOurs(guest, Password, 20_000));
      var w = FsInteropToolbox.WinToWsl(dir);
      var (stdout, stderr, code) = FsInteropToolbox.RunWsl(
        $"cd {w} && qemu-img check -q --object secret,id=s0,data={Password} --image-opts " +
        "'driver=qcow2,file.filename=ours.qcow2,encrypt.key-secret=s0' && " +
        $"qemu-img compare --object secret,id=s0,data={Password} --image-opts " +
        "'driver=qcow2,file.filename=ours.qcow2,encrypt.key-secret=s0' 'driver=raw,file.filename=guest.raw' && " +
        "qemu-img info ours.qcow2 | grep -E 'encrypted: yes|cipher mode: xts'");
      Assert.That(code, Is.Zero, stdout + stderr);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  /// <summary>Further LUKS profiles qemu-img writes, each read back against qemu-img's decryption.</summary>
  [TestCase("")]
  [TestCase(",encrypt.cipher-alg=aes-256,encrypt.cipher-mode=cbc,encrypt.ivgen-alg=plain64,encrypt.hash-alg=sha512")]
  [TestCase(",encrypt.cipher-alg=aes-192,encrypt.cipher-mode=xts,encrypt.ivgen-alg=plain,encrypt.hash-alg=sha384")]
  [TestCase(",encrypt.cipher-alg=aes-128,encrypt.cipher-mode=ecb,encrypt.hash-alg=sha1")]
  [Category("ExternalInterop")]
  public void Given_AQemuLuksProfile_When_Read_Then_ItMatchesQemuImgsDecryption(string options) {
    RequireQemu();
    var dir = Directory.CreateTempSubdirectory("cwb-qcow2-enc-").FullName;
    try {
      var guest = Guest(256 * 1024, 6);
      File.WriteAllBytes(Path.Combine(dir, "guest.raw"), guest);
      var w = FsInteropToolbox.WinToWsl(dir);
      var (stdout, stderr, code) = FsInteropToolbox.RunWsl(
        $"cd {w} && qemu-img convert --object secret,id=s0,data={Password} -f raw -O qcow2 " +
        $"-o encrypt.format=luks,encrypt.key-secret=s0,encrypt.iter-time=10{options} guest.raw theirs.qcow2");
      Assert.That(code, Is.Zero, stdout + stderr);
      using var file = File.OpenRead(Path.Combine(dir, "theirs.qcow2"));
      using var reader = new Qcow2Reader(file, Password);
      Assert.That(reader.ExtractDisk(), Is.EqualTo(guest));
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
