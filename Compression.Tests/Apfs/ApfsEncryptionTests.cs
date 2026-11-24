using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// Software-encrypted APFS volumes: the RFC 3394 unwrap on its published vectors, and a
/// FileVault volume written by macOS, unlocked with its password and compared with what
/// apfs-fuse read from it (<c>ReferenceVectors/dissect-encrypted.manifest</c>).
/// </summary>
/// <remarks>
/// The volume comes from fox-it/dissect.apfs's test data, which is AGPL-3.0: it is
/// downloaded at test time at a pinned revision and checked against its SHA-256, never
/// committed. Without network access those tests are skipped.
/// </remarks>
[TestFixture]
public sealed class ApfsEncryptionTests {

  private const string SampleUrl =
    "https://media.githubusercontent.com/media/fox-it/dissect.apfs/a5626763922a16182a670e4906cfb8b88cf4baa6/tests/_data/encrypted.bin.gz";
  private const string SampleSha256 = "9780647470fd4a818c0c7ae86f70f2b7d39c48a5ccf2f89f0f2bd47d35915f1d";

  private static byte[]? _sample;

  private static byte[] Sample() {
    if (_sample is not null) return _sample;
    var cache = Path.Combine(Path.GetTempPath(), "cwb-dissect-apfs-encrypted.bin.gz");
    byte[] gz;
    if (File.Exists(cache) && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(cache))) == SampleSha256) {
      gz = File.ReadAllBytes(cache);
    } else {
      try {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        gz = http.GetByteArrayAsync(SampleUrl).GetAwaiter().GetResult();
      } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
        Assert.Ignore($"the AGPL sample is downloaded at test time and the download failed: {ex.Message}");
        throw;
      }
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(gz)), Is.EqualTo(SampleSha256), "pinned sample changed upstream");
      try { File.WriteAllBytes(cache, gz); } catch (IOException) { /* cache is best effort */ }
    }
    using var input = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
    using var output = new MemoryStream();
    input.CopyTo(output);
    return _sample = output.ToArray();
  }

  private static IEnumerable<(char Kind, string Value, string Path)> Manifest() {
    using var resource = typeof(ApfsEncryptionTests).Assembly.GetManifestResourceStream("ApfsVectors.dissect-encrypted.manifest")!;
    using var text = new StreamReader(resource);
    while (text.ReadLine() is { } line) {
      if (line.Length < 4 || line[0] == '#') continue;
      var parts = line.Split(' ', 3);
      yield return (parts[0][0], parts[1], parts[2]);
    }
  }

  [Test, Category("ExternalInterop")]
  public void GivenAFileVaultVolume_WhenUnlockedWithItsPassword_ThenEveryFileMatchesApfsFuse() {
    using var reader = new ApfsReader(new MemoryStream(Sample(), writable: false), "password");
    Assert.That(reader.IsEncrypted, Is.True);
    var entries = reader.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.Multiple(() => {
      foreach (var (kind, value, path) in Manifest()) {
        // apfs-fuse names in the volume's own (decomposed or composed) form, as stored.
        Assert.That(entries.TryGetValue(path, out var entry), Is.True, $"{path} is listed");
        if (entry is null) continue;
        switch (kind) {
          case 'f':
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.Extract(entry))), Is.EqualTo(value), path);
            break;
          case 'l':
            Assert.That(entry.LinkTarget, Is.EqualTo(value), path);
            break;
          default:
            Assert.That(entry.Size, Is.Zero, $"{path} (device or fifo) carries no data");
            break;
        }
      }
    });
  }

  [Test, Category("ExternalInterop")]
  public void GivenAFileVaultVolume_WhenOpenedWithoutAPassword_ThenAPasswordIsAskedFor() {
    var image = Sample();
    var ex = Assert.Throws<ArchivePasswordException>(() => _ = new ApfsReader(new MemoryStream(image, writable: false)));
    Assert.That(ex!.PasswordWasSupplied, Is.False);
  }

  [Test, Category("ExternalInterop")]
  public void GivenAFileVaultVolume_WhenOpenedWithAWrongPassword_ThenItIsRefused() {
    var image = Sample();
    var ex = Assert.Throws<ArchivePasswordException>(() => _ = new ApfsReader(new MemoryStream(image, writable: false), "not the password"));
    Assert.That(ex!.PasswordWasSupplied, Is.True);
  }

  [Test, Category("ExternalInterop")]
  public void GivenAFileVaultVolume_WhenListedThroughTheDescriptorWithItsPassword_ThenFilesAreListed() {
    var entries = new ApfsFormatDescriptor().List(new MemoryStream(Sample(), writable: false), "password");
    Assert.That(entries.Select(e => e.Name), Does.Contain("dir/file"));
  }

  // RFC 3394 section 4: wrapped key data and the key encryption keys that unwrap them.
  [TestCase("000102030405060708090A0B0C0D0E0F", "1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5", "00112233445566778899AABBCCDDEEFF")]
  [TestCase("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F",
    "64E8C3F9CE0F5BA263E9777905818A2A93C8191E7D6E8AE7", "00112233445566778899AABBCCDDEEFF")]
  [TestCase("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F",
    "28C9F404C4B810F4CBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43BFB988B9B7A02DD21",
    "00112233445566778899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F")]
  [Category("HappyPath")]
  public void GivenAnRfc3394Vector_WhenUnwrapped_ThenTheKeyDataIsRecovered(string kek, string wrapped, string expected) =>
    Assert.That(ApfsEncryption.AesKeyUnwrap(Convert.FromHexString(kek), Convert.FromHexString(wrapped)),
      Is.EqualTo(Convert.FromHexString(expected)));

  [Test, Category("Exception")]
  public void GivenAWrongKeyEncryptionKey_WhenUnwrapped_ThenTheIntegrityCheckFails() =>
    Assert.That(ApfsEncryption.AesKeyUnwrap(new byte[16], Convert.FromHexString("1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5")), Is.Null);

  [Test, Category("Exception")]
  public void GivenAPackedValueThatIsNotAKeyObject_WhenParsed_ThenItIsRejected() =>
    Assert.Throws<InvalidDataException>(() => ApfsEncryption.ParseWrappedKey([0x31, 0x00]));

  /// <summary>
  /// An encrypted volume is not written (apfsck cannot check one), and a password is not
  /// silently dropped either: Create refuses and writes nothing.
  /// </summary>
  [Test, Category("Exception")]
  public void GivenAPassword_WhenAVolumeIsCreated_ThenItIsRefusedAndNothingIsWritten() {
    var descriptor = new ApfsFormatDescriptor();
    using var output = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => descriptor.Create(output,
      [ArchiveInputInfo.InMemory("a.txt", "secret"u8.ToArray())], new FormatCreateOptions { Password = "pw" }));
    Assert.That(output.Length, Is.Zero);
  }
}
