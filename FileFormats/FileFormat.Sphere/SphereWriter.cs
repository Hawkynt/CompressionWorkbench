#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Sphere;

/// <summary>
/// Writes a minimal little-endian linear-PCM NIST SPHERE file: the fixed
/// <c>NIST_1A</c> magic, a 1024-byte header carrying <c>channel_count</c>,
/// <c>sample_rate</c>, <c>sample_n_bytes</c> (2), <c>sample_byte_format</c>
/// (<c>01</c> = little-endian), <c>sample_coding</c> (<c>pcm</c>) and
/// <c>sample_count</c>, padded to the header size, followed by the interleaved
/// little-endian samples. The output round-trips through <see cref="SphereReader"/>.
/// </summary>
public sealed class SphereWriter {
  private const int HeaderSize = 1024;

    /// <summary>
  /// Writes the value to the supplied output.
  /// </summary>
public byte[] Write(byte[] interleavedLe, int channels, int sampleRate, int bitsPerSample) {
    var bytesPerSample = bitsPerSample / 8;
    var frameBytes = bytesPerSample * channels;
    var sampleCount = frameBytes > 0 ? interleavedLe.Length / frameBytes : 0;

    var body = new StringBuilder();
    body.Append("NIST_1A\n");
    body.Append("   1024\n");
    body.Append($"channel_count -i {channels}\n");
    body.Append($"sample_count -i {sampleCount}\n");
    body.Append($"sample_rate -i {sampleRate}\n");
    body.Append($"sample_n_bytes -i {bytesPerSample}\n");
    body.Append($"sample_byte_format -s2 01\n");
    body.Append($"sample_sig_bits -i {bitsPerSample}\n");
    body.Append($"sample_coding -s3 pcm\n");
    body.Append("end_head\n");

    var headerBytes = Encoding.ASCII.GetBytes(body.ToString());
    if (headerBytes.Length > HeaderSize)
      throw new InvalidOperationException("SPHERE header exceeds 1024 bytes.");

    var blob = new byte[HeaderSize + interleavedLe.Length];
    headerBytes.CopyTo(blob, 0);
    // Remaining header bytes stay zero (treated as padding past end_head).
    interleavedLe.CopyTo(blob, HeaderSize);
    return blob;
  }
}
