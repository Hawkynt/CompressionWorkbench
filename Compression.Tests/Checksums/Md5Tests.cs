using System.Text;
using Compression.Core.Checksums;

namespace Compression.Tests.Checksums;

[TestFixture]
public class Md5Tests {
  [Category("ThemVsUs")]
  [Test]
  public void Empty_Input() {
    var hash = Md5.Compute([]);
    Assert.That(Convert.ToHexString(hash), Is.EqualTo("D41D8CD98F00B204E9800998ECF8427E"));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Single_Character() {
    var hash = Md5.Compute("a"u8);
    Assert.That(Convert.ToHexString(hash), Is.EqualTo("0CC175B9C0F1B6A831C399E269772661"));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Abc() {
    var hash = Md5.Compute("abc"u8);
    Assert.That(Convert.ToHexString(hash), Is.EqualTo("900150983CD24FB0D6963F7D28E17F72"));
  }

  [Category("ThemVsUs")]
  [Test]
  public void MessageDigest() {
    var hash = Md5.Compute("message digest"u8);
    Assert.That(Convert.ToHexString(hash), Is.EqualTo("F96B697D7CB7938D525A2F31AAF161D0"));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Alphabet() {
    var hash = Md5.Compute("abcdefghijklmnopqrstuvwxyz"u8);
    Assert.That(Convert.ToHexString(hash), Is.EqualTo("C3FCD3D76192E4007DFB496CCA67E13B"));
  }

  [Category("HappyPath")]
  [Test]
  public void Produces_16_Bytes() {
    var hash = Md5.Compute("test"u8);
    Assert.That(hash, Has.Length.EqualTo(16));
  }
}
