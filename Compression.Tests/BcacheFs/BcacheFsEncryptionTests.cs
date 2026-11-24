using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// Encrypted bcachefs volumes (ChaCha20 data and metadata, Poly1305 MACs, a master key wrapped by
/// a scrypt-derived passphrase key): volumes bcachefs-tools and the 6.8 kernel wrote read back
/// file for file as <c>manifest-encrypted.txt</c> lists them; a missing or wrong passphrase, a
/// damaged extent and edits are refused. Provenance in <c>ReferenceVectors/README.md</c>.
/// </summary>
[TestFixture]
public sealed class BcacheFsEncryptionTests {
  private const string Passphrase = "Secr3t";
  private const string ToolsKey = "bcachefs-tools-1.39-encrypted-key.img.gz";
  private const string Kernel = "linux-6.8-encrypted.img.gz";
  private const string KernelZstdWide = "linux-6.8-encrypted-zstd-widemacs.img.gz";

  private static MemoryStream Image(string name) {
    using var resource = typeof(BcacheFsEncryptionTests).Assembly
      .GetManifestResourceStream("BcacheFsVectors." + name)
      ?? throw new FileNotFoundException($"embedded reference vector {name} is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return image;
  }

  private static Dictionary<string, (string Sha256, long Size)> Manifest() {
    using var resource = typeof(BcacheFsEncryptionTests).Assembly
      .GetManifestResourceStream("BcacheFsVectors.manifest-encrypted.txt")!;
    using var text = new StreamReader(resource);
    var files = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
    while (text.ReadLine() is { } line) {
      if (line.Length == 0) continue;
      var parts = line.Split(' ', 3);
      files[parts[2]] = (parts[0], long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }
    return files;
  }

  /// <summary>Every file of the tree, by content and length, and the link, as the tools and the kernel stored them.</summary>
  [TestCase(Kernel, null, TestName = "kernel 6.8, plain extents, 80-bit MACs")]
  [TestCase(KernelZstdWide, null, TestName = "kernel 6.8, zstd extents, 128-bit MACs")]
  [Category("ThirdPartyProof")]
  public void GivenAnEncryptedVolume_WhenReadWithItsKey_ThenEveryFileMatchesTheManifest(string image, string? passphrase) {
    using var stream = Image(image);
    using var reader = new BcacheFsReader(stream, passphrase);
    var manifest = Manifest();
    Assert.Multiple(() => {
      Assert.That(reader.Valid, Is.True, reader.Status);
      Assert.That(reader.Encrypted, Is.True);
      var files = reader.Entries.Where(e => e.LinkTarget == null).ToDictionary(e => e.Name, StringComparer.Ordinal);
      Assert.That(files.Keys.Order(StringComparer.Ordinal), Is.EqualTo(manifest.Keys.Order(StringComparer.Ordinal)));
      foreach (var (name, (sha, size)) in manifest) {
        if (!files.TryGetValue(name, out var entry)) continue;
        Assert.That(entry.Unreadable, Is.Null, name);
        var bytes = reader.Read(entry);
        Assert.That(bytes.LongLength, Is.EqualTo(size), name);
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(sha), name);
      }
      Assert.That(reader.Entries.Single(e => e.Name == "link").LinkTarget, Is.EqualTo("text/odd-size.txt"));
    });
  }

  /// <summary>
  /// The master key bcachefs-tools wrapped with the scrypt-derived passphrase key unwraps to the
  /// key magic with the passphrase. (The tools leave this volume at its superblock: they could
  /// not reach the kernel keyring to open it, so it carries the wrapped key and no files.)
  /// </summary>
  [Test, Category("ThirdPartyProof")]
  public void GivenAKeyTheToolsWrapped_WhenUnlockedWithThePassphrase_ThenItUnwraps() {
    using var stream = Image(ToolsKey);
    var superblock = BcacheFsDeviceSuperblocks.Read(stream).Current!;
    Assert.Multiple(() => {
      Assert.That(BcacheFsCrypt.NeedsPassphrase(superblock), Is.True);
      Assert.That(BcacheFsCrypt.Unlock(superblock, Passphrase), Is.Not.Null);
      Assert.Throws<UnauthorizedAccessException>(() => BcacheFsCrypt.Unlock(superblock, "Secr3T"));
    });
  }

  [TestCase(null)]
  [TestCase("")]
  [TestCase("secr3t")]
  [TestCase("Secr3t ")]
  [Category("Exception")]
  public void GivenAWrappedKey_WhenOpenedWithoutTheRightPassphrase_ThenItAsksForIt(string? passphrase) {
    using var stream = Image(ToolsKey);
    using var reader = new BcacheFsReader(stream, passphrase);
    Assert.Multiple(() => {
      Assert.That(reader.Valid, Is.False);
      Assert.That(reader.NeedsPassphrase, Is.True, reader.Status);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAKeyStoredInTheClear_WhenOpenedWithAnyPassphrase_ThenItReads() {
    using var stream = Image(Kernel);
    using var reader = new BcacheFsReader(stream, "ignored");
    Assert.That(reader.Valid, Is.True, reader.Status);
  }

  [Test, Category("HappyPath")]
  public void GivenAPassphraseVolume_WhenListedWithoutThePassphrase_ThenItDoesNotThrow() {
    var descriptor = new BcacheFsFormatDescriptor();
    using var stream = Image(ToolsKey);
    var list = descriptor.List(stream, null);
    Assert.That(list.Select(e => e.Name), Does.Contain("FULL.bcachefs"));
  }

  /// <summary>A passphrase volume holding bin/pattern.bin, written here (see BcacheFsEncryptedWriterTests for its oracles).</summary>
  private static MemoryStream OwnPassphraseVolume() {
    var writer = new BcacheFsWriter();
    writer.SetEncryption(Passphrase);
    writer.AddFile("bin/pattern.bin", Enumerable.Range(0, 300_000).Select(i => (byte)((i * 31 + i / 977) & 255)).ToArray());
    var stream = new MemoryStream();
    writer.WriteTo(stream);
    stream.Position = 0;
    return stream;
  }

  [Test, Category("HappyPath")]
  public void GivenAPassphraseVolume_WhenListedWithThePassphrase_ThenItsFilesAreListedAsEncrypted() {
    var descriptor = new BcacheFsFormatDescriptor();
    using var stream = OwnPassphraseVolume();
    var list = descriptor.List(stream, Passphrase);
    var file = list.Single(e => e.Name == "bin/pattern.bin");
    Assert.Multiple(() => {
      Assert.That(file.IsEncrypted, Is.True);
      Assert.That(file.OriginalSize, Is.EqualTo(300_000));
    });
  }

  [Test, Category("Exception")]
  public void GivenAPassphraseVolume_WhenExtractedWithoutThePassphrase_ThenItAsksForIt() {
    var descriptor = new BcacheFsFormatDescriptor();
    using var stream = Image(ToolsKey);
    var dir = Directory.CreateTempSubdirectory("cwb-bcachefs-enc-").FullName;
    try {
      Assert.Throws<UnauthorizedAccessException>(() => descriptor.Extract(stream, dir, null, null));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void GivenAPassphraseVolume_WhenExtractedWithThePassphrase_ThenTheFilesAreDecrypted() {
    var descriptor = new BcacheFsFormatDescriptor();
    using var stream = OwnPassphraseVolume();
    var dir = Directory.CreateTempSubdirectory("cwb-bcachefs-enc-").FullName;
    try {
      descriptor.Extract(stream, dir, Passphrase, null);
      var bytes = File.ReadAllBytes(Path.Combine(dir, "bin", "pattern.bin"));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(Manifest()["bin/pattern.bin"].Sha256));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("Exception")]
  public void GivenADamagedEncryptedExtent_WhenRead_ThenTheMacRefusesIt() {
    using var stream = Image(Kernel);
    var bytes = stream.ToArray();
    long sector;
    using (var reader = new BcacheFsReader(new MemoryStream(bytes), null))
      sector = reader.Entries.Single(e => e.Name == "bin/pattern.bin").Extents[0].FirstSector;
    bytes[sector * 512 + 100] ^= 0x01;
    using var damaged = new BcacheFsReader(new MemoryStream(bytes), null);
    Assert.That(damaged.Valid, Is.True, "metadata is intact");
    Assert.Throws<InvalidDataException>(() => damaged.Read(damaged.Entries.Single(e => e.Name == "bin/pattern.bin")));
  }

}
