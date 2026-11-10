using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Lib;
using Compression.Mounting;
using Compression.Registry;
using Compression.Tests.Luks;
using FileFormat.BitLocker;

namespace Compression.Tests.BitLocker;

/// <summary>Key handling against data Windows wrote, and the recovery-password format.</summary>
[TestFixture]
public sealed class BitLockerKeyTests {
  internal static IEnumerable<string[]> Vectors(string kind) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BitLocker.windows-bitlocker.txt")!;
    using var reader = new StreamReader(stream);
    while (reader.ReadLine() is { } line)
      if (line.Split(' ') is { } parts && parts[0] == kind)
        yield return parts;
  }

  [Test]
  public void GivenWindowsWrappedVmks_WhenTheDocumentedPasswordsAreStretched_ThenBothUnwrapToTheSameVmk() {
    byte[]? first = null;
    foreach (var v in Vectors("vmk")) {
      var salt = Convert.FromHexString(v[2]);
      var wrapped = new BitLockerWrappedKey(Convert.FromHexString(v[3]), Convert.FromHexString(v[4]), Convert.FromHexString(v[5]));
      var key = v[1] == "Password"
        ? BitLockerKeys.StretchPassword("anaconda", salt)
        : BitLockerKeys.StretchRecoveryKey(BitLockerKeys.ParseRecoveryPassword("235818-357951-253979-013365-241120-245575-342914-591910")!, salt);
      var vmk = BitLockerKeys.Unwrap(wrapped, key);
      Assert.That(vmk, Is.Not.Null.And.Length.EqualTo(32), $"{v[1]} VMK does not unwrap");
      if (first is null) first = vmk;
      else Assert.That(vmk, Is.EqualTo(first));
    }
    Assert.That(first, Is.Not.Null);
  }

  [Test]
  public void GivenAWindowsWrappedVmk_WhenTheWrongPasswordIsStretched_ThenTheTagFails() {
    var v = Vectors("vmk").First(static v => v[1] == "Password");
    var wrapped = new BitLockerWrappedKey(Convert.FromHexString(v[3]), Convert.FromHexString(v[4]), Convert.FromHexString(v[5]));
    Assert.That(BitLockerKeys.Unwrap(wrapped, BitLockerKeys.StretchPassword("anacondA", Convert.FromHexString(v[2]))), Is.Null);
  }

  [Test]
  public void GivenAKeyDatum_WhenWrappedAndUnwrapped_ThenItRoundTripsAndATamperedTagFails() {
    var key = RandomNumberGenerator.GetBytes(32);
    var payload = RandomNumberGenerator.GetBytes(64);
    var nonce = RandomNumberGenerator.GetBytes(12);
    var wrapped = BitLockerKeys.Wrap(payload, 0x8005, key, nonce);
    Assert.That(wrapped.Ciphertext, Has.Length.EqualTo(76));
    Assert.That(BitLockerKeys.Unwrap(wrapped, key), Is.EqualTo(payload));
    var raw = BitLockerKeys.UnwrapRaw(wrapped, key)!;
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(raw), Is.EqualTo(76));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8)), Is.EqualTo(0x8005u));
    wrapped.Tag[3] ^= 1;
    Assert.That(BitLockerKeys.Unwrap(wrapped, key), Is.Null);
  }

  [TestCase("235818-357951-253979-013365-241120-245575-342914-591910")]
  [TestCase("000000-000000-000000-000000-000000-000000-000000-000000")]
  [TestCase("720885-720885-720885-720885-720885-720885-720885-720885")]
  public void GivenAValidRecoveryPassword_WhenParsedAndFormatted_ThenItRoundTrips(string text) {
    var key = BitLockerKeys.ParseRecoveryPassword(text);
    Assert.That(key, Is.Not.Null.And.Length.EqualTo(16));
    Assert.That(BitLockerKeys.FormatRecoveryPassword(key), Is.EqualTo(text));
    Assert.That(BitLockerKeys.ParseRecoveryPassword(text + "\n"), Is.EqualTo(key), "a trailing newline from a key file is tolerated");
  }

  [Test]
  public void GivenARecoveryPassword_WhenParsed_ThenEachGroupIsItsQuotientBy11LittleEndian() {
    var key = BitLockerKeys.ParseRecoveryPassword("000011-000022-000033-000044-000055-000066-720885-000000")!;
    Assert.That(Enumerable.Range(0, 8).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(key.AsSpan(i * 2))),
      Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6, 65535, 0 }));
  }

  [TestCase("235818-357951-253979-013365-241120-245575-342914-591911", TestName = "RecoveryPassword_GroupNotDivisibleBy11")]
  [TestCase("720896-357951-253979-013365-241120-245575-342914-591910", TestName = "RecoveryPassword_QuotientAbove65535")]
  [TestCase("235818-357951-253979-013365-241120-245575-342914", TestName = "RecoveryPassword_SevenGroups")]
  [TestCase("235818 357951 253979 013365 241120 245575 342914 591910", TestName = "RecoveryPassword_WrongSeparator")]
  [TestCase("23581a-357951-253979-013365-241120-245575-342914-591910", TestName = "RecoveryPassword_NotADigit")]
  [TestCase("anaconda", TestName = "RecoveryPassword_APassword")]
  public void GivenAMalformedRecoveryPassword_WhenParsed_ThenItIsNotOne(string text)
    => Assert.That(BitLockerKeys.ParseRecoveryPassword(text), Is.Null);

  [Test]
  public void GivenAGeneratedRecoveryPassword_WhenParsed_ThenItIsValid()
    => Assert.That(BitLockerKeys.ParseRecoveryPassword(BitLockerKeys.NewRecoveryPassword()), Is.Not.Null);
}

