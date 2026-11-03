using Compression.Core.Dictionary.QuickLz;
using Compression.Tests.CodecProof;
using FileFormat.QuickLz;

namespace Compression.Tests.QuickLz;

/// <summary>
/// Third-party proof for QuickLZ packets against Lasse Mikkel Reinhold's QuickLZ 1.5.0
/// (<c>quicklz.c</c>/<c>quicklz.h</c> as shipped in PerconaFT revision
/// <c>74d500ff12edf9cc8e6539adfa0a3f6be0707246</c>, GPL-2.0, used only as a black-box oracle),
/// built with g++ 16.2 at <c>QLZ_COMPRESSION_LEVEL</c> 1 and 3, <c>QLZ_STREAMING_BUFFER</c> 0.
/// <list type="bullet">
/// <item>Packets <c>qlz_compress</c> wrote at levels 1 and 3 decode with ours.</item>
/// <item><c>qlz_decompress</c> restored the level-1 and level-3 packets we write for the same
/// text; they are pinned, so an encoder change has to be re-proven. On text and runs from 23 bytes
/// to 100 KB the official library also read every packet we wrote at both levels.</item>
/// </list>
/// Level 2 packets are refused, not decoded.
/// </summary>
[TestFixture]
public sealed class QuickLzReferenceTests {

  private const string QuickLz1Text =
    "RwUCAAAABAAAAAAAjGVzY2FwZSBvcmRlciB0aGUgcmFuZ2UgZG9nZYAikXN5bQAQBIBib2wgcXVpY2sgMApkPmp1bXBzZYBsYXp5IDc5MTkKb3" +
    "YFBgDgIQZlVFZtb2RlbCAlVdQ7MTU4MzgKZm94IGNvbnRleHQggwNkPv8B4IeEQrSSIpGEQqNsZD5F+gKOIpFicm93biAyMzc1NwpWmLSSAo4r" +
    "VSKRAo4zMTY3fODA/zYKhEJkPqlsgwNUSTM5NTk1CqNstJK0kjQ3NTE0CtQ7gwOEQlaYIuJWmFRJVEmEQgcHf/DUOwKOWkk1NTQzM9VlIuK0kj" +
    "YzMzUyww+EQlaY1DtkPoRCRHk3MTI3McXVJpEikZ8/8L9kPkX6gwNWmEKiMApEeQKORfpF+laYtJJF+jg3MTA5CkX61DujbFaYAo60koMDRfpk" +
    "PoRCOeCPf+A1MDI4CiKRAo5EeaNso2y0klaYMjk0RslEeWQ+o2wi4iVVVEkikTEwODYzCiLio2z/wP/gIpFF+iVVJVUCjoMDIpEi4jE4NzgyCi" +
    "VVVElkPgOOQyLUO7SSAo7UO0R5MjY3MDHVQUR5/w/8nyKRgwNF+lRJRHmEQiVVgwODA1RJgwOEQjM0NjIwClRJtJJkPtQ7gwOjbER5Ao4qVaNs" +
    "tJI0MjgAAIA1MznVZWQ+Rfpjb250ZQ==";

  private const string QuickLz3Text =
    "T9IBAAAABAAAAAAAjGVzY2FwZSBvcmRlciB0aGUgcmFuZ2UgZG9nEgWGAnN5bQAQBIBib2wgcXVpY2sgMAoOAmp1bXBzUglsYXp5IDc5MTkKb3" +
    "aDAQD4PFIVbW9kZWxWA84LMTU4MzgKZm94IGNvbnRleHQgSgyOFA4Tjg0/APzAXiAKF04IEiLGDwYIYnJvd24gMjM3NTcK1hOOD4YIKh7GC0YF" +
    "MzE2NzYKThYPHPj/DhXiF8oiThUzOTU5NQoKB44UjgE0NzUxNAqSPoYLjhJWHwZQFgMOEo4BjgnODYYhpgXg4IO/NTU0MzPSVAYQTho2MzM1Mg" +
    "pKrhgOEeZLDmw3MTI3MVImFjsGAipOiiyWEoZrMH7gP/AKjg+GCxIJ0gHuU1IFODcxMDkKUgMOG4pIVgvGEU4MihqqjU4rOTUwMjgKVihOH8oQ" +
    "4w/8h6JalhMyOTTWWk4KImxGShJ4DlPGFDEwODYzCsYGCglirtIK0gGKVUYohgjGCzE4Nzj84H/gMgoqFA4bHmAOIl6XJiYyNjcwMZJSos/iQA" +
    "4VDgcOFNbdCpHenSKVMzQ2MjAKzgUOHj8YAIDq3UYKyjmecCYVIlQ0MjUzOVKY6oNjb250ZQ==";

  private const string QuickLz3Zeros =
    "TzsAAADQBwAAqKoCgAAAAAP+AQAAA/4BAAAD/gEAAAP+AQAAA/4BAAAD/gEAAAP+AQAAA+MBAAAAAAA=";

