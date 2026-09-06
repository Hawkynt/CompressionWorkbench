#pragma warning disable CS1591
using System.IO.Compression;
using Codec.Ac3;

namespace Compression.Tests.Codecs.Ac3;

/// <summary>
/// Decodes AC-3 elementary streams produced by libavcodec and compares the result against
/// libavcodec's own decode of the same bytes, sample by sample.
/// <para>
/// Our encoder never emits coupling, rematrixing, block switching or exponent reuse, so a decoder
/// checked only against our own output leaves those paths untested; every one of them was broken.
/// The two streams here are the smallest that cover them: the stereo one uses coupling and
/// rematrixing at 192 kbit/s, the 5.1 one exercises the 3/2 + LFE channel map, and both reuse
/// exponent, bit-allocation and SNR state across the blocks of a frame and across frames.
/// </para>
/// <para>
/// The comparison is not bit-exact by construction: A/52 §7.3.4 leaves the dither sequence used for
/// zero-bit mantissas to the implementation, so our dither and libavcodec's differ. Everything else
/// must agree, which is what the error bounds below are calibrated to.
/// </para>
/// </summary>
[TestFixture]
public class Ac3ForeignStreamTests {

  /// <summary>Two independent noise sources, 48 kHz stereo, 192 kbit/s, encoded by libavcodec.</summary>
  private const string StereoPinkStream =
    "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";

  /// <summary>libavcodec's decode of <see cref="StereoPinkStream"/>: interleaved 16-bit PCM, deflated.</summary>
  private const string StereoPinkReference =
    "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" +
    "RstSf6B/1A31cTsaR9GTaj6/PC15hHRKyngjOhv5aMcdkKz6wl6texm0VDqR/kzml7Ci8510iOhg10TxTI+3tR3QCclhZOkswS7z" +
    "Ytco4ziQp72PaeF3fbjma9owW1VMvhqX6OwY2YVZZeGrbDA5VIXzbhaXRS5vP8ETC8oCCqVGTGBaHfDFURXpVUdb/UX16jVdK+WT" +
    "VALaScUJwsbO/fYt0pJMS8t6qLZlTHOh5nnIdklB60V5dFeXOLt5UvvJL+WGk9GftaNMj9WHRP9pXzOmdbjEvwTBsf9rbex5vFIq" +
    "nd1abQmRb6Ju0+T010lLe/5smVZbJ0rt8tEiUSnCcGJVm7pluGR159T2d9Vxf3eJournRN9uvRN6o6sfOb09zq6q+bd2n3htzCBN" +
    "5aDj7Rc0xZ1h8CLZfAaiGMruEMMaH7lfh0e7TS0X7W+wdBio3vYdEVxZTlCxvjFCp6mOSjHU5L1QtPnVSZLgdIWExVDUmJbp8sp9" +
    "jP0/l8qru+YK/6XU6BMyXpm+oWGmDd4nGl3vsy0QbJXND8Brj0FXNQnW4eolPqekMym5lujJLlWRkwcXx6gQ6lCxeOovVPu/vmGt" +
    "y+1ptcs6V0tWDxjTkKN6XN7SlPYrtWV907qAnS0LAuaUz1EWVy8Pmlvbjh1rCutc0uZS6tpuJPpUxhFG/O5Tuak1EL+rYlx9v0rE" +
    "tbh9Xv+clssGTucO2SXpOsYfqTNmsIQWP1fLHb5RZUJNeknXO6Ap8DSU3TTZ3UuXhdME8y2LXNsVg/CB4kLzZQNCMJi2SP9RBX/t" +
    "r31HzZScJi4UsBF/mQLBG1vUhe5sXYl1aWuM0UM9ZfDJPNKZ4wCa/ez2ruvZxc08Xk3D2+ZnsuHcYdL5hLLO2C/bzauQfbYyRpWc" +
    "Z/qjKgsdrD0fUG64z6bpDe4E5ebOnpZTkWcdO3P1lpjkCvu+yKfWv6QgKbn2nWBWV5CtZ1685fSAwQI3VNQ1nVWtGhBp19ECM0Vn" +
    "uKu7OBMzJW8HXpDXlA+xebAJxv/l+YsCg+U6kNxpu4iVS9ZaKO0PaMvlHcByh9p22p5or1I+0D1SH2Scli8SR2l8nWcsdZmPpI9a" +
    "Zpk1CoKzjPVLkq/qJ2SzFHJnwBWjb0SC8SQ11ziMcNyYhDuh3046AzXP+WKcFyaxU5R2T2Z7mD7s1V2rtx+oXZLQV7HAuMcxJzxB" +
    "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";