/// <summary>The sector transforms against sectors Windows wrote, and their own inverses.</summary>
[TestFixture]
public sealed class BitLockerSectorCipherTests {
  public static IEnumerable<TestCaseData> WindowsSectors()
    => BitLockerKeyTests.Vectors("sector").Select(static v => new TestCaseData((object)v).SetName("WindowsSector_" + v[1].Replace('-', '_')));

  private static BitLockerCipherMode Mode(string name) => name switch {
    "cbc-elephant" => BitLockerCipherMode.CbcElephant,
    "cbc-eboiv" => BitLockerCipherMode.CbcEboiv,
    _ => BitLockerCipherMode.XtsPlain64,
  };

  /// <summary>Each vector is a Windows-encrypted NTFS boot sector; decrypting it must give cryptsetup's plaintext, and re-encrypting it Windows' ciphertext.</summary>
  [TestCaseSource(nameof(WindowsSectors))]
  public void GivenAWindowsSector_WhenDecryptedAndReEncrypted_ThenBothDirectionsMatch(string[] v) {
    var ciphertext = Convert.FromHexString(v[6]);
    using var cipher = new BitLockerSectorCipher(Mode(v[2]), Convert.FromHexString(v[3]), 512);
    var data = ciphertext.ToArray();
    cipher.Decrypt(data, long.Parse(v[4]));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(v[5]));
    Assert.That(data.AsSpan(3, 8).ToArray(), Is.EqualTo("NTFS    "u8.ToArray()));
    cipher.Encrypt(data, long.Parse(v[4]));
    Assert.That(data, Is.EqualTo(ciphertext));
  }

  [TestCase(BitLockerCipherMode.XtsPlain64, 32, 512)]
  [TestCase(BitLockerCipherMode.XtsPlain64, 64, 4096)]
  [TestCase(BitLockerCipherMode.CbcEboiv, 16, 512)]
  [TestCase(BitLockerCipherMode.CbcEboiv, 32, 4096)]
  [TestCase(BitLockerCipherMode.CbcElephant, 32, 512)]
  [TestCase(BitLockerCipherMode.CbcElephant, 64, 4096)]
  public void GivenManySectors_WhenEncryptedInOneCallAndDecryptedOneByOne_ThenThePlaintextReturns(BitLockerCipherMode mode, int keyBytes, int sectorSize) {
    var key = RandomNumberGenerator.GetBytes(keyBytes);
    var plain = RandomNumberGenerator.GetBytes(sectorSize * 5);
    using var cipher = new BitLockerSectorCipher(mode, key, sectorSize);
    var data = plain.ToArray();
    cipher.Encrypt(data, 1234);
    Assert.That(data, Is.Not.EqualTo(plain));
    for (var s = 0; s < 5; ++s)
      cipher.Decrypt(data.AsSpan(s * sectorSize, sectorSize), 1234 + s);
    Assert.That(data, Is.EqualTo(plain));
    // The same sector at another number encrypts differently.
    var a = plain.AsSpan(0, sectorSize).ToArray();
    var b = plain.AsSpan(0, sectorSize).ToArray();
    cipher.Encrypt(a, 7);
    cipher.Encrypt(b, 8);
    Assert.That(a, Is.Not.EqualTo(b));
  }

  [TestCase(128)]
  [TestCase(1024)]
  public void GivenTheDiffusers_WhenEncryptedThenDecrypted_ThenTheyAreInverses(int words) {
    var data = Enumerable.Range(0, words).Select(static i => (uint)(i * 2654435761u)).ToArray();
    var copy = data.ToArray();
    BitLockerSectorCipher.DiffuserAEncrypt(copy);
    BitLockerSectorCipher.DiffuserBEncrypt(copy);
    Assert.That(copy, Is.Not.EqualTo(data));
    BitLockerSectorCipher.DiffuserBDecrypt(copy);
    BitLockerSectorCipher.DiffuserADecrypt(copy);
    Assert.That(copy, Is.EqualTo(data));
  }

  /// <summary>Ferguson's recurrences written out literally (indices mod n, n·cycles steps) agree with the optimised loops.</summary>
  [Test]
  public void GivenTheDiffusers_WhenComparedWithTheLiteralRecurrences_ThenTheyAgree() {
    var n = 128;
    var data = Enumerable.Range(0, n).Select(static i => (uint)(i * 0x9E3779B9u + 17)).ToArray();
    var literal = data.ToArray();
    int[] ra = [9, 0, 13, 0], rb = [0, 10, 0, 25];
    for (var i = 0; i < n * 3; ++i)
      literal[i % n] += literal[(i + 2) % n] ^ uint.RotateLeft(literal[(i + 5) % n], rb[i % 4]);
    for (var i = 0; i < n * 5; ++i)
      literal[i % n] += literal[(i - 2 + n) % n] ^ uint.RotateLeft(literal[(i - 5 + n) % n], ra[i % 4]);
    var ours = data.ToArray();
    BitLockerSectorCipher.DiffuserBDecrypt(ours);
    BitLockerSectorCipher.DiffuserADecrypt(ours);
    Assert.That(ours, Is.EqualTo(literal));
  }
}

