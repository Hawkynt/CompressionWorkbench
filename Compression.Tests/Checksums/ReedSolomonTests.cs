using Compression.Core.Checksums;

namespace Compression.Tests.Checksums;

[TestFixture]
public class ReedSolomonTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_SingleErasure() {
    var rs = new ReedSolomon(4, 2);
    byte[][] data = [
      [1, 2, 3, 4],
      [5, 6, 7, 8],
      [9, 10, 11, 12],
      [13, 14, 15, 16]
    ];

    var parity = rs.Encode(data);
    Assert.That(parity, Has.Length.EqualTo(2));

    // Simulate losing data shard 1
    var shards = new byte[]?[6];
    shards[0] = data[0];
    shards[1] = null; // lost
    shards[2] = data[2];
    shards[3] = data[3];
    shards[4] = parity[0];
    shards[5] = parity[1];

    var ok = rs.Reconstruct(shards);
    Assert.That(ok, Is.True);
    Assert.That(shards[1], Is.EqualTo(data[1]));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_TwoErasures() {
    var rs = new ReedSolomon(4, 2);
    byte[][] data = [
      [10, 20, 30, 40],
      [50, 60, 70, 80],
      [90, 100, 110, 120],
      [130, 140, 150, 160]
    ];

    var parity = rs.Encode(data);

    // Simulate losing data shards 0 and 2
    var shards = new byte[]?[6];
    shards[0] = null; // lost
    shards[1] = data[1];
    shards[2] = null; // lost
    shards[3] = data[3];
    shards[4] = parity[0];
    shards[5] = parity[1];

    var ok = rs.Reconstruct(shards);
    Assert.That(ok, Is.True);
    Assert.That(shards[0], Is.EqualTo(data[0]));
    Assert.That(shards[2], Is.EqualTo(data[2]));
  }

  [Category("HappyPath")]
  [Test]
  public void Encode_NoErasures_VerifyParity() {
    var rs = new ReedSolomon(3, 1);
    byte[][] data = [[1, 2], [3, 4], [5, 6]];

    var parity1 = rs.Encode(data);
    var parity2 = rs.Encode(data);

    Assert.That(parity1[0], Is.EqualTo(parity2[0]));
  }

  [Category("EdgeCase")]
  [Test]
  public void TooManyErasures_ReturnsFalse() {
    var rs = new ReedSolomon(4, 1);
    byte[][] data = [[1], [2], [3], [4]];
    var parity = rs.Encode(data);

    // Lose 2 data shards but only have 1 parity
    var shards = new byte[]?[5];
    shards[0] = null;
    shards[1] = null;
    shards[2] = data[2];
    shards[3] = data[3];
    shards[4] = parity[0];

    var ok = rs.Reconstruct(shards);
    Assert.That(ok, Is.False);
  }
}