  /// <summary>Six distinct tones, 48 kHz 5.1, 448 kbit/s, encoded by libavcodec.</summary>
  private const string SurroundStream =
    "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" +
    "UU+wsk5Ms7VMSra4SUi4ukdFurxFQ7y+Q0G+wEF/Agv30KG9eQ4jx3FjO3UaO5cx5DhvIkt20mU2aypbVrLmNGkyazZzZzNlOnMm" +
    "M6exYz6DG5EHUPAJ4MQrIQOjnZcALCsC0hONtJbFAEEE+jVAESC/6XfyV0GbW+BBCcb2GcKqL1VULLH1EiLLz1D8WRWq3lcvrMhV" +
    "Q660U1WwpFFjsphPb7SMS3m2gpMFcvSPE3bmiSN42IctfM6DOYDEf0OEun1LhrJ5U4qqd1uMpHNhjp5zZ5CYb2uSkm1xlI5rdZaK" +
    "aXmYhml9moLKBOfsoU58xhz3jGLQdMI1ByvkUXC+RSbrpVJsul0mq2ZTaLZlNnUqzqrQ1Uswr8f0cf5k2njaLxiM28F3YBGop5gU" +
    "o0Xc+gIMJOX7ZJBnIba0OWvUQIjkOVjFlQ7ifDtw9KECcN4uIXL/DEDUn4rAVf/pv/bgSL+3wIf/kQFOnpIhrV5TQexeFCILffTi" +
    "Kt3VomodtkJpnZbiiR2XQqidd8KoPXBLPdcQs71RizjVJLNtUsu01TS7LTPLsNNAuv00i63TTLq9NUuq01i6nTXMKNNkun02TCbR" +
    "bLpdFwwk0ISvZhgkvHelCAizAhZ+aYC967i3RnWHb8kGZHtr5oRx4/O2EdYZEUVu9uxAuz3Cjt84+sybIyRtLAK3diTTGa4oURav" +
    "6s+UcSzOknIujZDZi/mV8nohfdKSQWm2ollZnrJxSYbChTlyzpktYtqpJVLiuRlG7sURNvbRCS7+2QEjBuT5Gwrs9RMS9O0LGvzp" +
    "/HguX0iG4eyYjZ3KitnUuO1czJDRxNks282UyazpbJpPmMWk+aw/SD8MbOUhTbID5CCEEIIQQgqqY+fPnz59/zq+fPnz58+fPnz5" +
    "8+fPnz58+fPnz58+fPnx/zq+fPnz58+fPnz58+fPnz58+fPnz58+fPnx/zq+fPnz58+fPnz58+fPnz58+fPnz58+fPnx/zq+fPnz" +
    "58+fPnz58+fPnz58+fPnz58+fPnx/zq+fPnz58+fPnz58+fPnz58+fPnz58+fPnwAB4BtAeAbQHgGA8A2gPANIAAAAAAAAAAAAfA" +
    "AAAAPANoDwDaA8AwHgG0B4BpAAAAAAAAAAAAD4AAAAB4BtAeAbQHgGA8A2gPANIAAAAAAAAAAAAAzxk=";

  /// <summary>libavcodec's decode of <see cref="SurroundStream"/>: interleaved 16-bit PCM, deflated.</summary>
  private const string SurroundReference =
    "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" +
    "lOpswijLR9pjGQuE+zwFX9qt8TF8BDcSfiQzb47H6lnG9bAHS60m2De7OzND+VDVeg4n8s81uBGwxXwSX4xX5XYx21FTsyE+FswT" +
    "m9NricaoS/Z0lfpb0F75gOjzYfwh6OTvkpBRBW+X1hGucUMgyhu3Vdupo+I3wt30R3EJKCaWjB3kJL0MscDpbK7zCZOrlcTHCEsZ" +
    "HXmOGq0Vw1nor0hz+573rAbEqjsY1a1v4x5WCNdfiiuYH0d/xhKeGC6DGxqjI0vJmnYwp7Xu+jJrKOZxrr+EqIVE/jmEL8TOxlKS" +
    "+WcOt46cpJbg0VoFArbNzK3hFngDRuRa1xbJxXAd0pvBH+NziMJ+AYMTvwEUw7VJO0vaL6LmeB2GZP9l8hI8itggR8b5PpyiLdCf" +
    "So5/llqrOUYHHCbPvRzrpn9lDSfz2ed/bTUwRka+IqxvB3NOK29+FD1GTmJiuCyuaq2PU5pK1rUEN7NveY3JercqGcaoyFPUOK04" +
    "3ob+iOQ4E5kDWin8K3zolYjlcqpeluzbJ+yI+LVwL7KfJUER89xrwg69ItnnNupr/g6pmFGV7P9qOBZkKEONmuRcfgKiTtUzyfNa" +
    "o37PZKN0sxH+JVgoVuOGwzbzKXwhXpObRs69uvU8buhU8fOJPSwn9uCabzkNiJ2Us1vg0fr3Rv/k5xdt8M/EgmZKXVBqpANZmYSX" +
    "JD/v6IJbEHubo+TKhAeS3a5qvZv8fKQvnmekx2qi+kJmdACx2yeow6hi+Gp0OLHnCuEWZGNHxcbhHV5NeTVsolF8Mt5MLyUa5c1g" +
    "Q3wGnhGKQxxKe83dhZhYD5hwMZbnkh0lfnQMcp0J3hqcjprzE+Aw8bvv8Fcwii4BAfHHXZhKGRX/FA0xegYHcCL/XOi/x/B58j+o" +
    "NYhfn8Ij5fq4tjIcN6WvEH8fhmvLJQkO3MY5nKanSALBBwv/RbWO5AsdiCaktEvJ/PNdvIeVtXLR20zic67OXBntoDVVMDgwNK6K" +
    "phpYmcZON6eFGmiJ/PMjyfxzY60KPgerkvnnV4gtGmq9ID2Wzr+mFdNSpJ3eSPc4/xbBvVzuFTfH7y58oC23cv3LMYnOF8ZpmODk" +
    "pGgHtr84TdvqVjSfcKaEbohfaqspWrPsb/je0hptNhfCP1tzxQtSttZIaK9tNzPl9vIBraO8zdxp/KHsk//UDqhbo3/pCNVRLmud" +
    "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";

