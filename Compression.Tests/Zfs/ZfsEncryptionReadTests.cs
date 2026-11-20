using System.Text;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Reading natively encrypted datasets with their keys: cwbcrypt (OpenZFS 2.2.2: AES-256-GCM with
/// a passphrase and a child sharing its key, AES-128-CCM with a raw key, AES-192-GCM with a hex
/// key, a snapshot) reads byte for byte as the kernel served it; cwbenc's passphrase dataset too.
/// Every block's MAC is checked; wrong or missing keys refuse the dataset.
/// </summary>
[TestFixture]
public sealed class ZfsEncryptionReadTests {
  private static readonly Dictionary<string, ZfsKeyMaterial> CryptKeys = new(StringComparer.Ordinal) {
    ["cwbcrypt/gcm256"] = ZfsKeyMaterial.FromText("gcm-256-passphrase"),
    ["cwbcrypt/ccm128"] = ZfsKeyMaterial.FromBytes(Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef")),
    ["cwbcrypt/gcm192"] = ZfsKeyMaterial.FromText("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"),
  };

  [Test]
  public void Given_EverySuiteAndKeyFormat_When_ReadWithTheKeys_Then_EveryFileMatchesTheKernel() {
    using var reader = new ZfsReader(ZfsReferencePools.Open("cwbcrypt"), null, CryptKeys);
    Assert.That(reader.Warnings, Is.Empty);
    Assert.That(reader.Datasets.Where(d => d.Refusal != null), Is.Empty);
    ZfsReferencePools.AssertMatchesManifest(reader, "cwbcrypt");
  }

  [Test]
  public void Given_ThePassphraseDataset_When_ReadWithItsPassphrase_Then_ItsFileReads() {
    using var reader = new ZfsReader(ZfsReferencePools.Open("cwbenc"), ZfsKeyMaterial.FromText("cwb-passphrase"));
    Assert.That(reader.Warnings, Is.Empty);
    var secret = reader.Entries.Single(e => e.Name == "secret/secret.txt");
    Assert.That(Encoding.ASCII.GetString(reader.Extract(secret)), Is.EqualTo("secret contents\n"));
  }

  [Test]
  public void Given_AWrongPassphrase_When_Read_Then_TheDatasetIsRefusedAndTheRestReads() {
    using var reader = new ZfsReader(ZfsReferencePools.Open("cwbenc"), ZfsKeyMaterial.FromText("not the passphrase"));
    var secret = reader.Datasets.Single(d => d.Name == "cwbenc/secret");
    Assert.That(secret.Refusal, Does.Contain("wrong"));
    Assert.That(reader.Entries.Select(e => e.Name), Does.Contain("plain.txt").And.Not.Contain("secret/secret.txt"));
  }

  [Test]
  public void Given_KeysForSomeRoots_When_Read_Then_OnlyThoseRootsAndTheirChildrenOpen() {
    using var reader = new ZfsReader(ZfsReferencePools.Open("cwbcrypt"), ZfsKeyMaterial.FromText("gcm-256-passphrase"));
    var refused = reader.Datasets.Where(d => d.Refusal != null).Select(d => d.Name).ToList();
    Assert.That(refused, Is.EquivalentTo(new[] { "cwbcrypt/ccm128", "cwbcrypt/gcm192" }));
    Assert.That(reader.Entries.Select(e => e.Name), Does.Contain("gcm256/child/text.txt").And.Contain("gcm256/.zfs/snapshot/s1/rand.bin"));
  }

  [Test]
  public void Given_AHexKeyOfTheWrongLength_When_Read_Then_TheDatasetIsRefused() {
    var keys = new Dictionary<string, ZfsKeyMaterial>(CryptKeys) { ["cwbcrypt/gcm192"] = ZfsKeyMaterial.FromText("0011") };
    using var reader = new ZfsReader(ZfsReferencePools.Open("cwbcrypt"), null, keys);
    Assert.That(reader.Datasets.Single(d => d.Name == "cwbcrypt/gcm192").Refusal, Does.Contain("hex"));
  }

  [Test]
  public void Given_AnEncryptedBlock_When_ItsMacIsAltered_Then_DecryptionFails() {
    using var reader = new ZfsReader(ZfsReferencePools.Open("cwbcrypt"), null, CryptKeys);
    var entry = reader.Entries.Single(e => e.Name == "ccm128/large.bin");
    var dnode = entry.Dnode!;
    var bp = dnode.BlockPointers.First(b => !b.IsHole);
    var levels = dnode.Levels;
    // walk down to a level-0 block
    var os = entry.ObjectSet!;
    while (bp.Level > 0) bp = ZfsBlockPointer.Read(os.ReadBlock(bp).AsSpan(0, ZfsConstants.BlockPointerSize));
    Assert.That(levels, Is.GreaterThan(0));
    var cipher = os.Pool.ReadPhysical(bp);
    var (salt, iv) = ZfsCryptoBlocks.Parameters(bp.Raw);
    var mac = ZfsCryptoBlocks.Mac(bp.Raw);
    var key = os.Key!;
    var plain = new byte[cipher.Length];
    Assert.DoesNotThrow(() => ZfsDatasetKey.Decrypt(key.Crypt, key.BlockKey(salt), iv, cipher, mac, plain, []));
    mac[0] ^= 1;
    Assert.Throws<System.Security.Cryptography.AuthenticationTagMismatchException>(() => ZfsDatasetKey.Decrypt(key.Crypt, key.BlockKey(salt), iv, cipher, mac, plain, []));
  }
}
