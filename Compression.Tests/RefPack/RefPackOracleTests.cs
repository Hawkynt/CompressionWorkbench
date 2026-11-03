using Compression.Tests.CodecProof;
using FileFormat.RefPack;

namespace Compression.Tests.RefPack;

/// <summary>
/// Third-party proof for RefPack (QFS, <c>10 FB</c> header) against refpack-rs 5.0.4
/// (actioninja, MPL-2.0, used as a black-box oracle through a 15-line CLI around
/// <c>easy_compress</c>/<c>easy_decompress</c> with its <c>SimEA</c> header mode and the
/// <c>Optimal</c> compressor).
/// <list type="bullet">
/// <item>A stream refpack-rs wrote decodes with ours.</item>
/// <item>refpack-rs decoded the stream we write for the same text, and every stream we wrote for
/// text, mixed bytes, zeros and 100 KB of text; the text stream is pinned, so an encoder change has
/// to be re-proven.</item>
/// </list>
/// </summary>
[TestFixture]
public sealed class RefPackOracleTests {

  private const string RefPackRsText =
    "EPsABADlZXNjYXBlIG9yZGVyIHRoZSByYW5nZSBkDhNvZwgJ4nN5bWJvbCBxdWljaw8HIDAK4Gp1bXARJHPibGF6eSA3OTE5Cm92AA4QVOBtb2" +
    "RlEQxsEC7jMTU4MzgKZm94IGNvbnRleAYwdCAMWQxLDDUIdhxbDCAUhwA+CB/iYnJvd24gMjM3NTcKEE4IPQghiQB3BC4IFOAzMTY3Alg2Chh0" +
    "HF4IihBU4DM5NTkGGzUKDFEQBeA0NzUxClI0CggtDKIUyyQ/FAsMRwwFDCUMiQSFiQAV4DU1NDMtUjMAPzQ04DYzMzUlJzKKAGEMQ4gBLjCv4D" +
    "cxMjca6zEKNCKJATcAsRxJKK0KPTAKBC0QIxQGiAFOGBTgODcxMA4MOQoMiSg9FCwERggwDGlcNRys4Dk1MDISoDgKDLoMQjhpHHozajI5NBAo" +
    "PK8kKDDfLEsI6+ExMDg2Mwp0aAxSXLhQghgxKFVN2nYISeAxODc4iIBPMgoQayx/PLtcXIgAl+AyNjcwLUkxfD2IAQIMUwgbaGl4IQwkXHaIAl" +
    "PgMzQ2MgoWMAoIdzRIVHxwGRBVXPAcGlSz4DI1MzlMYFgOPOP8";

  private const string OursText =
    "EPsABADlZXNjYXBlIG9yZGVyIHRoZSByYW5nZSBkEhNvZwQJ4nN5bWJvbCBxdWljaw8HIDAK4Gp1bXARJHPibGF6eSA3OTE5Cm92AA4QVOBtb2" +
    "RlFQxsDC7jMTU4MzgKZm94IGNvbnRleAowdCAMUQxLDDUcgAhbDCAQhwQ+BB/iYnJvd24gMjM3NTcKFE4MPQQhiQB3BC4EFOAzMTY3Dlg2CgxT" +
    "hwBeCIoMVOAzOTU5Chs1CgxRDAXgNDc1MRL5NAoELQxJFHwkPxQLDEcMBQwlDDYEhYgAFeA1NTQzMVIzBD8MaOA2MzM1KScyigBhDEOIAS4sr+" +
    "A3MTI3EZgxFOsEB4kBNwixFEkkrQ49MAoELRAjEAaKAU4QFOA4NzEwEgw5CgxrKCEULARGDDAIaYkCNQys4Dk1MDIWoDgKDHwIQocBaRRNN2oy" +
    "OTQMKIcBryQoMN8sSwRS4DEwODYGGjMKCCOHArgQKhAGKFUEoAQhBC7gMTg3OImATzIKDGs8fwyHXFyIAJfgMjY3MDFJMYcDPYcBAgxTDBsMTx" +
    "BsTENcdocCU+AzNDYyDhYwCgx3iQN2BCgI5jzBiABThwFP4DQyNTNRYDmJAg4oaPw=";

  [Test, Category("HappyPath")]
  public void GivenARefPackRsStream_WhenDecoded_ThenItIsTheSample() {
    using var output = new MemoryStream();
    RefPackStream.Decompress(new MemoryStream(Convert.FromBase64String(RefPackRsText)), output);
    Assert.That(output.ToArray(), Is.EqualTo(CodecProofData.Text(1024)));
  }

  [Test, Category("HappyPath")]
  public void GivenTheSampleText_WhenCompressed_ThenItIsTheStreamRefPackRsRestored() {
    using var output = new MemoryStream();
    RefPackStream.Compress(new MemoryStream(CodecProofData.Text(1024)), output);
    Assert.That(output.ToArray(), Is.EqualTo(Convert.FromBase64String(OursText)));
  }
}
