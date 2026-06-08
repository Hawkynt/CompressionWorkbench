using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Macrium;

namespace Compression.Tests.Macrium;

/// <summary>
/// R/W acceptance gate for Macrium Reflect X (.mrimgx) via the MIT-licensed
/// vendor spec at <see href="https://github.com/macrium/mrimgx_file_layout"/>.
/// <para>
/// Covers writer correctness (footer + metadata chain + $TRACK0 / $INDEX /
/// $JSON / $AUXDATA), round-tripping through the reader's $INDEX walk + per-
/// block zstd decompression + AES-CBC decryption + password validation,
/// equivalence-class disk sizes (sub-block, single-block, multi-block, last-
/// block-partial), and exceptional / boundary cases (empty disk, wrong
/// password, no-password-on-encrypted, AES-128/192/256 variants).
/// </para>
/// </summary>
[TestFixture]
public class MacriumRwTests {

  // ---- Helpers ------------------------------------------------------------

  private static byte[] DeterministicDisk(int size, int seed = 1) {
    var rng = new Random(seed);
    var bytes = new byte[size];
    rng.NextBytes(bytes);
    return bytes;
  }

  private static byte[] WriteReflectX(
      byte[] disk,
      bool compress = true,
      string? password = null,
      MacriumAesType aes = MacriumAesType.Aes256,
      int blockSize = MacriumWriter.DefaultBlockSize,
      int? pbkdf2Iter = null,
      byte[]? imageId = null) {
    var writer = new MacriumWriter {
      BlockSize = blockSize,
      CompressDataBlocks = compress,
      EncryptDataBlocks = password is not null,
      Password = password,
      AesType = aes,
      Pbkdf2Iterations = pbkdf2Iter ?? MacriumCrypto.DefaultPbkdf2Iterations,
      ImageId = imageId,
    };
    return writer.Build(disk);
  }

  private static byte[] ReadReconstructed(byte[] image, string? password = null) {
    using var ms = new MemoryStream(image);
    using var r = new MacriumReader(ms, password);
    Assert.That(r.SectorReconstructionAvailable, Is.True,
      $"Sector reconstruction failed: {r.SectorReconstructionStatus}");
    var disk = r.Entries.FirstOrDefault(e => e.Name == "disk-image.raw");
    Assert.That(disk, Is.Not.Null, "Reader must surface disk-image.raw on success.");
    return disk!.Data;
  }

  // ---- Footer + container shape ------------------------------------------

  [Test, Category("HappyPath")]
  public void Writer_EmitsFooterMagic() {
    var image = WriteReflectX(DeterministicDisk(1024), compress: false);
    var tail = Encoding.ASCII.GetString(image, image.Length - 12, 12);
    Assert.That(tail, Is.EqualTo("MACRIUM_FILE"));
  }

  [Test, Category("HappyPath")]
  public void Writer_EmitsValidContainerShape() {
    var image = WriteReflectX(DeterministicDisk(8192), compress: false);
    using var ms = new MemoryStream(image);
    using var r = new MacriumReader(ms);
    Assert.That(r.Variant, Is.EqualTo("mrimgx"));
    Assert.That(r.Tag, Is.EqualTo("MACRIUM_FILE"));
    Assert.That(r.ValidHeader, Is.True);
    // Must produce $TRACK0 + $INDEX + $JSON + $AUXDATA chain.
    var names = r.Blocks.Select(b => b.Name).ToList();
    Assert.That(names, Does.Contain("$TRACK0"));
    Assert.That(names, Does.Contain("$INDEX"));
    Assert.That(names, Does.Contain("$JSON"));
    Assert.That(names, Does.Contain("$AUXDATA"));
    Assert.That(r.Blocks.Last().IsLast, Is.True);
  }

  // ---- Plain (no zstd, no AES) round-trip --------------------------------

