using System.Text;
using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class BlakeTests {
  [TestCase("", "7DC5313B1C04512A174BD6503B89607AECBEE0903D40A8A569C94EED")]
  [TestCase("The quick brown fox jumps over the lazy dog", "C8E92D7088EF87C1530AEE2AD44DC720CC10589CC2EC58F95A15E51B")]
  public void Blake224MatchesSourceVectors(string text, string expected) =>
    Assert.That(Convert.ToHexString(Blake.Compute224(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [TestCase("", "716F6E863F744B9AC22C97EC7B76EA5F5908BC5B2F67C61510BFC4751384EA7A")]
  [TestCase("BLAKE", "07663E00CF96FBC136CF7B1EE099C95346BA3920893D18CC8851F22EE2E36AA6")]
  [TestCase("The quick brown fox jumps over the lazy dog", "7576698EE9CAD30173080678E5965916ADBB11CB5245D386BF1FFDA1CB26C9D7")]
  public void Blake256MatchesSourceVectors(string text, string expected) =>
    Assert.That(Convert.ToHexString(Blake.Compute256(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [Test]
  public void Blake384MatchesSourceVector() =>
    Assert.That(Convert.ToHexString(Blake.Compute384([])), Is.EqualTo("C6CBD89C926AB525C242E6621F2F5FA73AA4AFE3D9E24AED727FAAADD6AF38B620BDB623DD2B4788B1C8086984AF8706"));

  [Test]
  public void Blake512MatchesSourceVector() =>
    Assert.That(Convert.ToHexString(Blake.Compute512([])), Is.EqualTo("A8CFBBD73726062DF0C6864DDA65DEFE58EF0CC52A5625090FA17601E1EECD1B628E94F396AE402A00ACC9EAB77B4D4C2E852AAAA25A636D80AF3FC7913EF5B8"));
}
