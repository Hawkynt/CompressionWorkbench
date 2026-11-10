using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.BitLocker;

namespace Compression.Tests.BitLocker;

/// <summary>
/// cryptsetup's BITLK test images (<c>tests/bitlk-images.tar.xz</c>, made by Windows Vista to 11)
/// as the oracle: <c>images.conf</c> gives each image's passwords, VMK GUIDs, cipher and the
/// SHA-256 of cryptsetup's whole device-mapper view, which our decrypted device must reproduce
/// byte for byte with every credential that opens it. Set <c>CWB_BITLK_IMAGES</c> to the
/// extracted <c>bitlk-images</c> directory; skipped without it unless
/// <c>CWB_REQUIRE_BITLK_IMAGES=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class BitlkImageConformanceTests {
  private static string? Directory => Environment.GetEnvironmentVariable("CWB_BITLK_IMAGES") is { Length: > 0 } d && System.IO.Directory.Exists(d) ? d : null;

  private static string RequireImages() {
    if (Directory is { } d) return d;
    const string hint = "CWB_BITLK_IMAGES is not set to the extracted cryptsetup tests/bitlk-images directory.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_BITLK_IMAGES") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
    return "";
  }

  /// <summary>images.conf: <c>[name]</c> sections of <c>KEY=value</c> lines.</summary>
  internal static Dictionary<string, Dictionary<string, string>> Config(string dir) {
    var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
    Dictionary<string, string>? current = null;
    foreach (var line in File.ReadAllLines(Path.Combine(dir, "images.conf"))) {
      if (line.StartsWith('[')) sections[line.Trim().Trim('[', ']')] = current = new(StringComparer.Ordinal);
      else if (current is not null && line.IndexOf('=') is > 0 and var at) current[line[..at]] = line[(at + 1)..];
    }
    return sections;
  }

  private static string Get(Dictionary<string, string> vars, string key) => vars.TryGetValue(key, out var v) ? v : "";

  /// <summary>Every (image, credential) pair images.conf supplies, as cryptsetup's bitlk-compat-test tries them.</summary>
  public static IEnumerable<TestCaseData> Cases() {
    if (Directory is not { } dir || !File.Exists(Path.Combine(dir, "images.conf"))) {
      yield return new TestCaseData("(images not available)", "none").SetName("BitlkImages_NotAvailable");
      yield break;
    }
    foreach (var (name, vars) in Config(dir).OrderBy(static s => s.Key, StringComparer.Ordinal)) {
      foreach (var kind in new[] { "PW", "RP", "RP2" })
        if (Get(vars, kind).Length > 0)
          yield return new TestCaseData(name, kind).SetName($"BitlkImage_{name.Replace('-', '_')}_{kind}");
      if (Get(vars, "SK_VMK_GUID").Length > 0)
        yield return new TestCaseData(name, "BEK").SetName($"BitlkImage_{name.Replace('-', '_')}_BEK");
      if (Get(vars, "CLEARKEY_VMK_GUID").Length > 0)
        yield return new TestCaseData(name, "CLEARKEY").SetName($"BitlkImage_{name.Replace('-', '_')}_CLEARKEY");
    }
  }

  public static IEnumerable<TestCaseData> ImageNames() {
    if (Directory is not { } dir || !File.Exists(Path.Combine(dir, "images.conf"))) {
      yield return new TestCaseData("(images not available)").SetName("BitlkImageMetadata_NotAvailable");
      yield break;
    }
    foreach (var name in Config(dir).Keys.Order(StringComparer.Ordinal))
      yield return new TestCaseData(name).SetName($"BitlkImageMetadata_{name.Replace('-', '_')}");
  }

  private static BitLockerCredentials Credentials(string dir, Dictionary<string, string> vars, string kind) => kind switch {
    "PW" => new(Get(vars, "PW")),
    "RP" => new(null, Get(vars, "RP")),
    "RP2" => new(null, Get(vars, "RP2")),
    "BEK" => new(null, StartupKey: File.ReadAllBytes(Path.Combine(dir, Get(vars, "SK_VMK_GUID").ToUpperInvariant() + ".BEK"))),
    _ => new(null),
  };

  internal static string Sha256(IRandomAccessBlockDevice device) {
    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var size = device.Geometry.LogicalBlockSize;
    var buffer = new byte[(1 << 20) / size * size];
    for (long block = 0; block < device.Geometry.BlockCount;) {
      var n = (int)Math.Min(buffer.Length / size, device.Geometry.BlockCount - block);
      device.ReadBlocks(block, buffer.AsSpan(0, n * size));
      sha.AppendData(buffer, 0, n * size);
      block += n;
    }
    return Convert.ToHexStringLower(sha.GetHashAndReset());
  }

  [TestCaseSource(nameof(Cases))]
  public void GivenABitlkImage_WhenOpenedWithEachCredential_ThenTheDecryptedDeviceIsCryptsetupsView(string name, string kind) {
    var dir = RequireImages();
    var vars = Config(dir)[name];
    using var stream = File.OpenRead(Path.Combine(dir, name + ".img"));
    var volume = BitLockerVolume.Read(stream);
    var fvek = volume.TryUnlock(Credentials(dir, vars, kind), out var used);
    Assert.That(fvek, Is.Not.Null, $"{kind} opens no protector");
    TestContext.Out.WriteLine($"{name}: aes-{volume.CipherModeName} {volume.KeyBits} bits, sector {volume.SectorSize}, opened via {used!.Protection} {used.Id}");

    var expected = Get(vars, "SHA256SUM");
    if (expected.Length == 0) {
      // cryptsetup refuses to activate these (BITLK_activate_by_volume_key's _activate_check):
      // both are encrypt-on-write volumes, still converting, so there is no device-mapper view
      // to compare with and images.conf records no checksum. We refuse them the same way.
      Assert.Throws<NotSupportedException>(() => new BitLockerBlockDevice(stream, volume, fvek!, writable: false));
      Assert.Ignore($"{name}: cryptsetup does not activate {volume.EncryptionType} volumes, so it has no plaintext view to match; the key opens and activation is refused exactly as cryptsetup refuses it.");
    }
    using var device = new BitLockerBlockDevice(stream, volume, fvek!, writable: false);
    Assert.That(Sha256(device), Is.EqualTo(expected));
    var boot = new byte[device.Geometry.LogicalBlockSize];
    device.ReadBlocks(0, boot);
    Assert.That(FilesystemUuid(boot), Is.EqualTo(Get(vars, "UUID")), "blkid's UUID of the filesystem inside");
  }

  /// <summary>The UUID blkid reports: an NTFS volume serial (8 bytes at 0x48), or a FAT serial (0x43 on FAT32, 0x27 on FAT12/16) as XXXX-XXXX.</summary>
  internal static string FilesystemUuid(ReadOnlySpan<byte> boot) {
    if (boot.Slice(3, 4).SequenceEqual("NTFS"u8))
      return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(boot[0x48..]).ToString("X16");
    var serial = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(boot[(boot.Slice(0x52, 5).SequenceEqual("FAT32"u8) ? 0x43 : 0x27)..]);
    return $"{serial >> 16:X4}-{serial & 0xFFFF:X4}";
  }

  /// <summary>What cryptsetup's check_dump verifies: GUID, cipher, description and the VMK GUIDs per protection.</summary>
  [TestCaseSource(nameof(ImageNames))]
  public void GivenABitlkImage_WhenItsMetadataIsRead_ThenItMatchesImagesConf(string name) {
    var dir = RequireImages();
    var vars = Config(dir)[name];
    using var stream = File.OpenRead(Path.Combine(dir, name + ".img"));
    var v = BitLockerVolume.Read(stream);
    Guid? Protector(BitLockerProtection p, int nth = 0) => v.Protectors.Where(x => x.Protection == p).Select(static x => (Guid?)x.Id).ElementAtOrDefault(nth);
    Assert.Multiple(() => {
      Assert.That(v.VolumeGuid.ToString(), Is.EqualTo(Get(vars, "GUID")));
      Assert.That($"aes-{v.CipherModeName}", Is.EqualTo(Get(vars, "CIPHER")));
      Assert.That(v.VolumeSize, Is.AnyOf(104857600UL, 134217728UL, 105906176UL));
      Assert.That(v.Description, Does.StartWith("DESKTOP").Or.StartWith("WIN"));
      Assert.That(v.IsToGo, Is.EqualTo(name.Contains("togo", StringComparison.Ordinal)));
      if (Get(vars, "PW_VMK_GUID").Length > 0) Assert.That(Protector(BitLockerProtection.Password)?.ToString(), Is.EqualTo(Get(vars, "PW_VMK_GUID")));
      if (Get(vars, "RP_VMK_GUID").Length > 0) Assert.That(Protector(BitLockerProtection.RecoveryPassword)?.ToString(), Is.EqualTo(Get(vars, "RP_VMK_GUID")));
      if (Get(vars, "RP2_VMK_GUID").Length > 0) Assert.That(Protector(BitLockerProtection.RecoveryPassword, 1)?.ToString(), Is.EqualTo(Get(vars, "RP2_VMK_GUID")));
      if (Get(vars, "SK_VMK_GUID").Length > 0) Assert.That(Protector(BitLockerProtection.StartupKey)?.ToString(), Is.EqualTo(Get(vars, "SK_VMK_GUID")));
      if (Get(vars, "SC_VMK_GUID").Length > 0) Assert.That(Protector(BitLockerProtection.SmartCard)?.ToString(), Is.EqualTo(Get(vars, "SC_VMK_GUID")));
      if (Get(vars, "CLEARKEY_VMK_GUID").Length > 0) Assert.That(Protector(BitLockerProtection.ClearKey)?.ToString(), Is.EqualTo(Get(vars, "CLEARKEY_VMK_GUID")));
    });
  }

  /// <summary>
  /// Writing through each sector transform on a copy of a real volume: what we encrypt, the
  /// transform proven above decrypts back, the written sectors alone change on disk, and a
  /// write into the metadata area is swallowed as dm-zero swallows it.
  /// </summary>
  [TestCase("bitlk-aes-xts-128")]
  [TestCase("bitlk-aes-xts-128-4k")]
  [TestCase("bitlk-aes-cbc-256")]
  [TestCase("bitlk-aes-cbc-128-4k")]
  [TestCase("bitlk-aes-cbc-elephant-128")]
  [TestCase("bitlk-aes-cbc-elephant-256")]
  public void GivenACopyOfABitlkImage_WhenSectorsAreWritten_ThenTheyReadBackAndOnlyTheirCiphertextChanges(string name) {
    var dir = RequireImages();
    var vars = Config(dir)[name];
    var copy = Path.Combine(Path.GetTempPath(), $"cwb-bitlk-{Guid.NewGuid():N}.img");
    File.Copy(Path.Combine(dir, name + ".img"), copy);
    try {
      var before = File.ReadAllBytes(copy);
      long metadataBlock;
      var written = new byte[3 * 4096];
      new Random(7).NextBytes(written);
      using (var stream = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite)) {
        var v = BitLockerVolume.Read(stream);
        var fvek = BitLockerFormatDescriptor.Unlock(v, new(Get(vars, "PW")));
        using var device = new BitLockerBlockDevice(stream, v, fvek, writable: true);
        var size = device.Geometry.LogicalBlockSize;
        device.WriteBlocks(1000, written.AsSpan(0, written.Length / size * size));
        device.WriteBlocks(0, written.AsSpan(0, size));   // sector 0 lives in the relocated header
        metadataBlock = v.MetadataOffsets[1] / size;
        device.WriteBlocks(metadataBlock, written.AsSpan(0, size));
      }
      using (var stream = File.OpenRead(copy)) {
        var v = BitLockerVolume.Read(stream);
        Assert.That(v.ValidCopy, Is.Zero);
        using var device = new BitLockerBlockDevice(stream, v, BitLockerFormatDescriptor.Unlock(v, new(Get(vars, "PW"))), writable: false);
        var size = device.Geometry.LogicalBlockSize;
        var back = new byte[written.Length / size * size];
        device.ReadBlocks(1000, back);
        Assert.That(back, Is.EqualTo(written.AsSpan(0, back.Length).ToArray()));
        var first = new byte[size];
        device.ReadBlocks(0, first);
        Assert.That(first, Is.EqualTo(written.AsSpan(0, size).ToArray()));
        device.ReadBlocks(metadataBlock, first);
        Assert.That(first, Is.All.EqualTo((byte)0), "the metadata area must keep reading as zeros");

        var after = File.ReadAllBytes(copy);
        var changed = Enumerable.Range(0, after.Length / 512).Where(s => !after.AsSpan(s * 512, 512).SequenceEqual(before.AsSpan(s * 512, 512))).ToArray();
        var expected = Enumerable.Range((int)(1000L * size / 512), back.Length / 512)
          .Concat(Enumerable.Range((int)(v.VolumeHeaderOffset / 512), size / 512)).Order().ToArray();
        Assert.That(changed, Is.EqualTo(expected));
      }
    } finally {
      File.Delete(copy);
    }
  }
}
