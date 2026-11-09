using Compression.Lib;
using Compression.Mounting;
using Compression.Registry;
using FileFormat.Luks;

namespace Compression.Tests.Luks;

/// <summary>
/// Mounting through LUKS: the passphrase unlocks a key slot, the data segment becomes a block
/// device, and the filesystem inside resolves and mounts through it, read-only or read-write.
/// </summary>
[TestFixture]
public sealed class LuksMountTests {
  private const string Passphrase = "mount passphrase";
  private static readonly byte[] Content = Enumerable.Range(0, 5000).Select(static i => (byte)(i * 7 + 3)).ToArray();

  [OneTimeSetUp]
  public void RegisterFormats() => FormatRegistration.EnsureInitialized();

  internal static byte[] FatImage(string name, byte[] content) {
    var creator = (IArchiveCreatable)FormatRegistry.GetArchiveOps("Fat")!;
    using var ms = new MemoryStream();
    creator.Create(ms, [ArchiveInputInfo.InMemory(name, content)], new FormatCreateOptions { ForceCompress = true });
    return ms.ToArray();
  }

  private static MemoryStream Wrap(int version, byte[] payload) {
    var ms = new MemoryStream();
    if (version == 1) LuksVolume.WriteLuks1(ms, payload, Passphrase, 1000);
    else LuksVolume.WriteLuks2(ms, payload, Passphrase, LuksKdf.Pbkdf2(1000), digestIterations: 1000);
    ms.Position = 0;
    return ms;
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

  [TestCase(1)]
  [TestCase(2)]
  public void GivenALuksVolumeHoldingFat_WhenProbedWithThePassphrase_ThenTheFilesystemInsideResolves(int version) {
    using var image = Wrap(version, FatImage("HELLO.TXT", Content));

    var probe = MountNamespaceResolver.Probe("Luks", image, Passphrase);

    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True);
      Assert.That(probe.Layers, Does.Contain("container:Luks"));
      Assert.That(probe.Layers, Does.Contain("filesystem:Fat"));
    });
  }

  [TestCase(1)]
  [TestCase(2)]
  public void GivenALuksVolumeHoldingFat_WhenOpenedWithThePassphrase_ThenTheFileReadsBack(int version) {
    using var image = Wrap(version, FatImage("HELLO.TXT", Content));

    using var session = MountNamespaceResolver.Open("Luks", image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true), Passphrase);

    Assert.That(session.Profile.FormatId, Is.EqualTo("Fat").IgnoreCase);
    var file = session.Lookup(session.RootNodeId, "HELLO.TXT");
    Assert.That(file, Is.Not.Null);
    Assert.That(ReadAll(session, file!.Value), Is.EqualTo(Content));
  }

  [Test]
  public void GivenNoPassphrase_WhenProbed_ThenItAsksForOne() {
    using var image = Wrap(1, FatImage("HELLO.TXT", Content));
    var e = Assert.Throws<ArchivePasswordException>(() => MountNamespaceResolver.Probe("Luks", image));
    Assert.That(e!.PasswordWasSupplied, Is.False);
  }

  [Test]
  public void GivenTheWrongPassphrase_WhenOpened_ThenItSaysTheGivenOneIsWrong() {
    using var image = Wrap(2, FatImage("HELLO.TXT", Content));
    var e = Assert.Throws<ArchivePasswordException>(() =>
      MountNamespaceResolver.Open("Luks", image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true), "not it"));
    Assert.That(e!.PasswordWasSupplied, Is.True);
  }

  [TestCase(1)]
  [TestCase(2)]
  public void GivenAWritableMount_WhenAFileIsWritten_ThenTheDecryptedVolumeHoldsIt(int version) {
    using var image = Wrap(version, FatImage("HELLO.TXT", Content));
    var headerBefore = image.ToArray().AsSpan(0, 4096).ToArray();
    var added = "written through the encrypted mount"u8.ToArray();

    using (var session = MountNamespaceResolver.Open("Luks", image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true), Passphrase)) {
      Assert.That(session.Profile.CanMountWritable, Is.True, "the filesystem inside must be writable for this test");
      var node = session.CreateFile(session.RootNodeId, "ADDED.TXT");
      using (var handle = session.OpenFile(node, FileAccess.ReadWrite))
        handle.Write(0, added);
      session.Flush();
    }

    Assert.That(image.ToArray().AsSpan(0, 4096).SequenceEqual(headerBefore), Is.True, "the LUKS header must not change");
    var plain = LuksTests.Decrypt(image.ToArray(), Passphrase);
    using var fat = FormatRegistry.OpenFilesystem("Fat", new MemoryStream(plain), new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: false));
    Assert.That(ReadAll(fat, fat.Lookup(fat.RootNodeId, "ADDED.TXT")!.Value), Is.EqualTo(added));
    Assert.That(ReadAll(fat, fat.Lookup(fat.RootNodeId, "HELLO.TXT")!.Value), Is.EqualTo(Content));
  }

  [Test]
  public void GivenTheBlockDevice_WhenSectorsAreWrittenAndRead_ThenOnlyThoseCiphertextSectorsChange() {
    var payload = LuksTests.SamplePayload(64, 21);
    using var image = Wrap(2, payload);
    var before = image.ToArray();
    var device = new LuksFormatDescriptor().OpenBlockDevice(image, new(Passphrase), writable: true);
    using (device) {
      Assert.That(device.Geometry.BlockCount, Is.EqualTo(64));
      var read = new byte[3 * 512];
      device.ReadBlocks(10, read);
      Assert.That(read, Is.EqualTo(payload.AsSpan(10 * 512, 3 * 512).ToArray()));
      device.WriteBlocks(20, new byte[512]);
    }
    var after = image.ToArray();
    var dataOffset = 16 << 20;
    var changed = Enumerable.Range(0, 64).Where(s => !after.AsSpan(dataOffset + s * 512, 512).SequenceEqual(before.AsSpan(dataOffset + s * 512, 512))).ToArray();
    Assert.That(changed, Is.EqualTo(new[] { 20 }));
    var plain = LuksTests.Decrypt(after, Passphrase);
    Assert.That(plain.AsSpan(20 * 512, 512).ToArray(), Is.All.EqualTo((byte)0));
  }

  [Test]
  public void GivenAReadOnlyStream_WhenOpenedWritable_ThenRefused() {
    var bytes = Wrap(1, LuksTests.SamplePayload(4, 1)).ToArray();
    Assert.Throws<NotSupportedException>(() =>
      new LuksFormatDescriptor().OpenBlockDevice(new MemoryStream(bytes, writable: false), new(Passphrase), writable: true));
  }
}

