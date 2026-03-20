using Compression.Core.Transforms;

namespace Compression.Tests.Transforms;

[TestFixture]
public class Bcj2Tests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SimpleData_NoInstructions() {
    byte[] data = "Hello, BCJ2 World! No x86 instructions here."u8.ToArray();
    var (main, call, jump, range) = Bcj2Filter.Encode(data);
    byte[] decoded = Bcj2Filter.Decode(main, call, jump, range, data.Length);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_WithCallInstructions() {
    byte[] data = new byte[100];
    Array.Fill(data, (byte)0x90);

    // Place a CALL at position 10 targeting position 50
    data[10] = 0xE8;
    int relTarget = 50 - (10 + 5);
    data[11] = (byte)relTarget;
    data[12] = (byte)(relTarget >> 8);
    data[13] = (byte)(relTarget >> 16);
    data[14] = (byte)(relTarget >> 24);

    var (main, call, jump, range) = Bcj2Filter.Encode(data);
    byte[] decoded = Bcj2Filter.Decode(main, call, jump, range, data.Length);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_WithJumpInstructions() {
    byte[] data = new byte[80];
    Array.Fill(data, (byte)0xCC);

    data[5] = 0xE9;
    int relTarget = 30 - (5 + 5);
    data[6] = (byte)relTarget;
    data[7] = (byte)(relTarget >> 8);
    data[8] = (byte)(relTarget >> 16);
    data[9] = (byte)(relTarget >> 24);

    var (main, call, jump, range) = Bcj2Filter.Encode(data);
    byte[] decoded = Bcj2Filter.Decode(main, call, jump, range, data.Length);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MixedCallsAndJumps() {
    byte[] data = new byte[200];
    var rng = new Random(42);
    rng.NextBytes(data);

    for (int pos = 0; pos < data.Length - 10; pos += 25) {
      data[pos] = (pos % 2 == 0) ? (byte)0xE8 : (byte)0xE9;
      int target = (pos + 30) % data.Length;
      int rel = target - (pos + 5);
      data[pos + 1] = (byte)rel;
      data[pos + 2] = (byte)(rel >> 8);
      data[pos + 3] = (byte)(rel >> 16);
      data[pos + 4] = (byte)(rel >> 24);
    }

    var (main, call, jump, range) = Bcj2Filter.Encode(data);
    byte[] decoded = Bcj2Filter.Decode(main, call, jump, range, data.Length);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Encode_SplitsIntoFourStreams() {
    byte[] data = new byte[50];
    Array.Fill(data, (byte)0x90);
    data[10] = 0xE8;
    int rel = 30 - 15;
    data[11] = (byte)rel;
    data[12] = 0;
    data[13] = 0;
    data[14] = 0;

    var (main, call, jump, range) = Bcj2Filter.Encode(data);
    Assert.That(main.Length, Is.GreaterThan(0));
    Assert.That(range.Length, Is.GreaterThan(0));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyData() {
    byte[] data = [];
    var (main, call, jump, range) = Bcj2Filter.Encode(data);
    byte[] decoded = Bcj2Filter.Decode(main, call, jump, range, 0);
    Assert.That(decoded, Is.Empty);
  }
}