/// <summary>Our writer against our reader, the descriptor, and mounting through the resolver.</summary>
[TestFixture]
public sealed class BitLockerTests {
  private const string Password = "cwb password";
  private const string Recovery = "235818-357951-253979-013365-241120-245575-342914-591910";
  private static readonly byte[] Content = Enumerable.Range(0, 7000).Select(static i => (byte)(i * 11 + 5)).ToArray();

  [OneTimeSetUp]
  public void RegisterFormats() => FormatRegistration.EnsureInitialized();

  internal static byte[] Payload(int sectors, int seed) {
    var data = new byte[sectors * 512];
    new Random(seed).NextBytes(data);
    return data;
  }

  internal static (MemoryStream Volume, BitLockerCreateResult Result) Create(byte[] payload, ushort method = 0x8004, string? password = Password) {
    var ms = new MemoryStream();
    var result = BitLockerWriter.Write(ms, payload, new BitLockerCreateOptions { Password = password, RecoveryPassword = Recovery, EncryptionMethod = method });
    ms.Position = 0;
    return (ms, result);
  }

  /// <summary>The whole decrypted view of <paramref name="volume"/>.</summary>
  internal static byte[] Decrypt(Stream volume, BitLockerCredentials credentials) {
    var v = BitLockerVolume.Read(volume);
    using var device = new BitLockerBlockDevice(volume, v, BitLockerFormatDescriptor.Unlock(v, credentials), writable: false);
    var all = new byte[device.Geometry.BlockCount * device.Geometry.LogicalBlockSize];
    device.ReadBlocks(0, all);
    return all;
  }

  private static byte[] Expected(byte[] payload, long volumeSize) {
    var expected = new byte[volumeSize];
    payload.CopyTo(expected, 0);
    return expected;
  }

