using System.Reflection;
using FileFormat.Pcap;
using FileFormat.Pcapng;

namespace Compression.Tests.Pcap;

/// <summary>
/// pcap and pcapng against Wireshark's own tools. The fixtures were written by text2pcap and editcap
/// (Wireshark 4.2.2) from <c>frame0..2.bin</c>, and tshark read each as the three frames at
/// 1793347013.000001/2/3 s. Captures we write are read back by tshark, capinfos and tcpdump in WSL.
/// See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class CaptureOracleTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"PcapVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded capture vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly DateTimeOffset Base = DateTimeOffset.FromUnixTimeSeconds(1793347013);
  private static byte[][] Frames => [Vector("frame0.bin"), Vector("frame1.bin"), Vector("frame2.bin")];
  private static DateTimeOffset At(int i) => Base.AddTicks((i + 1) * 10); // 1, 2, 3 microseconds

  [TestCase("text2pcap.pcap", false)]
  [TestCase("editcap-nsec.pcap", true)]
  [Category("ThirdPartyVector")]
  public void Given_WiresharkPcap_When_Read_Then_FramesAndTimesMatchTshark(string vector, bool nano) {
    var c = PcapReader.Read(Vector(vector));
    Assert.That((c.LittleEndian, c.Nanosecond, c.LinkType, c.Snaplen), Is.EqualTo((true, nano, 1u, 262144u)));
    Assert.That(c.Packets.Select(p => p.Data), Is.EqualTo(Frames));
    for (var i = 0; i < 3; ++i) {
      var p = c.Packets[i];
      Assert.That(p.TimestampSeconds, Is.EqualTo(1793347013u));
      Assert.That(p.TimestampFraction, Is.EqualTo((uint)((i + 1) * (nano ? 1000 : 1))));
    }
  }

  [TestCase("text2pcap.pcap", false)]
  [TestCase("editcap-nsec.pcap", true)]
  [Category("ThirdPartyVector")]
  public void Given_SameFramesAndTimes_When_WeWritePcap_Then_ByteIdenticalToWireshark(string vector, bool nano) {
    using var ms = new MemoryStream();
    PcapWriter.Write(ms, new PcapSettings(Nanosecond: nano), Frames.Select((f, i) => (At(i), f)));
    Assert.That(ms.ToArray(), Is.EqualTo(Vector(vector)));
  }

  [TestCase("text2pcap.pcapng")]
  [TestCase("editcap.pcapng")]
  [Category("ThirdPartyVector")]
  public void Given_WiresharkPcapng_When_Read_Then_FramesAndTimesMatchTshark(string vector) {
    var c = PcapngReader.Read(Vector(vector));
    Assert.That(c.Interfaces.Select(i => i.LinkType), Is.EqualTo(new[] { 1u }));
    Assert.That(c.Packets.Select(p => p.Data), Is.EqualTo(Frames));
    for (var i = 0; i < 3; ++i)
      Assert.That(c.Interfaces[0].ToDateTime(c.Packets[i].TimestampRaw), Is.EqualTo(At(i).UtcDateTime));
  }

  // ── Wireshark/tcpdump read what we write ───────────────────────────

  private static string TsharkFields(string wslPath)
    => FsInteropToolbox.RunWsl($"tshark -r '{wslPath}' -T fields -e frame.time_epoch -e frame.cap_len -e frame.len 2>&1").StdOut.Trim();

  [TestCase(false)]
  [TestCase(true)]
  [Category("EndToEnd")]
  public void Given_OurPcap_When_ReadByTsharkAndTcpdump_Then_SameFramesAndTimes(bool nano) {
    if (!FsInteropToolbox.WslHasTool("tshark") || !FsInteropToolbox.WslHasTool("tcpdump")) Assert.Ignore("tshark/tcpdump not in WSL.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_pcap_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "ours.pcap");
      using (var fs = File.Create(path))
        PcapWriter.Write(fs, new PcapSettings(Nanosecond: nano), Frames.Select((f, i) => (At(i), f)));
      var w = FsInteropToolbox.WinToWsl(path);
      Assert.That(TsharkFields(w), Is.EqualTo(
        "1793347013.000001000\t60\t60\n1793347013.000002000\t67\t67\n1793347013.000003000\t74\t74"));
      var dump = FsInteropToolbox.RunWsl($"tcpdump -r '{w}' -xx -n -tt 2>/dev/null").StdOut;
      Assert.That(dump.Split('\n').Count(l => l.StartsWith("1793347013.", StringComparison.Ordinal)), Is.EqualTo(3), dump);
    } finally { Directory.Delete(dir, true); }
  }

  [TestCase(false)]
  [TestCase(true)]
  [Category("EndToEnd")]
  public void Given_OurPcapng_When_ReadByTsharkAndCapinfos_Then_SameFramesAndTimes(bool nano) {
    if (!FsInteropToolbox.WslHasTool("tshark") || !FsInteropToolbox.WslHasTool("capinfos")) Assert.Ignore("tshark/capinfos not in WSL.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_pcapng_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "ours.pcapng");
      using (var fs = File.Create(path))
        PcapngWriter.Write(fs, new PcapngSettings(Nanosecond: nano), Frames.Select((f, i) => (At(i), f)));
      var w = FsInteropToolbox.WinToWsl(path);
      Assert.That(TsharkFields(w), Is.EqualTo(
        "1793347013.000001000\t60\t60\n1793347013.000002000\t67\t67\n1793347013.000003000\t74\t74"));
      var info = FsInteropToolbox.RunWsl($"capinfos -c -t '{w}' 2>&1").StdOut;
      Assert.That(info, Does.Contain("pcapng"));
      Assert.That(info, Does.Match(@"Number of packets:\s+3"));
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("EndToEnd")]
  public void Given_WiresharkPcap_When_PacketRemovedAndAppendedByUs_Then_TsharkReadsTheEdit() {
    if (!FsInteropToolbox.WslHasTool("tshark")) Assert.Ignore("tshark not in WSL.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_pcap_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "edited.pcap");
      File.WriteAllBytes(path, Vector("text2pcap.pcap"));
      var d = new PcapFormatDescriptor();
      using (var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite)) {
        d.Remove(fs, ["packet_0000.bin"]);
        d.Add(fs, [Compression.Registry.ArchiveInputInfo.InMemory("extra.bin", new byte[42])]);
      }
      var fields = TsharkFields(FsInteropToolbox.WinToWsl(path)).Split('\n');
      Assert.That(fields, Has.Length.EqualTo(3));
      Assert.That(fields[0], Does.StartWith("1793347013.000002000\t67"));
      Assert.That(fields[2], Does.EndWith("\t42\t42"));
    } finally { Directory.Delete(dir, true); }
  }
}