/// <summary>The LUKS2 writer, against our own reader.</summary>
[TestFixture]
public sealed class Luks2WriterTests {
  [TestCase("pbkdf2")]
  [TestCase("argon2id")]
  [TestCase("argon2i")]
  public void GivenALuks2Volume_WhenReadBack_ThenTheHeaderAndPayloadMatch(string kdf) {
    var payload = LuksTests.SamplePayload(32, 9);
    using var ms = new MemoryStream();
    var spec = kdf == "pbkdf2" ? LuksKdf.Pbkdf2(1000) : new LuksKdf(kdf, 2, 1024, 2);
    LuksVolume.WriteLuks2(ms, payload, "pw2", spec, label: "cwb", digestIterations: 1000);

    var v = LuksVolume.Read(ms);
    Assert.Multiple(() => {
      Assert.That(v.Version, Is.EqualTo(2));
      Assert.That($"{v.Cipher}-{v.CipherMode}", Is.EqualTo("aes-xts-plain64"));
      Assert.That(v.KeyBytes, Is.EqualTo(64));
      Assert.That(v.Label, Is.EqualTo("cwb"));
      Assert.That(v.PayloadOffset, Is.EqualTo(16 << 20));
      Assert.That(v.KeySlots.Single().Kdf, Is.EqualTo(kdf));
    });
    Assert.That(LuksTests.Decrypt(ms.ToArray(), "pw2"), Is.EqualTo(payload));
  }

  [Test]
  public void GivenALuks2Volume_WhenTheSecondaryHeaderIsInspected_ThenItIsAValidCopy() {
    using var ms = new MemoryStream();
    LuksVolume.WriteLuks2(ms, new byte[512], "pw", LuksKdf.Pbkdf2(1000), digestIterations: 1000);
    var bytes = ms.ToArray();
    var secondary = bytes.AsSpan(16384, 16384).ToArray();
    Assert.That(secondary.AsSpan(0, 6).ToArray(), Is.EqualTo(new byte[] { (byte)'S', (byte)'K', (byte)'U', (byte)'L', 0xBA, 0xBE }));
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(secondary.AsSpan(256)), Is.EqualTo(16384UL));
    var stored = secondary.AsSpan(448, 32).ToArray();
    secondary.AsSpan(448, 64).Clear();
    Assert.That(System.Security.Cryptography.SHA256.HashData(secondary), Is.EqualTo(stored));
  }

  [Test]
  public void GivenTheDescriptor_WhenVersion2IsRequested_ThenALuks2VolumeIsWritten() {
    var payload = LuksTests.SamplePayload(8, 4);
    using var ms = new MemoryStream();
    new LuksFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("volume.img", payload)], new FormatCreateOptions {
      Password = "pw",
      FormatSpecific = new Dictionary<string, string> { ["Version"] = "2", ["Kdf"] = "argon2id", ["Argon2MemoryKiB"] = "1024", ["Argon2Time"] = "1", ["Argon2Lanes"] = "1", ["Iterations"] = "1000" },
    });
    Assert.That(LuksVolume.Read(ms).Version, Is.EqualTo(2));
    Assert.That(LuksTests.Decrypt(ms.ToArray(), "pw"), Is.EqualTo(payload));
  }

  [Test]
  public void GivenTooFewPbkdf2Iterations_WhenWriting_ThenRefused()
    => Assert.Throws<ArgumentOutOfRangeException>(() => LuksVolume.WriteLuks2(new MemoryStream(), new byte[512], "pw", LuksKdf.Pbkdf2(10)));
}