  [TestCase((ushort)0x8004)]
  [TestCase((ushort)0x8005)]
  [TestCase((ushort)0x8002)]
  [TestCase((ushort)0x8001)]
  public void GivenOurVolume_WhenOpenedWithThePassword_ThenTheViewIsThePayloadFollowedByZeros(ushort method) {
    var payload = Payload(300, method);
    var (volume, result) = Create(payload, method);
    var v = BitLockerVolume.Read(volume);
    Assert.Multiple(() => {
      Assert.That(v.EncryptionMethod, Is.EqualTo(method));
      Assert.That(v.VolumeGuid, Is.EqualTo(result.VolumeGuid));
      Assert.That(v.VolumeSize, Is.EqualTo((ulong)volume.Length));
      Assert.That(v.MetadataOffsets, Is.EqualTo(result.MetadataOffsets));
      Assert.That(v.VolumeHeaderOffset, Is.EqualTo(result.VolumeHeaderOffset));
      Assert.That(v.Protectors.Select(static p => p.Protection), Is.EqualTo(new[] { BitLockerProtection.Password, BitLockerProtection.RecoveryPassword }));
      Assert.That(v.StateIsNormal && v.EncryptionType == BitLockerEncryptionType.Normal, Is.True);
    });
    Assert.That(Decrypt(volume, new(Password)), Is.EqualTo(Expected(payload, result.VolumeSize)));
  }

  [Test]
  public void GivenOurVolume_WhenOpenedWithTheRecoveryPassword_ThenTheViewMatchesEitherWay() {
    var payload = Payload(64, 3);
    var (volume, result) = Create(payload);
    Assert.That(result.RecoveryPassword, Is.EqualTo(Recovery));
    var expected = Expected(payload, result.VolumeSize);
    Assert.That(Decrypt(volume, new(null, Recovery)), Is.EqualTo(expected));
    Assert.That(Decrypt(volume, new(Recovery)), Is.EqualTo(expected), "the password is also tried as a recovery password, as cryptsetup does");
  }

  [Test]
  public void GivenTheWrongCredentials_WhenUnlocked_ThenTheyAreWrongAndMissingOnesAreMissing() {
    var (volume, _) = Create(Payload(32, 4));
    var v = BitLockerVolume.Read(volume);
    Assert.That(v.TryUnlock(new("not it", "000000-000000-000000-000000-000000-000000-000000-000000"), out _), Is.Null);
    var wrong = Assert.Throws<ArchivePasswordException>(() => BitLockerFormatDescriptor.Unlock(v, new("not it")));
    Assert.That(wrong!.PasswordWasSupplied, Is.True);
    var missing = Assert.Throws<ArchivePasswordException>(() => BitLockerFormatDescriptor.Unlock(v, new(null)));
    Assert.That(missing!.PasswordWasSupplied, Is.False);
  }