  private const string OursLevel1Text =
    "RwQCAAAABAAAAAAAjGVzY2FwZSBvcmRlciB0aGUgcmFuZ2UgZG9nZYAikXN5bQAQBIBib2wgcXVpY2sgMApkPmp1bXBzZYBsYXp5IDc5MTkKb3" +
    "YDAwDwIQYlVW1vZGVsICVV1DsxNTgzOApmb3ggY29udGV4dCCDA2Q+hEL/APCDtJIikYRCo2xkPkX6Ao4ikWJyb3duIDIzNzU3ClaYtJICjitV" +
    "IpECjjMxNjc2PnDg/wqEQmQ+qWyDA1RJMzk1OTUKo2y0krSSNDc1MTQK1DuDA4RCVpgi4laYVElUSYRC1DuDgz/4Ao5aSTU1NDMz1WUi4rSSNj" +
    "MzNTLDD4RCVpjUO2Q+hEJEeTcxMjcxxdUmkSKRZD7PH/ifRfqDA1aYQqIwCkR5Ao5F+kX6Vpi0kkX6ODcxMDkKRfrUO6NsVpgCjrSSgwNF+mQ+" +
    "hEI5NfDHP/AwMjgKIpECjkR5o2yjbLSSVpgyOTRGyUR5ZD6jbCLiJVVUSSKRMTA4NjMKIuKjbCKRf+B/8EX6JVUlVQKOgwMikSLiMTg3ODIKJV" +
    "VUSWQ+A45DItQ7tJICjtQ7RHkyNjcwMdVBRHkikf8H/o+DA0X6VElEeYRCJVWDA4MDVEmDA4RCMzQ2MjAKVEm0kmQ+1DuDA6NsRHkCjipVo2y0" +
    "kjQyNRwAAIAzOdVlZD5F+mNvbnRl";

  private const string OursLevel3Text =
    "T9IBAAAABAAAAAAAjGVzY2FwZSBvcmRlciB0aGUgcmFuZ2UgZG9nEgWGAnN5bQAQBIBib2wgcXVpY2sgMAoOAmp1bXBzUglsYXp5IDc5MTkKb3" +
    "aDAQD4PFIVbW9kZWxWA84LMTU4MzgKZm94IGNvbnRleHQgSgyOFA4Tjg0/APzAXiAKF04IEiLGDwYIYnJvd24gMjM3NTcK1hOOD4YIKh7GC0YF" +
    "MzE2NzYKThYPHPj/DhXiF8oiThUzOTU5NQoKB44UjgE0NzUxNAqSPoYLjhJWHwZQFgMOEo4BjgnODYYhpgXg4IO/NTU0MzPSVAYQTho2MzM1Mg" +
    "pKrhgOEeZLDmw3MTI3MVImFjsGAipOiiyWEoZrMH7gP/AKjg+GCxIJ0gHuU1IFODcxMDkKUgMOG4pIVgvGEU4MihqqjU4rOTUwMjgKVihOH8oQ" +
    "4w/8h6JalhMyOTTWWk4KImxGShJ4DlPGFDEwODYzCsYGCglirtIK0gGKVUYohgjGCzE4Nzj84H/gMgoqFA4bHmAOIl6XJiYyNjcwMZJSos/iQA" +
    "4VDgcOFNbdCpHenSKVMzQ2MjAKzgUOHj8YAIDq3UYKyjmecCYVIlQ0MjUzOVKY6oNjb250ZQ==";

  [TestCase(QuickLz1Text, TestName = "GivenAnOfficialLevel1Packet_WhenDecoded_ThenItIsTheSample")]
  [TestCase(QuickLz3Text, TestName = "GivenAnOfficialLevel3Packet_WhenDecoded_ThenItIsTheSample")]
  [Category("HappyPath")]
  public void OfficialTextPacketDecodes(string packet)
    => Assert.That(Decode(Convert.FromBase64String(packet)), Is.EqualTo(CodecProofData.Text(1024)));

  [Test, Category("HappyPath")]
  public void GivenAnOfficialLevel3PacketOfZeros_WhenDecoded_ThenItIsTheZeros()
    => Assert.That(Decode(Convert.FromBase64String(QuickLz3Zeros)), Is.EqualTo(new byte[2000]));

  [TestCase(QuickLzCompressionLevel.Level1, OursLevel1Text, TestName = "GivenTheSampleText_WhenCompressedAtLevel1_ThenItIsThePacketQuickLzRestored")]
  [TestCase(QuickLzCompressionLevel.Level3, OursLevel3Text, TestName = "GivenTheSampleText_WhenCompressedAtLevel3_ThenItIsThePacketQuickLzRestored")]
  [Category("HappyPath")]
  public void OurPacketIsTheOneQuickLzRestored(QuickLzCompressionLevel level, string packet) {
    using var output = new MemoryStream();
    QuickLzStream.Compress(new MemoryStream(CodecProofData.Text(1024)), output, level);
    Assert.That(output.ToArray(), Is.EqualTo(Convert.FromBase64String(packet)));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenALevel2Packet_WhenDecoded_ThenItIsRefused() {
    var packet = Convert.FromBase64String(QuickLz3Text);
    packet[0] = (byte)(packet[0] & ~0x0C | 0x08); // level bits 2..3 = 2
    Assert.Throws<NotSupportedException>(() => Decode(packet));
  }

  private static byte[] Decode(byte[] packet) {
    using var output = new MemoryStream();
    QuickLzStream.Decompress(new MemoryStream(packet), output);
    return output.ToArray();
  }
}
