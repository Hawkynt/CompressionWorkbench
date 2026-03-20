using Compression.Core.Checksums;

namespace Compression.Tests.Checksums;

[TestFixture]
public class Sha256Tests {
  [Category("ThemVsUs")]
  [Test]
  public void Compute_EmptyString() {
    byte[] hash = Sha256.Compute(ReadOnlySpan<byte>.Empty);
    string hex = Convert.ToHexString(hash).ToLowerInvariant();
    Assert.That(hex, Is.EqualTo("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Compute_ABC() {
    byte[] hash = Sha256.Compute("abc"u8);
    string hex = Convert.ToHexString(hash).ToLowerInvariant();
    Assert.That(hex, Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Compute_NIST_OneBlockMessage() {
    // NIST test vector: "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"
    byte[] hash = Sha256.Compute("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"u8);
    string hex = Convert.ToHexString(hash).ToLowerInvariant();
    Assert.That(hex, Is.EqualTo("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Compute_NIST_TwoBlockMessage() {
    // NIST test vector: "abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu"
    byte[] hash = Sha256.Compute(
      "abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu"u8);
    string hex = Convert.ToHexString(hash).ToLowerInvariant();
    Assert.That(hex, Is.EqualTo("cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1"));
  }

  [Category("HappyPath")]
  [Test]
  public void Compute_HashSize() {
    byte[] hash = Sha256.Compute("test"u8);
    Assert.That(hash.Length, Is.EqualTo(32));
    Assert.That(hash.Length, Is.EqualTo(Sha256.HashSize));
  }

  [Category("HappyPath")]
  [Test]
  public void Incremental_MatchesBatch() {
    var data = new byte[1000];
    new Random(42).NextBytes(data);
    byte[] batch = Sha256.Compute(data);

    var sha = new Sha256();
    sha.Update(data.AsSpan(0, 100));
    sha.Update(data.AsSpan(100, 900));
    sha.Finish();

    Assert.That(sha.Hash, Is.EqualTo(batch));
  }

  [Category("HappyPath")]
  [Test]
  public void Incremental_SingleByteUpdates() {
    var data = new byte[128];
    new Random(99).NextBytes(data);
    byte[] batch = Sha256.Compute(data);

    var sha = new Sha256();
    foreach (byte b in data)
      sha.Update([b]);
    sha.Finish();

    Assert.That(sha.Hash, Is.EqualTo(batch));
  }

  [Category("EdgeCase")]
  [Test]
  public void Incremental_EmptyFinish() {
    var sha = new Sha256();
    sha.Finish();
    byte[] batch = Sha256.Compute(ReadOnlySpan<byte>.Empty);
    Assert.That(sha.Hash, Is.EqualTo(batch));
  }

  [Category("ThemVsUs")]
  [Test]
  public void LargeData_MatchesSystemSecurity() {
    var data = new byte[10000];
    new Random(123).NextBytes(data);

    byte[] ours = Sha256.Compute(data);
    byte[] theirs = System.Security.Cryptography.SHA256.HashData(data);

    Assert.That(ours, Is.EqualTo(theirs));
  }

  [Category("HappyPath")]
  [Test]
  public void Reset_AllowsReuse() {
    var sha = new Sha256();
    sha.Update("abc"u8);
    sha.Finish();
    byte[] first = sha.Hash;

    sha.Reset();
    sha.Update("abc"u8);
    sha.Finish();
    byte[] second = sha.Hash;

    Assert.That(second, Is.EqualTo(first));
  }

  [Category("EdgeCase")]
  [Test]
  public void Finish_CalledTwice_ReturnsSameHash() {
    var sha = new Sha256();
    sha.Update("test"u8);
    sha.Finish();
    byte[] first = sha.Hash;

    sha.Finish();
    byte[] second = sha.Hash;

    Assert.That(second, Is.EqualTo(first));
  }

  [Category("Exception")]
  [Test]
  public void Update_AfterFinish_ThrowsInvalidOperation() {
    var sha = new Sha256();
    sha.Update("test"u8);
    sha.Finish();

    Assert.That(() => sha.Update("more"u8), Throws.TypeOf<InvalidOperationException>());
  }

  [Category("Boundary")]
  [Test]
  public void Compute_ExactlyOneBlock() {
    // 55 bytes of data + 1 byte padding + 8 bytes length = 64 bytes = 1 block
    var data = new byte[55];
    new Random(77).NextBytes(data);
    byte[] ours = Sha256.Compute(data);
    byte[] theirs = System.Security.Cryptography.SHA256.HashData(data);
    Assert.That(ours, Is.EqualTo(theirs));
  }

  [Category("Boundary")]
  [Test]
  public void Compute_ExactBlockBoundary() {
    // Exactly 64 bytes: needs padding in a second block
    var data = new byte[64];
    new Random(88).NextBytes(data);
    byte[] ours = Sha256.Compute(data);
    byte[] theirs = System.Security.Cryptography.SHA256.HashData(data);
    Assert.That(ours, Is.EqualTo(theirs));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Compute_MultipleBlocks() {
    var data = new byte[256];
    new Random(44).NextBytes(data);
    byte[] ours = Sha256.Compute(data);
    byte[] theirs = System.Security.Cryptography.SHA256.HashData(data);
    Assert.That(ours, Is.EqualTo(theirs));
  }
}
