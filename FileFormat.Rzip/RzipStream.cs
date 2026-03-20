using Compression.Core.Dictionary.Rzip;
using Compression.Core.Streams;
using FileFormat.Bzip2;

namespace FileFormat.Rzip;

/// <summary>
/// Provides static methods for compressing and decompressing data in the RZIP format.
/// RZIP uses long-distance rolling-hash block matching followed by bzip2 compression
/// of the residual token stream.
/// </summary>
/// <remarks>
/// <para>
/// Format layout:
/// <code>
/// Header: "RZIP" (4 bytes) + major (1) + minor (1) + original_size (4, big-endian)
/// Chunks (repeated):
///   compressed_size (4 bytes, big-endian)
///   bzip2-compressed token stream (compressed_size bytes)
///
/// Token stream (after bzip2 decompression):
///   tag=0 (literal):  length (2 bytes BE) + literal bytes
///   tag=1 (match):    length (2 bytes BE) + offset (4 bytes BE, absolute position in output)
/// </code>
/// </para>
/// </remarks>
public static class RzipStream {

  /// <summary>
  /// Decompresses an RZIP stream.
  /// </summary>
  /// <param name="input">The input stream containing RZIP-compressed data.</param>
  /// <param name="output">The output stream to write decompressed data to.</param>
  /// <exception cref="InvalidDataException">The stream does not contain valid RZIP data.</exception>
  public static void Decompress(Stream input, Stream output) {
    // Read header
    Span<byte> header = stackalloc byte[RzipConstants.HeaderSize];
    ReadExactly(input, header);

    // Verify magic
    if (!header[..4].SequenceEqual(RzipConstants.Magic))
      throw new InvalidDataException("Invalid RZIP magic bytes.");

    byte major = header[4];
    byte minor = header[5];

    // We accept version 2.x; warn on mismatch but don't fail for minor differences
    if (major != RzipConstants.VersionMajor)
      throw new InvalidDataException($"Unsupported RZIP version: {major}.{minor}");

    uint originalSize = ReadUInt32BigEndian(header[6..]);

    // Accumulate all output for back-reference resolution
    using var outputBuffer = new MemoryStream((int)originalSize);

    Span<byte> chunkLenBuf = stackalloc byte[4];

    while (outputBuffer.Length < originalSize) {
      // Read chunk compressed length
      int bytesRead = ReadBytes(input, chunkLenBuf);
      if (bytesRead < 4)
        break; // end of stream

      int compressedLen = (int)ReadUInt32BigEndian(chunkLenBuf);
      if (compressedLen <= 0)
        break;

      // Read compressed chunk data
      byte[] compressedData = new byte[compressedLen];
      ReadExactly(input, compressedData);

      // Decompress with bzip2
      byte[] tokenData;
      using (var compressedStream = new MemoryStream(compressedData))
      using (var bzip2 = new Bzip2Stream(compressedStream, CompressionStreamMode.Decompress, leaveOpen: true))
      using (var tokenStream = new MemoryStream()) {
        bzip2.CopyTo(tokenStream);
        tokenData = tokenStream.ToArray();
      }

      // Process tokens
      int pos = 0;
      while (pos < tokenData.Length) {
        byte tag = tokenData[pos++];

        if (tag == RzipConstants.TagLiteral) {
          if (pos + 2 > tokenData.Length)
            throw new InvalidDataException("Truncated literal token.");

          int length = (tokenData[pos] << 8) | tokenData[pos + 1];
          pos += 2;

          if (pos + length > tokenData.Length)
            throw new InvalidDataException("Literal data exceeds token stream.");

          outputBuffer.Write(tokenData, pos, length);
          pos += length;
        } else if (tag == RzipConstants.TagMatch) {
          if (pos + 6 > tokenData.Length)
            throw new InvalidDataException("Truncated match token.");

          int length = (tokenData[pos] << 8) | tokenData[pos + 1];
          pos += 2;

          int offset = (int)ReadUInt32BigEndian(tokenData.AsSpan(pos, 4));
          pos += 4;

          // Copy from already-decompressed output
          byte[] outputBytes = outputBuffer.GetBuffer();
          long currentLen = outputBuffer.Length;

          if (offset < 0 || offset + length > currentLen)
            throw new InvalidDataException($"Match offset {offset}+{length} exceeds output size {currentLen}.");

          // Must write byte-by-byte in case of overlapping references
          long savedPos = outputBuffer.Position;
          outputBuffer.Seek(0, SeekOrigin.End);
          for (int i = 0; i < length; i++)
            outputBuffer.WriteByte(outputBytes[offset + i]);
        } else {
          throw new InvalidDataException($"Unknown token tag: {tag}");
        }
      }
    }

    // Write final output
    outputBuffer.Position = 0;
    outputBuffer.CopyTo(output);
  }

