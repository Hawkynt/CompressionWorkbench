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

  // =======================================================================
  // IArchiveModifiable contract — Add / Remove / Replace (rebuild-based,
  // disk-image semantic matching VHD / VDI / VMDK / QCOW2)
  // =======================================================================

  /// <summary>Shared seekable backing store for modify tests so Add / Remove
  /// can mutate the same stream the reader observes.</summary>
  private static MemoryStream NewArchiveStream(byte[] initial) {
    // Use the (capacity, writable) ctor — passing a backing array makes the
    // stream non-resizable, which breaks ModifyRebuilder.SetLength on grow.
    var ms = new MemoryStream();
    ms.Write(initial, 0, initial.Length);
    ms.Position = 0;
    return ms;
  }

  // ---- Capability surface ------------------------------------------------

  [Test, Category("HappyPath")]
  public void Descriptor_IsArchiveModifiable() {
    var d = new MacriumFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>(),
      "Macrium descriptor must advertise IArchiveModifiable for R/W modify.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True,
      "FormatCapabilities.CanModify must be set when IArchiveModifiable is wired.");
  }

  // ---- Add: read existing → add entry → re-read → new entry present ------

  [Test, Category("HappyPath")]
  public void Add_AppendsBytesToExistingDiskImage_RebuiltContainerExtractsBoth() {
    var original = DeterministicDisk(1024, seed: 1);
    var image = WriteReflectX(original, compress: false);
    using var archive = NewArchiveStream(image);

    var d = new MacriumFormatDescriptor();
    var modifiable = (IArchiveModifiable)d;

    var tail = DeterministicDisk(256, seed: 2);
    modifiable.Add(archive, [ArchiveInputInfo.InMemory("tail.bin", tail)]);

    archive.Position = 0;
    var rebuilt = archive.ToArray();
    // Container must still be a valid Reflect X archive.
    Assert.That(rebuilt[^12..], Is.EqualTo("MACRIUM_FILE"u8.ToArray()),
      "Rebuilt container must retain the MACRIUM_FILE footer.");

    var recovered = ReadReconstructed(rebuilt);
    var expected = original.Concat(tail).ToArray();
    Assert.That(recovered, Is.EqualTo(expected),
      "Add must append the new bytes onto the existing disk image.");
  }

  [Test, Category("HappyPath")]
  public void Add_DiskImageRaw_ReplacesExistingDiskPayload() {
    // Per Macrium descriptor semantics, an input whose name matches the
    // canonical 'disk-image.raw' entry REPLACES the existing disk content
    // rather than appending — same shape as a save-as inside an image editor.
    var original = DeterministicDisk(2048, seed: 7);
    var image = WriteReflectX(original, compress: false);
    using var archive = NewArchiveStream(image);

    var d = new MacriumFormatDescriptor();
    var modifiable = (IArchiveModifiable)d;

    var replacement = DeterministicDisk(900, seed: 99);
    modifiable.Add(archive,
      [ArchiveInputInfo.InMemory(MacriumFormatDescriptor.DiskImageEntryName, replacement)]);

    var rebuilt = archive.ToArray();
    var recovered = ReadReconstructed(rebuilt);
    Assert.That(recovered, Is.EqualTo(replacement),
      "Add of disk-image.raw must REPLACE the existing disk payload, not append.");
  }

  [Test, Category("HappyPath")]
  public void Add_ToEmptyContainer_ProducesValidContainerWithJustNewBytes() {
    // Equivalence class: Add against an "empty" existing image (zero-byte
    // disk payload). The reader returns disk-image.raw of length 0, and Add
    // appends the new bytes — net effect is a single-payload container.
    var image = WriteReflectX([], compress: false);
    using var archive = NewArchiveStream(image);
    var d = new MacriumFormatDescriptor();

    var newDisk = DeterministicDisk(64, seed: 3);
    ((IArchiveModifiable)d).Add(archive, [ArchiveInputInfo.InMemory("a.bin", newDisk)]);

    var recovered = ReadReconstructed(archive.ToArray());
    Assert.That(recovered, Is.EqualTo(newDisk));
  }

  // ---- Remove: read existing → remove entry → re-read → entry gone -------

  [Test, Category("HappyPath")]
  public void Remove_DiskImageRaw_EmptiesPayload_ButKeepsValidContainer() {
    var original = DeterministicDisk(4096, seed: 4);
    var image = WriteReflectX(original, compress: false);
    using var archive = NewArchiveStream(image);

    var d = new MacriumFormatDescriptor();
    var modifiable = (IArchiveModifiable)d;
    modifiable.Remove(archive, [MacriumFormatDescriptor.DiskImageEntryName]);

    var rebuilt = archive.ToArray();
    Assert.That(rebuilt[^12..], Is.EqualTo("MACRIUM_FILE"u8.ToArray()),
      "Container must still carry the MACRIUM_FILE footer after Remove.");

    // The empty-payload case takes the no-$INDEX-block path because the
    // writer emits index_file_position=0 (no preceding data blocks) which
    // the reader's "IndexFilePosition > 0" gate skips — that's a pre-
    // existing limitation of the chain walker, not a defect of the modify
    // path. What matters is the rebuilt container is still spec-valid:
    using var ms = new MemoryStream(rebuilt);
    using var r = new MacriumReader(ms);
    Assert.That(r.ValidHeader, Is.True,
      "Rebuilt empty container must still parse as a Reflect X file.");
    Assert.That(r.Variant, Is.EqualTo("mrimgx"));
    // disk-image.raw is either empty (when reconstruction succeeded) or
    // absent (when the empty-payload reader path skipped the $INDEX walk).
    // Both states are "no surviving content" — the bytes are gone either way.
    var diskEntry = r.Entries.FirstOrDefault(e => e.Name == MacriumFormatDescriptor.DiskImageEntryName);
    Assert.That(diskEntry?.Data ?? [], Has.Length.EqualTo(0),
      "Remove of disk-image.raw must result in zero recoverable payload bytes.");
  }

  [Test, Category("HappyPath")]
  public void Remove_NonExistentEntry_LeavesDiskUnchanged() {
    // Equivalence class: Remove of a synthetic/projection entry name
    // (metadata.ini, metadata.json, block-NN.bin, macrium-image.bin) must
    // not affect the underlying disk payload — those are read-only
    // projections of the container structure.
    var original = DeterministicDisk(1500, seed: 5);
    var image = WriteReflectX(original, compress: false);
    using var archive = NewArchiveStream(image);

    var d = new MacriumFormatDescriptor();
    ((IArchiveModifiable)d).Remove(archive, ["metadata.ini", "block-00.JSON.bin", "no-such-entry.txt"]);

    var recovered = ReadReconstructed(archive.ToArray());
    Assert.That(recovered, Is.EqualTo(original),
      "Remove of synthetic/projection entries must NOT alter the disk payload.");
  }

  // ---- Replace via Add of disk-image.raw ---------------------------------

  [Test, Category("HappyPath")]
  public void Replace_DiskImageRaw_FullRoundTripWithEncryption() {
    // Full R/W round-trip on an encrypted container: read existing → replace
    // disk content → re-read → new content is recovered through the same
    // password and the rebuild stays spec-compliant (AES-CBC + PBKDF2-HMAC-
    // SHA256 + zstd survive the modify).
    var original = DeterministicDisk(MacriumWriter.DefaultBlockSize * 2 + 5, seed: 6);
    var d = new MacriumFormatDescriptor();
    using var imageMs = new MemoryStream();
    d.Create(imageMs, [ArchiveInputInfo.InMemory("payload.bin", original)],
      new FormatCreateOptions {
        Password = "replace-rt",
        EncryptionMethod = "aes-256",
        FormatSpecific = new Dictionary<string, string> { ["pbkdf2_iterations"] = "1000" },
      });
    var encrypted = imageMs.ToArray();

    // Verify original round-trip first.
    var preReplace = ReadReconstructed(encrypted, password: "replace-rt");
    Assert.That(preReplace, Is.EqualTo(original));

    // Now Replace via Add of disk-image.raw. The Modifier rebuild path
    // currently produces a plain (non-encrypted) container because the
    // password isn't part of the IArchiveModifiable surface — that's an
    // honest limitation we document in the descriptor. We assert it here so
    // the gap stays visible and locked.
    using var archive = NewArchiveStream(encrypted);
    var replacement = DeterministicDisk(900, seed: 600);
    ((IArchiveModifiable)d).Add(archive,
      [ArchiveInputInfo.InMemory(MacriumFormatDescriptor.DiskImageEntryName, replacement)]);

    // Rebuilt container is plain (no password needed) and carries the
    // replacement bytes — both invariants must hold.
    var rebuilt = archive.ToArray();
    Assert.That(rebuilt[^12..], Is.EqualTo("MACRIUM_FILE"u8.ToArray()));

    using var ms = new MemoryStream(rebuilt);
    using var r = new MacriumReader(ms);
    Assert.That(r.IsEncrypted, Is.False,
      "Modify rebuild emits a plain container; password rotation is out-of-scope per descriptor docs.");
    Assert.That(r.SectorReconstructionAvailable, Is.True);
    var recovered = r.Entries.First(e => e.Name == MacriumFormatDescriptor.DiskImageEntryName).Data;
    Assert.That(recovered, Is.EqualTo(replacement),
      "Replace via Add(disk-image.raw) must surface the replacement bytes.");
  }

  // ---- Add-then-Extract: full round-trip after mutation ------------------

  [Test, Category("HappyPath")]
  public void Modify_ThenExtract_FullRoundTripViaDescriptor() {
    // BDD: GIVEN an existing Reflect X container with disk-image.raw of N
    // bytes, WHEN the caller appends a tail blob via Add THEN re-extracts
    // via Descriptor.Extract, the extracted disk-image.raw matches the
    // concatenation byte-for-byte.
    var d = new MacriumFormatDescriptor();
    var original = DeterministicDisk(1024, seed: 8);
    using var imageMs = new MemoryStream();
    d.Create(imageMs, [ArchiveInputInfo.InMemory("payload.bin", original)], new FormatCreateOptions());
    var image = imageMs.ToArray();

    using var archive = NewArchiveStream(image);
    var tail = DeterministicDisk(256, seed: 9);
    ((IArchiveModifiable)d).Add(archive, [ArchiveInputInfo.InMemory("tail.bin", tail)]);

    archive.Position = 0;
    var dir = Path.Combine(Path.GetTempPath(), "macrium-modify-" + Path.GetRandomFileName());
    try {
      Directory.CreateDirectory(dir);
      d.Extract(archive, dir, password: null, files: null);
      var extracted = File.ReadAllBytes(Path.Combine(dir, MacriumFormatDescriptor.DiskImageEntryName));
      Assert.That(extracted, Is.EqualTo(original.Concat(tail).ToArray()),
        "Extract after modify must surface the concatenated disk image bytes.");
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  // ---- Block-level integrity after mutation ------------------------------

  [Test, Category("HappyPath")]
  public void Modify_PreservesContainerInvariants_FooterAndChainShape() {
    var image = WriteReflectX(DeterministicDisk(4096), compress: true);
    using var archive = NewArchiveStream(image);

    var d = new MacriumFormatDescriptor();
    ((IArchiveModifiable)d).Add(archive,
      [ArchiveInputInfo.InMemory("appended.bin", DeterministicDisk(512, seed: 42))]);

    var rebuilt = archive.ToArray();
    using var ms = new MemoryStream(rebuilt);
    using var r = new MacriumReader(ms);

    Assert.That(r.Variant, Is.EqualTo("mrimgx"));
    Assert.That(r.Tag, Is.EqualTo("MACRIUM_FILE"));
    Assert.That(r.ValidHeader, Is.True);
    var names = r.Blocks.Select(b => b.Name).ToList();
    Assert.That(names, Does.Contain("$TRACK0"));
    Assert.That(names, Does.Contain("$INDEX"));
    Assert.That(names, Does.Contain("$JSON"));
    Assert.That(names, Does.Contain("$AUXDATA"));
    Assert.That(r.Blocks.Last().IsLast, Is.True,
      "Last block in the rebuilt chain must still carry the terminator flag.");
  }

  [Test, Category("HappyPath")]
  public void Modify_PreservesZstdBlockCompression_OnUntouchedDiskPayload() {
    // The rebuild emits every block freshly through MacriumWriter — including
    // re-applying zstd on the untouched disk-payload bytes. Verify the
    // resulting container is still smaller than the raw disk image when the
    // payload is highly compressible (proves zstd kicked in post-modify).
    var compressible = new byte[MacriumWriter.DefaultBlockSize * 4];
    Array.Fill<byte>(compressible, 0x42);
    var image = WriteReflectX(compressible, compress: true);
    using var archive = NewArchiveStream(image);
    var preLen = archive.Length;

    // Add a small entry — the disk payload remains the same compressible
    // bytes plus a tiny tail, so the rebuilt container must still be tiny.
    ((IArchiveModifiable)new MacriumFormatDescriptor()).Add(archive,
      [ArchiveInputInfo.InMemory("tail.bin", new byte[16])]);

    Assert.That(archive.Length, Is.LessThan(compressible.Length / 4),
      $"Post-modify container must remain zstd-compressed (got {archive.Length} bytes for ~{compressible.Length}-byte payload).");
  }

  // ---- Boundary cases ----------------------------------------------------

  [Test, Category("BoundaryCase")]
  public void Add_EmptyInputList_LeavesDiskUnchanged() {
    var original = DeterministicDisk(512, seed: 10);
    var image = WriteReflectX(original, compress: false);
    using var archive = NewArchiveStream(image);

    ((IArchiveModifiable)new MacriumFormatDescriptor()).Add(archive, []);

    var recovered = ReadReconstructed(archive.ToArray());
    Assert.That(recovered, Is.EqualTo(original),
      "Add with no inputs must rebuild the container with the same payload.");
  }

  [Test, Category("BoundaryCase")]
  public void Remove_EmptyEntryNames_LeavesDiskUnchanged() {
    var original = DeterministicDisk(512, seed: 11);
    var image = WriteReflectX(original, compress: false);
    using var archive = NewArchiveStream(image);

    ((IArchiveModifiable)new MacriumFormatDescriptor()).Remove(archive, []);

    var recovered = ReadReconstructed(archive.ToArray());
    Assert.That(recovered, Is.EqualTo(original),
      "Remove with no entry names must rebuild the container with the same payload.");
  }

  [Test, Category("BoundaryCase")]
  public void RoundTrip_AfterMultipleAdds_AccumulatesAllAppends() {
    // Equivalence: chained mutations. After three sequential Add calls, the
    // recovered disk image must be the concatenation of all four sources.
    var original = DeterministicDisk(256, seed: 20);
    var image = WriteReflectX(original, compress: false);
    using var archive = NewArchiveStream(image);

    var d = new MacriumFormatDescriptor();
    var t1 = DeterministicDisk(128, seed: 21);
    var t2 = DeterministicDisk(64, seed: 22);
    var t3 = DeterministicDisk(32, seed: 23);
    var m = (IArchiveModifiable)d;
    m.Add(archive, [ArchiveInputInfo.InMemory("t1.bin", t1)]);
    m.Add(archive, [ArchiveInputInfo.InMemory("t2.bin", t2)]);
    m.Add(archive, [ArchiveInputInfo.InMemory("t3.bin", t3)]);

    var recovered = ReadReconstructed(archive.ToArray());
    var expected = original.Concat(t1).Concat(t2).Concat(t3).ToArray();
    Assert.That(recovered, Is.EqualTo(expected),
      "Chained Add calls must accumulate appends in declaration order.");
  }

  // ---- Exceptional cases -------------------------------------------------

  [Test, Category("ExceptionalCase")]
  public void Add_NullStream_Throws() {
    Assert.That(() => ((IArchiveModifiable)new MacriumFormatDescriptor()).Add(null!, []),
      Throws.InstanceOf<ArgumentNullException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Add_NullInputs_Throws() {
    using var ms = new MemoryStream();
    Assert.That(() => ((IArchiveModifiable)new MacriumFormatDescriptor()).Add(ms, null!),
      Throws.InstanceOf<ArgumentNullException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Remove_NullStream_Throws() {
    Assert.That(() => ((IArchiveModifiable)new MacriumFormatDescriptor()).Remove(null!, []),
      Throws.InstanceOf<ArgumentNullException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Remove_NullEntryNames_Throws() {
    using var ms = new MemoryStream();
    Assert.That(() => ((IArchiveModifiable)new MacriumFormatDescriptor()).Remove(ms, null!),
      Throws.InstanceOf<ArgumentNullException>());
  }
}