  [Test]
  public void GivenAVolumeWithoutAUserPassword_WhenCreated_ThenOnlyTheRecoveryPasswordOpensIt() {
    var payload = Payload(40, 5);
    var (volume, result) = Create(payload, password: null);
    Assert.That(result.PasswordProtectorId, Is.Null);
    Assert.That(BitLockerVolume.Read(volume).Protectors.Single().Protection, Is.EqualTo(BitLockerProtection.RecoveryPassword));
    Assert.That(Decrypt(volume, new(null, Recovery)).AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
  }

  [Test]
  public void GivenTheDescriptorWithoutARecoveryPassword_WhenCreating_ThenOnlyThePasswordProtectsIt() {
    var payload = Payload(40, 9);
    using var volume = new MemoryStream();
    new BitLockerFormatDescriptor().Create(volume, [ArchiveInputInfo.InMemory("volume.img", payload)], new FormatCreateOptions { Password = Password });
    Assert.That(BitLockerVolume.Read(volume).Protectors.Single().Protection, Is.EqualTo(BitLockerProtection.Password));
    Assert.That(Decrypt(volume, new(Password)).AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
  }

  [Test]
  public void GivenADamagedFirstMetadataCopy_WhenRead_ThenTheNextCopyIsUsed() {
    var payload = Payload(48, 6);
    var (volume, result) = Create(payload);
    var bytes = volume.ToArray();
    bytes[result.MetadataOffsets[0] + 200] ^= 0xFF;   // breaks copy 0's CRC-32
    using var damaged = new MemoryStream(bytes);
    var v = BitLockerVolume.Read(damaged);
    Assert.That(v.ValidCopy, Is.EqualTo(1));
    Assert.That(Decrypt(damaged, new(Password)).AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
    bytes[result.MetadataOffsets[1] + 200] ^= 0xFF;
    bytes[result.MetadataOffsets[2] + 200] ^= 0xFF;
    Assert.Throws<InvalidDataException>(() => BitLockerVolume.Read(new MemoryStream(bytes)));
  }

  [Test]
  public void GivenTamperedMetadataWithAValidCrc_WhenUnlocked_ThenTheValidationHashRejectsIt() {
    var (volume, result) = Create(Payload(48, 7));
    var bytes = volume.ToArray();
    foreach (var offset in result.MetadataOffsets) {
      // Change the description and recompute only the CRC-32: the VMK-sealed SHA-256 must catch it.
      var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)offset + 8)) << 4;
      bytes[offset + 112 + 8] ^= 0x01;
      var crc = Compression.Core.Checksums.Crc32.Compute(bytes.AsSpan((int)offset, blockSize));
      BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)offset + blockSize + 4), crc);
    }
    var v = BitLockerVolume.Read(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => v.TryUnlock(new(Password), out _));
  }

  [Test]
  public void GivenOurVolume_WhenListedWithoutAPassword_ThenMetadataAndTheVolumeSizeShow() {
    var payload = Payload(64, 8);
    var (volume, result) = Create(payload);
    var entries = new BitLockerFormatDescriptor().List(volume, null);
    Assert.That(entries.Select(static e => e.Name), Is.EqualTo(new[] { "metadata.ini", "volume.img" }));
    Assert.That(entries[1].OriginalSize, Is.EqualTo(result.VolumeSize));
  }

  [Test]
  public void GivenOurVolume_WhenExtracted_ThenMetadataNeedsNoPasswordAndTheVolumeDoes() {
    var payload = Payload(64, 9);
    var (volume, result) = Create(payload);
    var descriptor = new BitLockerFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "cwb-bitlocker-" + Guid.NewGuid().ToString("N"));
    try {
      descriptor.Extract(volume, dir, null, ["metadata.ini"]);
      var ini = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(ini, Does.Contain($"guid={result.VolumeGuid}").And.Contain("cipher=aes-xts-plain64").And.Contain("protection=RecoveryPassword"));
      var missing = Assert.Throws<ArchivePasswordException>(() => descriptor.Extract(volume, dir, null, null));
      Assert.That(missing!.PasswordWasSupplied, Is.False);
      descriptor.Extract(volume, dir, Password, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "volume.img")), Is.EqualTo(Expected(payload, result.VolumeSize)));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void GivenTheDescriptorWithARecoveryPassword_WhenCreating_ThenItHonoursTheMethodAndBothProtectors() {
    var payload = Payload(64, 10);
    var descriptor = new BitLockerFormatDescriptor();
    using var ms = new MemoryStream();
    descriptor.Create(ms, [ArchiveInputInfo.InMemory("volume.img", payload)], new FormatCreateOptions {
      Password = Password,
      FormatSpecific = new Dictionary<string, string> { ["RecoveryPassword"] = Recovery, ["Method"] = "aes-xts-256", ["Description"] = "cwb test" },
    });
    var v = BitLockerVolume.Read(ms);
    Assert.That((v.CipherModeName, v.KeyBits, v.Description), Is.EqualTo(("xts-plain64", 512, "cwb test")));
    Assert.That(Decrypt(ms, new(null, Recovery)).AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
    Assert.That(Decrypt(ms, new(Password)).AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
  }

  [Test]
  public void GivenAnImageSmallerThanTheRelocatedHeader_WhenCreatedThroughTheDescriptor_ThenItIsZeroPaddedAndReadsBack() {
    var payload = Payload(1, 12);
    using var ms = new MemoryStream();
    new BitLockerFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("volume.img", payload)], new FormatCreateOptions { Password = Password });
    var plain = Decrypt(ms, new(Password));
    Assert.That(plain.AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
    Assert.That(plain.AsSpan(payload.Length, BitLockerWriter.VolumeHeaderBytes).IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
  }

  [Test]
  public void GivenOurVolume_WhenDetected_ThenItIsBitLocker() {
    var (volume, _) = Create(Payload(32, 11));
    Assert.That(FormatDetector.DetectByMagic(volume.ToArray().AsSpan(0, 4096)).ToString(), Is.EqualTo("BitLocker"));
  }

  /// <summary>To Go volumes keep a FAT32 boot sector: only the BitLocker GUID at 424 tells them from plain FAT.</summary>
  [Test]
  public void GivenAFat32BootSector_WhenItCarriesTheBitLockerGuid_ThenOnlyThenIsItToGo() {
    var sector = new byte[512];
    sector[0] = 0xEB; sector[1] = 0x58; sector[2] = 0x90;
    "MSWIN4.1"u8.CopyTo(sector.AsSpan(3));
    sector[510] = 0x55; sector[511] = 0xAA;
    Assert.That(BitLockerVolume.IsBitLocker(sector), Is.False);
    BitLockerVolume.NormalGuid.CopyTo(sector, 424);
    Assert.That(BitLockerVolume.IsBitLocker(sector), Is.True);
    Assert.That(FormatDetector.DetectByMagic(sector).ToString(), Is.EqualTo("BitLocker"));
  }

  [Test]
  public void GivenOurVolumeHoldingFat_WhenProbedAndOpenedWithThePassword_ThenTheFileReadsBack() {
    var (volume, _) = Create(LuksMountTests.FatImage("HELLO.TXT", Content));
    var probe = MountNamespaceResolver.Probe("BitLocker", volume, Password);
    Assert.That(probe.Layers, Does.Contain("container:BitLocker").And.Contain("filesystem:Fat"));
    using var session = MountNamespaceResolver.Open("BitLocker", volume, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true), null,
      new Dictionary<string, string> { ["RecoveryPassword"] = Recovery });
    Assert.That(session.Profile.FormatId, Is.EqualTo("Fat").IgnoreCase);
    Assert.That(ReadAll(session, session.Lookup(session.RootNodeId, "HELLO.TXT")!.Value), Is.EqualTo(Content));
  }

  [Test]
  public void GivenNoOrWrongCredentials_WhenProbed_ThenThePasswordIsAskedFor() {
    var (volume, _) = Create(LuksMountTests.FatImage("HELLO.TXT", Content));
    Assert.That(Assert.Throws<ArchivePasswordException>(() => MountNamespaceResolver.Probe("BitLocker", volume))!.PasswordWasSupplied, Is.False);
    Assert.That(Assert.Throws<ArchivePasswordException>(() =>
      MountNamespaceResolver.Open("BitLocker", volume, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true), "not it"))!.PasswordWasSupplied, Is.True);
  }

  [Test]
  public void GivenAWritableMount_WhenAFileIsWritten_ThenTheDecryptedVolumeHoldsItAndTheMetadataIsUntouched() {
    var (volume, result) = Create(LuksMountTests.FatImage("HELLO.TXT", Content));
    var before = volume.ToArray();
    var added = "written through the BitLocker mount"u8.ToArray();
    using (var session = MountNamespaceResolver.Open("BitLocker", volume, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true), Password)) {
      Assert.That(session.Profile.CanMountWritable, Is.True);
      var node = session.CreateFile(session.RootNodeId, "ADDED.TXT");
      using (var handle = session.OpenFile(node, FileAccess.ReadWrite))
        handle.Write(0, added);
      session.Flush();
    }
    var after = volume.ToArray();
    Assert.That(after.AsSpan(0, 8192).SequenceEqual(before.AsSpan(0, 8192)), Is.True, "the -FVE-FS- boot region must not change");
    foreach (var offset in result.MetadataOffsets)
      Assert.That(after.AsSpan((int)offset, 65536).SequenceEqual(before.AsSpan((int)offset, 65536)), Is.True, "FVE metadata must not change");

    var plain = Decrypt(new MemoryStream(after), new(Password));
    using var fat = FormatRegistry.OpenFilesystem("Fat", new MemoryStream(plain), new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: false));
    Assert.That(ReadAll(fat, fat.Lookup(fat.RootNodeId, "ADDED.TXT")!.Value), Is.EqualTo(added));
    Assert.That(ReadAll(fat, fat.Lookup(fat.RootNodeId, "HELLO.TXT")!.Value), Is.EqualTo(Content));
  }

  [Test]
  public void GivenAReadOnlyStream_WhenOpenedWritable_ThenRefused() {
    var bytes = Create(Payload(32, 12)).Volume.ToArray();
    Assert.Throws<NotSupportedException>(() =>
      new BitLockerFormatDescriptor().OpenBlockDevice(new MemoryStream(bytes, writable: false), new(Password), writable: true));
  }

  private static byte[] ReadAll(IFilesystemSession session, FilesystemNodeId node) {
    using var handle = session.OpenFile(node, FileAccess.Read);
    var result = new byte[handle.Length];
    var offset = 0;
    while (offset < result.Length) {
      var n = handle.Read(offset, result.AsSpan(offset));
      if (n == 0) break;
      offset += n;
    }
    return result;
  }
}