  /// <summary>
  /// Compresses data into RZIP format.
  /// </summary>
  /// <param name="input">The input stream containing data to compress.</param>
  /// <param name="output">The output stream to write RZIP-compressed data to.</param>
  /// <param name="blockSize">The chunk size for processing. Default is 900 KB.</param>
  /// <param name="hashBlockSize">The block size for rolling hash matching. Default is 4096.</param>
  public static void Compress(Stream input, Stream output,
    int blockSize = RzipConstants.DefaultBlockSize, int hashBlockSize = 4096) {
    // Read all input
    byte[] inputData;
    if (input is MemoryStream ms && ms.TryGetBuffer(out var seg))
      inputData = seg.ToArray();
    else {
      using var temp = new MemoryStream();
      input.CopyTo(temp);
      inputData = temp.ToArray();
    }

    // Write header
    Span<byte> header = stackalloc byte[RzipConstants.HeaderSize];
    RzipConstants.Magic.CopyTo(header);
    header[4] = RzipConstants.VersionMajor;
    header[5] = RzipConstants.VersionMinor;
    WriteUInt32BigEndian(header[6..], (uint)inputData.Length);
    output.Write(header);

    // Process in chunks
    var matcher = new RollingHashMatcher(hashBlockSize);
    // allPrevious accumulates all data written so far (for long-distance matching)
    using var allPrevious = new MemoryStream();
    int inputPos = 0;

    while (inputPos < inputData.Length) {
      int chunkLen = Math.Min(blockSize, inputData.Length - inputPos);
      byte[] chunk = new byte[chunkLen];
      Array.Copy(inputData, inputPos, chunk, 0, chunkLen);

      // Build token stream for this chunk
      using var tokenStream = new MemoryStream();

      byte[] previousData = allPrevious.ToArray();
      if (previousData.Length >= hashBlockSize) {
        // Index all previously written data and find matches
        matcher.Index(previousData);
        var tokens = matcher.FindMatches(chunk, previousData);

        foreach (var token in tokens) {
          if (token.IsLiteral) {
            // May need to split literals longer than 65535
            int litPos = token.InputOffset;
            int remaining = token.Length;
            while (remaining > 0) {
              int segLen = Math.Min(remaining, 65535);
              tokenStream.WriteByte(RzipConstants.TagLiteral);
              tokenStream.WriteByte((byte)(segLen >> 8));
              tokenStream.WriteByte((byte)(segLen & 0xFF));
              tokenStream.Write(chunk, litPos, segLen);
              litPos += segLen;
              remaining -= segLen;
            }
          } else {
            // Match — also split if longer than 65535
            int matchRemaining = token.Length;
            int refOff = token.ReferenceOffset;
            byte[] offBuf = new byte[4];
            while (matchRemaining > 0) {
              int segLen = Math.Min(matchRemaining, 65535);
              tokenStream.WriteByte(RzipConstants.TagMatch);
              tokenStream.WriteByte((byte)(segLen >> 8));
              tokenStream.WriteByte((byte)(segLen & 0xFF));
              WriteUInt32BigEndian(offBuf, (uint)refOff);
              tokenStream.Write(offBuf);
              refOff += segLen;
              matchRemaining -= segLen;
            }
          }
        }
      } else {
        // No previous data to match against — emit all as literal
        int remaining = chunkLen;
        int litPos = 0;
        while (remaining > 0) {
          int segLen = Math.Min(remaining, 65535);
          tokenStream.WriteByte(RzipConstants.TagLiteral);
          tokenStream.WriteByte((byte)(segLen >> 8));
          tokenStream.WriteByte((byte)(segLen & 0xFF));
          tokenStream.Write(chunk, litPos, segLen);
          litPos += segLen;
          remaining -= segLen;
        }
      }

      // Compress token stream with bzip2
      byte[] tokenData = tokenStream.ToArray();
      byte[] compressedData;
      using (var compressedStream = new MemoryStream())
      {
        using (var bzip2 = new Bzip2Stream(compressedStream, CompressionStreamMode.Compress, leaveOpen: true))
          bzip2.Write(tokenData, 0, tokenData.Length);

        compressedData = compressedStream.ToArray();
      }

      // Write chunk: compressed_length (BE) + bzip2 data
      byte[] lenBuf = new byte[4];
      WriteUInt32BigEndian(lenBuf, (uint)compressedData.Length);
      output.Write(lenBuf);
      output.Write(compressedData);

      // Accumulate for future matching
      allPrevious.Write(chunk, 0, chunkLen);
      inputPos += chunkLen;
    }
  }

  private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> buf)
    => (uint)(buf[0] << 24 | buf[1] << 16 | buf[2] << 8 | buf[3]);

  private static void WriteUInt32BigEndian(Span<byte> buf, uint value) {
    buf[0] = (byte)(value >> 24);
    buf[1] = (byte)(value >> 16);
    buf[2] = (byte)(value >> 8);
    buf[3] = (byte)value;
  }

  private static void ReadExactly(Stream stream, Span<byte> buffer) {
    int offset = 0;
    while (offset < buffer.Length) {
      int read = stream.Read(buffer[offset..]);
      if (read == 0)
        throw new InvalidDataException("Unexpected end of stream.");
      offset += read;
    }
  }

  private static void ReadExactly(Stream stream, byte[] buffer) {
    int offset = 0;
    while (offset < buffer.Length) {
      int read = stream.Read(buffer, offset, buffer.Length - offset);
      if (read == 0)
        throw new InvalidDataException("Unexpected end of stream.");
      offset += read;
    }
  }

  private static int ReadBytes(Stream stream, Span<byte> buffer) {
    int offset = 0;
    while (offset < buffer.Length) {
      int read = stream.Read(buffer[offset..]);
      if (read == 0)
        return offset;
      offset += read;
    }

    return offset;
  }
}