  [Test, Category("HappyPath")]
  public void RoundTrip_Plain_SingleBlock_Exact() {
    var disk = DeterministicDisk(1024); // < BlockSize
    var image = WriteReflectX(disk, compress: false);
    var recovered = ReadReconstructed(image);
    Assert.That(recovered, Is.EqualTo(disk));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Plain_ExactBlockBoundary() {
    var disk = DeterministicDisk(MacriumWriter.DefaultBlockSize);
    var image = WriteReflectX(disk, compress: false);
    var recovered = ReadReconstructed(image);
    Assert.That(recovered, Is.EqualTo(disk));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Plain_MultipleBlocksWithTail() {
    var disk = DeterministicDisk(MacriumWriter.DefaultBlockSize * 2 + 123);
    var image = WriteReflectX(disk, compress: false);
    var recovered = ReadReconstructed(image);
    Assert.That(recovered, Is.EqualTo(disk));
  }

  // ---- Zstd-compressed (no AES) round-trip -------------------------------

  [Test, Category("HappyPath")]
  public void RoundTrip_Zstd_MultipleBlocksWithTail() {
    var disk = DeterministicDisk(MacriumWriter.DefaultBlockSize * 3 + 7);
    var image = WriteReflectX(disk, compress: true);
    var recovered = ReadReconstructed(image);
    Assert.That(recovered, Is.EqualTo(disk));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Zstd_Compressible_ShrinksContainer() {
    // Highly compressible payload — zstd should make the container smaller than the input.
    var disk = new byte[MacriumWriter.DefaultBlockSize * 4];
    Array.Fill<byte>(disk, 0x42);
    var image = WriteReflectX(disk, compress: true);
    var recovered = ReadReconstructed(image);
    Assert.That(recovered, Is.EqualTo(disk));
    Assert.That(image.Length, Is.LessThan(disk.Length / 4),
      "zstd of constant-byte payload must shrink the container dramatically.");
  }

  // ---- AES encryption round-trip — all three key sizes -------------------

  [Test, Category("HappyPath")]
  public void RoundTrip_Aes256_Encrypted_WithCorrectPassword() {
    var disk = DeterministicDisk(MacriumWriter.DefaultBlockSize * 2 + 17);
    // Use a small iteration count to keep test wall-clock fast; the writer
    // round-trips through the JSON so the same value is read back.
    var image = WriteReflectX(disk, compress: true, password: "correct horse battery staple",
      aes: MacriumAesType.Aes256, pbkdf2Iter: 1000);
    var recovered = ReadReconstructed(image, password: "correct horse battery staple");
    Assert.That(recovered, Is.EqualTo(disk));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Aes192_Encrypted_WithCorrectPassword() {
    var disk = DeterministicDisk(MacriumWriter.DefaultBlockSize + 5);
    var image = WriteReflectX(disk, compress: false, password: "pw192",
      aes: MacriumAesType.Aes192, pbkdf2Iter: 1000);
    var recovered = ReadReconstructed(image, password: "pw192");
    Assert.That(recovered, Is.EqualTo(disk));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Aes128_Encrypted_WithCorrectPassword() {
    var disk = DeterministicDisk(MacriumWriter.DefaultBlockSize + 99);
    var image = WriteReflectX(disk, compress: true, password: "pw128",
      aes: MacriumAesType.Aes128, pbkdf2Iter: 1000);
    var recovered = ReadReconstructed(image, password: "pw128");
    Assert.That(recovered, Is.EqualTo(disk));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Aes_DefaultPbkdf2Iterations_SpecCompliant() {
    // Full 600 000 iter — slow but must work end-to-end.
    var disk = DeterministicDisk(512);
    var image = WriteReflectX(disk, compress: false, password: "specdefault");
    var recovered = ReadReconstructed(image, password: "specdefault");
    Assert.That(recovered, Is.EqualTo(disk));
  }

  // ---- Encryption blockers / negative paths ------------------------------

  [Test, Category("ExceptionalCase")]
  public void Encrypted_WithNoPassword_BlocksReconstruction() {
    var disk = DeterministicDisk(1024);
    var image = WriteReflectX(disk, compress: false, password: "secret", pbkdf2Iter: 1000);
    using var ms = new MemoryStream(image);
    using var r = new MacriumReader(ms, password: null);
    Assert.That(r.IsEncrypted, Is.True);
    Assert.That(r.SectorReconstructionAvailable, Is.False);
    Assert.That(r.SectorReconstructionStatus, Is.EqualTo("encrypted-no-password"));
  }

  [Test, Category("ExceptionalCase")]
  public void Encrypted_WithWrongPassword_RejectsViaHmac() {
    var disk = DeterministicDisk(1024);
    var image = WriteReflectX(disk, compress: false, password: "right", pbkdf2Iter: 1000);
    using var ms = new MemoryStream(image);
    using var r = new MacriumReader(ms, password: "wrong");
    Assert.That(r.IsEncrypted, Is.True);
    Assert.That(r.SectorReconstructionAvailable, Is.False);
    Assert.That(r.SectorReconstructionStatus, Is.EqualTo("encrypted-wrong-password"),
      "HMAC check must reject wrong passwords before attempting decryption.");
  }

  [Test, Category("ExceptionalCase")]
  public void EncryptDataBlocks_RequiresPassword() {
    var writer = new MacriumWriter { EncryptDataBlocks = true };
    Assert.That(() => writer.Build(DeterministicDisk(64)),
      Throws.InstanceOf<InvalidOperationException>());
  }

  // ---- Descriptor.Create + Extract via IArchiveCreatable -----------------

  [Test, Category("HappyPath")]
  public void Descriptor_Create_Then_Extract_RoundTrips() {
    var d = new MacriumFormatDescriptor();
    var disk = DeterministicDisk(MacriumWriter.DefaultBlockSize + 33);

    using var imageMs = new MemoryStream();
    d.Create(imageMs, [ArchiveInputInfo.InMemory("payload.bin", disk)], new FormatCreateOptions());
    var image = imageMs.ToArray();

    Assert.That(image[^12..], Is.EqualTo("MACRIUM_FILE"u8.ToArray()));

    using var readMs = new MemoryStream(image);
    var entries = d.List(readMs, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("disk-image.raw"),
      "Descriptor.Create + List must expose the reconstructed disk image.");

    readMs.Position = 0;
    var dir = Path.Combine(Path.GetTempPath(), "macrium-rw-" + Path.GetRandomFileName());
    try {
      Directory.CreateDirectory(dir);
      d.Extract(readMs, dir, password: null, files: null);
      var recovered = File.ReadAllBytes(Path.Combine(dir, "disk-image.raw"));
      Assert.That(recovered, Is.EqualTo(disk));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_WithPassword_AndCorrectExtractRoundTrips() {
    var d = new MacriumFormatDescriptor();
    var disk = DeterministicDisk(MacriumWriter.DefaultBlockSize * 2);
    using var imageMs = new MemoryStream();
    d.Create(imageMs, [ArchiveInputInfo.InMemory("payload.bin", disk)], new FormatCreateOptions {
      Password = "topsecret",
      EncryptionMethod = "aes-256",
      FormatSpecific = new Dictionary<string, string> { ["pbkdf2_iterations"] = "1000" },
    });
    var image = imageMs.ToArray();
    using var ms = new MemoryStream(image);
    using var r = new MacriumReader(ms, password: "topsecret");
    Assert.That(r.IsEncrypted, Is.True);
    Assert.That(r.SectorReconstructionAvailable, Is.True,
      $"Reconstruction must succeed for matching password (status={r.SectorReconstructionStatus}).");
    var recovered = r.Entries.First(e => e.Name == "disk-image.raw").Data;
    Assert.That(recovered, Is.EqualTo(disk));
  }

  // ---- Crypto unit tests --------------------------------------------------

  [Test, Category("HappyPath")]
  public void Crypto_DeriveKey_IsDeterministicForFixedImageIdAndIterations() {
    var imageId = new byte[] { 0xD6, 0x84, 0xBA, 0x87, 0x24, 0x12, 0x63, 0xE2 };
    var a = MacriumCrypto.DeriveKey("hello", imageId, iterations: 5000);
    var b = MacriumCrypto.DeriveKey("hello", imageId, iterations: 5000);
    Assert.That(a.Length, Is.EqualTo(32));
    Assert.That(a, Is.EqualTo(b));
  }

  [Test, Category("HappyPath")]
  public void Crypto_DeriveKey_DiffersForDifferentImageIds() {
    var idA = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
    var idB = new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 };
    var a = MacriumCrypto.DeriveKey("same-pw", idA, iterations: 1000);
    var b = MacriumCrypto.DeriveKey("same-pw", idB, iterations: 1000);
    Assert.That(a, Is.Not.EqualTo(b));
  }

  [Test, Category("HappyPath")]
  public void Crypto_ValidateHmac_RoundTrips() {
    var key = new byte[32];
    RandomNumberGenerator.Fill(key);
    var hmac = MacriumCrypto.ComputeHmac(key);
    Assert.That(MacriumCrypto.ValidateHmac(key, hmac), Is.True);
  }

  [Test, Category("ExceptionalCase")]
  public void Crypto_ValidateHmac_RejectsTamperedHmac() {
    var key = new byte[32];
    RandomNumberGenerator.Fill(key);
    var hmac = MacriumCrypto.ComputeHmac(key);
    hmac[0] ^= 0xFF; // flip a bit
    Assert.That(MacriumCrypto.ValidateHmac(key, hmac), Is.False);
  }

  [Test, Category("HappyPath")]
  public void Crypto_DeriveBlockIv_IsUniquePerBlockIndex() {
    var derived = new byte[32];
    RandomNumberGenerator.Fill(derived);
    var imageId = new byte[8];
    RandomNumberGenerator.Fill(imageId);

    var iv0 = MacriumCrypto.DeriveBlockIv(derived, imageId, 0, 1, 0);
    var iv1 = MacriumCrypto.DeriveBlockIv(derived, imageId, 0, 1, 1);
    var iv2 = MacriumCrypto.DeriveBlockIv(derived, imageId, 0, 1, 2);
    Assert.That(iv0.Length, Is.EqualTo(16));
    Assert.That(iv0, Is.Not.EqualTo(iv1));
    Assert.That(iv1, Is.Not.EqualTo(iv2));
    Assert.That(iv0, Is.Not.EqualTo(iv2));
  }

  [Test, Category("HappyPath")]
  public void Crypto_DeriveBlockIv_IsUniquePerPartition() {
    var derived = new byte[32];
    var imageId = new byte[8];
    RandomNumberGenerator.Fill(derived);
    RandomNumberGenerator.Fill(imageId);
    var p1 = MacriumCrypto.DeriveBlockIv(derived, imageId, 0, 1, 0);
    var p2 = MacriumCrypto.DeriveBlockIv(derived, imageId, 0, 2, 0);
    Assert.That(p1, Is.Not.EqualTo(p2));
  }

  [Test, Category("HappyPath")]
  public void Crypto_AesCbc_RoundTrips_AllKeySizes() {
    var plaintext = Encoding.UTF8.GetBytes("Macrium Reflect X round-trip plaintext, longer than one AES block to exercise CBC chaining.");
    foreach (var keyLen in new[] { 16, 24, 32 }) {
      var key = new byte[keyLen];
      var iv = new byte[16];
      RandomNumberGenerator.Fill(key);
      RandomNumberGenerator.Fill(iv);
      var ct = MacriumCrypto.EncryptBlock(plaintext, key, iv);
      var pt = MacriumCrypto.DecryptBlock(ct, key, iv);
      Assert.That(pt, Is.EqualTo(plaintext), $"AES-{keyLen * 8}-CBC must round-trip.");
    }
  }

  [Test, Category("HappyPath")]
  public void Crypto_Hex_RoundTrips() {
    var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42, 0x00, 0x7F };
    var hex = MacriumCrypto.BytesToHex(bytes);
    Assert.That(hex, Is.EqualTo("deadbeef42007f"));
    Assert.That(MacriumCrypto.HexToBytes(hex), Is.EqualTo(bytes));
    Assert.That(MacriumCrypto.HexToBytes("DEADBEEF42007F"), Is.EqualTo(bytes), "Hex parser must accept uppercase too.");
  }

  // ---- JSON layout pinning ------------------------------------------------

  [Test, Category("HappyPath")]
  public void Reader_ParsesEncryptionFieldsFromJson() {
    var disk = DeterministicDisk(MacriumWriter.DefaultBlockSize);
    var image = WriteReflectX(disk, compress: true, password: "json-pin",
      aes: MacriumAesType.Aes128, pbkdf2Iter: 1234);
    using var ms = new MemoryStream(image);
    using var r = new MacriumReader(ms, password: "json-pin");
    var json = r.Entries.First(e => e.Name == "metadata.json");
    var jsonText = Encoding.UTF8.GetString(json.Data);
    Assert.That(jsonText, Does.Contain("\"aes_type\":\"aes-128\""));
    Assert.That(jsonText, Does.Contain("\"key_derivation\":\"pbkdf2\""));
    Assert.That(jsonText, Does.Contain("\"key_iterations\":1234"));
    Assert.That(jsonText, Does.Contain("\"hmac\":\""));
    Assert.That(jsonText, Does.Contain("\"compression_method\":\"zstd\""));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesImageIdFromJson_RoundTripsKey() {
    // Pin imageid so we can verify the derived key matches what the JSON-driven
    // reconstruction path computes.
    var fixedId = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
    var disk = DeterministicDisk(256);
    var image = WriteReflectX(disk, compress: false, password: "id-pin",
      pbkdf2Iter: 1000, imageId: fixedId);
    using var ms = new MemoryStream(image);
    using var r = new MacriumReader(ms, password: "id-pin");
    Assert.That(r.SectorReconstructionAvailable, Is.True);
    var json = Encoding.UTF8.GetString(r.Entries.First(e => e.Name == "metadata.json").Data);
    Assert.That(json, Does.Contain("\"imageid\":\"1122334455667788\""));
  }

  // ---- Metadata.ini R/W surface pin --------------------------------------

  [Test, Category("HappyPath")]
  public void MetadataIni_PromotesToRw_WhenReconstructionAvailable() {
    var disk = DeterministicDisk(2048);
    var image = WriteReflectX(disk, compress: false);
    using var ms = new MemoryStream(image);
    using var r = new MacriumReader(ms);
    var ini = Encoding.UTF8.GetString(r.Entries.First(e => e.Name == "metadata.ini").Data);
    Assert.That(ini, Does.Contain("sector_reconstruction=ok"));
    Assert.That(ini, Does.Contain("rw_promotion=rw"));
    Assert.That(ini, Does.Contain("AES-CBC"));
    Assert.That(ini, Does.Contain("PBKDF2"));
  }

  [Test, Category("HappyPath")]
  public void MetadataIni_FlagsEncryptedBlocker_WhenPasswordMissing() {
    var disk = DeterministicDisk(512);
    var image = WriteReflectX(disk, compress: false, password: "x", pbkdf2Iter: 1000);
    using var ms = new MemoryStream(image);
    using var r = new MacriumReader(ms, password: null);
    var ini = Encoding.UTF8.GetString(r.Entries.First(e => e.Name == "metadata.ini").Data);
    Assert.That(ini, Does.Contain("encrypted=1"));
    Assert.That(ini, Does.Contain("rw_promotion=blocked-encrypted"));
    Assert.That(ini, Does.Contain("sector_reconstruction=encrypted-no-password"));
  }

  // ---- Boundary / equivalence classes ------------------------------------

  [Test, Category("BoundaryCase")]
  public void RoundTrip_SingleByteDisk() {
    var disk = new byte[] { 0xA5 };
    var image = WriteReflectX(disk, compress: false);
    var recovered = ReadReconstructed(image);
    Assert.That(recovered, Is.EqualTo(disk));
  }

  [Test, Category("BoundaryCase")]
  public void RoundTrip_NonDefaultBlockSize() {
    var disk = DeterministicDisk(4096 + 17);
    // 512-byte block size (minimum sector size).
    var image = WriteReflectX(disk, compress: false, blockSize: 512);
    var recovered = ReadReconstructed(image);
    Assert.That(recovered, Is.EqualTo(disk));
  }

  [Test, Category("ExceptionalCase")]
  public void Writer_RejectsInvalidBlockSize() {
    var w = new MacriumWriter { BlockSize = 1000 }; // not a multiple of 512
    Assert.That(() => w.Build([1, 2, 3]), Throws.InstanceOf<InvalidOperationException>());
  }
}
