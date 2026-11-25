#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// dar's encryption layer (docs/DAR-ON-DISK.md, section 12) without dar: the elastic buffers,
/// the ESSIV vectors, the header fields, and archives we encrypt read back by our reader.
/// dar itself judges both directions in <see cref="DarEncryptionOracleTests"/>.
/// </summary>
[TestFixture]
public class DarEncryptionTests {
  private const string Password = "pässwörd 1";

  internal static readonly char[] Ciphers = ['a', 'b', 't', 'p', 'c', 's'];

  internal static List<DarWriter.Item> Items() {
    var now = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    var items = DarFixtures.TreeFiles.Select(kv => DarWriter.Item.FromBytes(kv.Key, kv.Value, 0x1A4, now, now)).ToList();
    var noise = new byte[40_000];
    new Random(11).NextBytes(noise);
    items.Add(DarWriter.Item.FromBytes("dir/noise40k.bin", noise, 0x1A4, now, now));
    return items;
  }

  private static byte[] Write(DarWriter.Settings settings) {
    using var ms = new MemoryStream();
    DarWriter.Write(ms, Items(), settings);
    return ms.ToArray();
  }

  private static DarArchive Open(byte[] slice, string? password) {
    var set = DarSliceSet.Open(new MemoryStream(slice));
    return DarArchive.Read(set.Archive, password);
  }

  private static void AssertTree(DarArchive archive) {
    foreach (var item in Items()) {
      var entry = archive.Entries.Single(e => e.Path == item.Path);
      using var expected = item.Open!();
      using var ms = new MemoryStream();
      expected.CopyTo(ms);
      Assert.That(archive.ReadData(entry), Is.EqualTo(ms.ToArray()), item.Path);
    }
  }

  // Fast PBKDF2 settings keep the round trips quick; argon2 has its own case.
  private static DarWriter.Encryption Fast(char cipher) => new(cipher, Password, '1', 1000);