  private static short[] Decode(byte[] stream) {
    using var src = new MemoryStream(stream, writable: false);
    using var dst = new MemoryStream();
    Ac3Codec.Decompress(src, dst);
    return ToSamples(dst.ToArray());
  }

  private static short[] Reference(string deflated) {
    using var src = new MemoryStream(Convert.FromBase64String(deflated), writable: false);
    using var inflate = new ZLibStream(src, CompressionMode.Decompress);
    using var dst = new MemoryStream();
    inflate.CopyTo(dst);
    return ToSamples(dst.ToArray());
  }

  private static short[] ToSamples(byte[] pcm) {
    var samples = new short[pcm.Length / 2];
    Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 2);
    return samples;
  }

  private readonly record struct Error(double Rms, double ReferenceRms, int Peak, double Gain, double WithinOne);

  private static Error Compare(short[] ours, short[] reference) {
    double sumSquared = 0, referenceSquared = 0, cross = 0, ourSquared = 0;
    var peak = 0;
    var withinOne = 0;
    for (var i = 0; i < reference.Length; ++i) {
      double a = reference[i];
      double b = ours[i];
      var d = a - b;
      sumSquared += d * d;
      referenceSquared += a * a;
      ourSquared += b * b;
      cross += a * b;
      var abs = Math.Abs((int)d);
      if (abs > peak) peak = abs;
      if (abs <= 1) ++withinOne;
    }
    var n = reference.Length;
    return new Error(Math.Sqrt(sumSquared / n), Math.Sqrt(referenceSquared / n), peak,
      ourSquared > 0 ? cross / ourSquared : 0, (double)withinOne / n);
  }

  private static void AssertMatchesReference(string streamBase64, string referenceBase64,
                                             int channels, double maxRelativeRms) {
    var stream = Convert.FromBase64String(streamBase64);
    var reference = Reference(referenceBase64);
    var ours = Decode(stream);

    Assert.That(ours, Has.Length.EqualTo(reference.Length),
      "the decode must cover the whole stream, not stop at the first block it cannot parse");
    Assert.That(reference.Length % channels, Is.Zero);

    var error = Compare(ours, reference);
    Assert.Multiple(() => {
      Assert.That(error.Rms / error.ReferenceRms, Is.LessThan(maxRelativeRms),
        $"rms error {error.Rms:F2} against reference rms {error.ReferenceRms:F1} (peak {error.Peak})");
      Assert.That(error.Gain, Is.EqualTo(1.0).Within(0.005),
        "the decoded level must match; a scale or table error shows up here first");
    });
  }

  /// <summary>
  /// 0.064 s of two independent noise sources, 48 kHz stereo at 192 kbit/s. libavcodec's encoder couples the upper
  /// sub-bands and rematrixes the pair, so this stream is the one that exercises the coupling
  /// coordinates, the coupling-channel exponents and the sum/difference undo.
  /// </summary>
  [Test]
  [Category("RoundTrip")]
  public void CoupledStereoMatchesLibavcodec()
    => AssertMatchesReference(StereoPinkStream, StereoPinkReference, channels: 2, maxRelativeRms: 0.06);

  /// <summary>
  /// 0.064 s of six distinct tones, 48 kHz 5.1 at 448 kbit/s. The bit stream carries the front
  /// channels as L, C, R and the LFE last; the decoded PCM has to come out in the interleave order
  /// every consumer expects, so a wrong map shows up as two swapped tones.
  /// </summary>
  [Test]
  [Category("RoundTrip")]
  public void SurroundMatchesLibavcodec()
    => AssertMatchesReference(SurroundStream, SurroundReference, channels: 6, maxRelativeRms: 0.01);

  /// <summary>Stream info has to agree with libavcodec's view of the same stream.</summary>
  [Test]
  [Category("HappyPath")]
  public void SurroundStreamInfoMatchesLibavcodec() {
    using var src = new MemoryStream(Convert.FromBase64String(SurroundStream), writable: false);
    var info = Ac3Codec.ReadStreamInfo(src);

    Assert.Multiple(() => {
      Assert.That(info.SampleRate, Is.EqualTo(48_000));
      Assert.That(info.Channels, Is.EqualTo(6));
      Assert.That(info.Acmod, Is.EqualTo(7));
      Assert.That(info.Lfe, Is.True);
      Assert.That(info.IsEnhanced, Is.False);
      Assert.That(info.DurationSamples, Is.EqualTo(3072));
    });
  }
}