  // ── elastic buffers ──────────────────────────────────────────────────────

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4)]
  [TestCase(253)]
  [TestCase(254)]
  [TestCase(255)]
  [TestCase(10240)]
  [TestCase(51200)]
  [TestCase(64_515)] // 254² - 1: the largest two-digit size
  [TestCase(64_516)] // 254²: the smallest three-digit size
  [Category("Boundary")]
  public void GivenElasticBufferOfAnySize_WhenReadBackwardsAfterData_ThenItsSizeComesBack(int size) {
    var data = new byte[100 + size];
    new Random(size).NextBytes(data.AsSpan(0, 100));
    DarCrypto.WriteElastic(data.AsSpan(100));
    Assert.That(DarCrypto.ElasticSizeAtEnd(data), Is.EqualTo(size));
  }

  [Test, Category("EdgeCase")]
  public void GivenElasticBuffer_WhenWritten_ThenFillerAvoidsTheMarks() {
    var buffer = DarCrypto.Elastic(5000);
    Assert.That(buffer.Count(b => b == 0xFF), Is.EqualTo(1), "one start mark");
    Assert.That(buffer.Count(b => b == 0xFE), Is.EqualTo(1), "one end mark");
    Assert.That(buffer, Has.None.EqualTo((byte)'X'));
  }

  [TestCase(new byte[] { 1, 2, 3 })]
  [TestCase(new byte[] { 0xFE, 1 })]                  // an end mark not at the end: no start mark before it
  [TestCase(new byte[] { 0xFF, 2, 0xFE })]            // a size below 3 but with digits
  [TestCase(new byte[] { 0xFF, 1, 1, 1, 1, 1, 0xFE })] // five digits
  [TestCase(new byte[] { (byte)'X', 7 })]             // a single mark that does not end the data
  [Category("Exception")]
  public void GivenNoCoherentElasticBuffer_WhenRead_ThenRefused(byte[] data)
    => Assert.That(() => DarCrypto.ElasticSizeAtEnd(data), Throws.InstanceOf<InvalidDataException>());

  // ── ESSIV ────────────────────────────────────────────────────────────────

  // libdar's own self-test: the Blowfish ESSIV key of the 4-byte key 00 00 00 00 (SHA-1 of it),
  // and the vector it gives for each sector number.
  [TestCase(0u, "79bf812226e4136f")]
  [TestCase(7u, "6103d1208a0d222d")]
  [TestCase(8u, "c961ce292e6528be")]
  [TestCase(0x10000007u, "37e9c092c355fb4b")]
  [TestCase(0xa5a55a5au, "087f1aa9ec4ac0c5")]
  [TestCase(0xffffffffu, "7a8f9cd0cbcc56ec")]
  [Category("ThemVsUs")]
  public void GivenSectorNumber_WhenTheBlowfishInitialisationVectorIsMade_ThenItIsDarsSelfTestVector(uint sector, string expected) {
    var cipher = DarBlockCipher.BlowfishForTest([0, 0, 0, 0]);
    var iv = new byte[8];
    cipher.InitialisationVector(sector, iv);
    Assert.That(Convert.ToHexStringLower(iv), Is.EqualTo(expected));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(10239)]
  [TestCase(10240)]
  [Category("Boundary")]
  public void GivenClearBlock_WhenEncryptedAndDecrypted_ThenItComesBackAndTakesTheNextWholeCipherBlock(int size) {
    var cipher = DarCrypto.OpenCipher('a', new byte[32]);
    var clear = new byte[size];
    new Random(size).NextBytes(clear);
    var encrypted = new byte[cipher.EncryptedSize(size)];
    cipher.Encrypt(5, clear, encrypted);
    Assert.That(encrypted.Length, Is.EqualTo((size / 16 + 1) * 16));
    Assert.That(cipher.Decrypt(5, encrypted), Is.EqualTo(size));
    Assert.That(encrypted.AsSpan(0, size).ToArray(), Is.EqualTo(clear));
  }

  // ── archives ─────────────────────────────────────────────────────────────

  [TestCase('a', false)]
  [TestCase('b', true)]
  [TestCase('t', false)]
  [TestCase('p', true)]
  [TestCase('c', false)]
  [TestCase('s', true)]
  [Category("RoundTrip")]
  public void GivenPassword_WhenWrittenAndRead_ThenEveryFileRoundTrips(char cipher, bool tapeMarks) {
    var bytes = Write(new DarWriter.Settings('z', TapeMarks: tapeMarks) { Encryption = Fast(cipher) });
    var archive = Open(bytes, Password);
    Assert.That(archive.Trailer.Cipher, Is.EqualTo(cipher));
    AssertTree(archive);
    Assert.That(bytes.AsSpan().IndexOf("quick brown fox"u8), Is.EqualTo(-1), "no clear text");
  }

  [Test, Category("RoundTrip")]
  public void GivenDarsDefaultKeyDerivation_WhenWrittenAndRead_ThenArgon2RoundTrips() {
    var bytes = Write(new DarWriter.Settings('n') { Encryption = new DarWriter.Encryption('a', Password) });
    var archive = Open(bytes, Password);
    Assert.That(archive.Trailer.Kdf!.Hash, Is.EqualTo('a'));
    Assert.That(archive.Trailer.Kdf.Iterations, Is.EqualTo(10000UL));
    AssertTree(archive);
  }

  [TestCase('5')]
  [TestCase('m')]
  [Category("RoundTrip")]
  public void GivenPbkdf2Hash_WhenWrittenAndRead_ThenItRoundTrips(char hash) {
    var bytes = Write(new DarWriter.Settings('n') { Encryption = new DarWriter.Encryption('t', Password, hash, 500) });
    AssertTree(Open(bytes, Password));
  }

  [Test, Category("HappyPath")]
  public void GivenBlockCipher_WhenWritten_ThenTheEncryptedZoneIsWholeBlocksAndTheTrailerRecordsItsStart() {
    var bytes = Write(new DarWriter.Settings('n') { Encryption = Fast('a') });
    var set = DarSliceSet.Open(new MemoryStream(bytes));
    var level = set.Archive;
    var header = DarVersionHeader.Parse(ReadAll(level), out var headerLength);
    var trailerAt = (long)DarPrimitivesProbe.Terminator(level, level.Length);
    level.Position = trailerAt;
    var trailer = DarVersionHeader.Parse(ReadAll(level), out _);
    Assert.That(header.Flags, Is.EqualTo(0x0420u), "encrypted, KDF parameters");
    Assert.That(trailer.Flags, Is.EqualTo(0x0428u), "and the initial offset");
    Assert.That(trailer.InitialOffset, Is.EqualTo((ulong)headerLength));
    Assert.That((trailerAt - headerLength) % 10256, Is.Zero, "whole 10 240-byte AES blocks");
    Assert.That(trailer.Kdf!.Salt, Has.Length.EqualTo(32));
  }

  private static byte[] ReadAll(Stream s) {
    var buffer = new byte[s.Length - s.Position];
    s.ReadExactly(buffer);
    return buffer;
  }

  [Test, Category("HappyPath")]
  public void GivenSlicedEncryptedArchive_WhenTheLastSliceIsOpened_ThenTheWholeSetDecrypts() {
    var dir = DarFixtures.TempDirectory("encsliced");
    try {
      var first = Path.Combine(dir, "set.1.dar");
      using (var fs = File.Create(first))
        DarWriter.Write(n => n == 1 ? fs : File.Create(Path.Combine(dir, $"set.{n}.dar")), Items(),
          new DarWriter.Settings('n', TapeMarks: true, SliceSize: 9000) { Encryption = Fast('p') });
      var slices = Enumerable.Range(1, 100).Select(n => Path.Combine(dir, $"set.{n}.dar")).TakeWhile(File.Exists).ToArray();
      Assert.That(slices, Has.Length.GreaterThan(3));
      using var last = File.OpenRead(slices[^1]);
      var names = new DarFormatDescriptor().List(last, Password).Select(e => e.Name).ToList();
      Assert.That(names, Does.Contain("dir/noise40k.bin"));
      var outDir = Path.Combine(dir, "out");
      last.Position = 0;
      new DarFormatDescriptor().Extract(last, outDir, Password, null);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "dir", "deeper", "file.txt")), Is.EqualTo("deep\n"u8.ToArray()));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [TestCase('a')]
  [TestCase('b')]
  [TestCase('s')]
  [Category("Exception")]
  public void GivenEncryptedArchive_WhenReadWithAWrongPassword_ThenThePasswordIsRefused(char cipher) {
    var bytes = Write(new DarWriter.Settings('n') { Encryption = Fast(cipher) });
    Assert.That(() => Open(bytes, "pässwörd 2"),
      Throws.InstanceOf<ArchivePasswordException>().With.Property(nameof(ArchivePasswordException.PasswordWasSupplied)).True);
  }

  [TestCase('a', 1)]
  [TestCase('a', 15)]
  [TestCase('a', 16)]
  [TestCase('a', 1000)]
  [TestCase('a', 10239)]
  [TestCase('a', 10241)]
  [TestCase('a', 65536)]
  [TestCase('b', 8)]
  [TestCase('b', 4001)]
  [TestCase('t', 333)]
  [Category("HappyPath")]
  public void GivenArchiveWithAnotherCryptoBlockSize_WhenReadWithoutBeingTold_ThenTheSizeIsInferred(char cipher, int blockSize) {
    var bytes = Write(new DarWriter.Settings('z') { Encryption = Fast(cipher) with { ClearBlockSize = blockSize } });
    var archive = Open(bytes, Password);
    Assert.That(archive.ClearBlockSize, Is.EqualTo(blockSize));
    AssertTree(archive);
  }

  [TestCase(1000)]
  [TestCase(10241)]
  [Category("Exception")]
  public void GivenArchiveWithAnotherCryptoBlockSize_WhenReadWithAWrongPassword_ThenThePasswordIsStillRefused(int blockSize) {
    var bytes = Write(new DarWriter.Settings('n') { Encryption = Fast('a') with { ClearBlockSize = blockSize } });
    Assert.That(() => Open(bytes, "not it"),
      Throws.InstanceOf<ArchivePasswordException>().With.Property(nameof(ArchivePasswordException.PasswordWasSupplied)).True);
  }

  [Test, Category("HappyPath")]
  public void GivenArchiveWithAnotherCryptoBlockSize_WhenEdited_ThenTheEditKeepsTheSize() {
    using var ms = new MemoryStream();
    DarWriter.Write(ms, Items(), new DarWriter.Settings('n') { Encryption = Fast('c') with { ClearBlockSize = 2000 } });
    new DarFormatDescriptor().Add(ms, [ArchiveInputInfo.InMemory("added.txt", "added\n"u8.ToArray())], new ArchiveMutationOptions { Password = Password });
    var archive = Open(ms.ToArray(), Password);
    Assert.That(archive.ClearBlockSize, Is.EqualTo(2000));
    AssertTree(archive);
    Assert.That(archive.ReadData(archive.Entries.Single(e => e.Path == "added.txt")), Is.EqualTo("added\n"u8.ToArray()));
  }

  [TestCase(null)]
  [TestCase("")]
  [Category("Exception")]
  public void GivenEncryptedArchive_WhenReadWithoutAPassword_ThenAPasswordIsAskedFor(string? password) {
    var bytes = Write(new DarWriter.Settings('n') { Encryption = Fast('a') });
    Assert.That(() => new DarFormatDescriptor().List(new MemoryStream(bytes), password),
      Throws.InstanceOf<ArchivePasswordException>().With.Property(nameof(ArchivePasswordException.PasswordWasSupplied)).False);
  }

  [Test, Category("Exception")]
  public void GivenEncryptedArchiveWithAFlippedCiphertextByte_WhenRead_ThenItIsRefused() {
    var bytes = Write(new DarWriter.Settings('n') { Encryption = Fast('a') });
    bytes[bytes.Length / 2] ^= 0x10;
    Assert.That(() => {
      var archive = Open(bytes, Password);
      AssertTree(archive);
    }, Throws.Exception);
  }

  private static byte[] Bytes(DarVersionHeader h) {
    using var ms = new MemoryStream();
    h.Write(ms);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void GivenVersionHeaderWithCipherAndKdf_WhenWrittenAndParsed_ThenEveryFieldComesBack() {
    var kdf = new DarKdf([1, 2, 3, 4, 5], 123456, '5');
    var header = new DarVersionHeader(11, 3, 'z', "N/A", 0x0428 | 0x10, 99, 0) { Cipher = 'c', Kdf = kdf };
    var parsed = DarVersionHeader.Parse(Bytes(header), out _);
    Assert.That((parsed.Cipher, parsed.InitialOffset, parsed.Kdf!.Iterations, parsed.Kdf.Hash), Is.EqualTo(('c', 99UL, 123456UL, '5')));
    Assert.That(parsed.Kdf.Salt, Is.EqualTo(kdf.Salt));
  }

  // ── descriptor options ───────────────────────────────────────────────────

  [TestCase(null, 'a')]
  [TestCase("blowfish", 'b')]
  [TestCase("TWOFISH", 't')]
  [TestCase("serpent256", 'p')]
  [TestCase("camellia", 'c')]
  [TestCase("scram", 's')]
  [Category("HappyPath")]
  public void GivenEncryptionMethod_WhenCreatedWithAPassword_ThenThatCipherIsWritten(string? method, char cipher) {
    var options = new FormatCreateOptions { Password = Password, EncryptionMethod = method, FormatSpecific = { [DarFormatDescriptor.KdfHashOption] = "sha1", [DarFormatDescriptor.KdfIterationsOption] = "100" } };
    using var ms = new MemoryStream();
    new DarFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("a.txt", "abc"u8.ToArray())], options);
    var archive = Open(ms.ToArray(), Password);
    Assert.That(archive.Trailer.Cipher, Is.EqualTo(cipher));
    Assert.That(archive.ReadData(archive.Entries.Single()), Is.EqualTo("abc"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void GivenNoPassword_WhenCreated_ThenNothingIsEncrypted()
    => Assert.That(DarFormatDescriptor.EncryptionOf(new FormatCreateOptions { EncryptionMethod = "aes" }), Is.Null);

  [TestCase("des", null)]
  [TestCase(null, "sha256")]
  [Category("Exception")]
  public void GivenUnknownCipherOrHash_WhenCreated_ThenRefused(string? method, string? hash)
    => Assert.That(() => DarFormatDescriptor.EncryptionOf(new FormatCreateOptions {
      Password = "p", EncryptionMethod = method, FormatSpecific = { [DarFormatDescriptor.KdfHashOption] = hash ?? "" } }),
      Throws.InstanceOf<NotSupportedException>());

  [TestCase("0")]
  [TestCase("x")]
  [TestCase("4294967296")]
  [Category("Exception")]
  public void GivenBadIterationCount_WhenCreated_ThenRefused(string count)
    => Assert.That(() => DarFormatDescriptor.EncryptionOf(new FormatCreateOptions {
      Password = "p", FormatSpecific = { [DarFormatDescriptor.KdfIterationsOption] = count } }), Throws.ArgumentException);
}

/// <summary>Reads a terminator through the library's own primitive.</summary>
internal static class DarPrimitivesProbe {
  public static ulong Terminator(Stream s, long end) => DarPrimitives.ReadTerminator(s, end, out _);
}
