#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using Compression.Core.Deflate;
using Compression.Core.Streams;
using FileFormat.Gzip;
using FileFormat.Zlib;
using System.Text.RegularExpressions;
using Compression.Core.Dictionary.Lz4;
using Compression.Registry;

namespace FileFormat.Mca;

/// <summary>Clean-room writer for Minecraft Anvil region containers.</summary>
public static partial class McaWriter {
  private sealed record EncodedChunk(int X, int Z, uint Timestamp, byte Compression, byte[] Payload);

  [GeneratedRegex(@"^chunk_(?<x>\d+)_(?<z>\d+)\.nbt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex ChunkNameRegex();

  public static void Write(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite) throw new ArgumentException("MCA output must be writable.", nameof(output));
    if (!string.IsNullOrEmpty(options.Password) || options.EncryptFilenames)
      throw new NotSupportedException("MCA does not define encryption.");

    var method = NormalizeMethod(options.MethodName);
    var level = options.Level ?? 6;
    if (level is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(options), "MCA compression level must be 0 through 9.");
    var chunks = new List<EncodedChunk>(inputs.Count);
    var occupied = new HashSet<int>();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var match = ChunkNameRegex().Match(Path.GetFileName(input.ArchiveName.Replace('\\', '/')) ?? string.Empty);
      if (!match.Success ||
          !int.TryParse(match.Groups["x"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var x) || x is < 0 or > 31 ||
          !int.TryParse(match.Groups["z"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var z) || z is < 0 or > 31)
        throw new ArgumentException($"MCA entry name '{input.ArchiveName}' must be chunk_X_Z.nbt with X and Z in 0..31.", nameof(inputs));
      var index = z * 32 + x;
      if (!occupied.Add(index)) throw new ArgumentException($"Duplicate MCA chunk coordinate ({x},{z}).", nameof(inputs));
      var raw = input.ReadContent();
      var payload = method switch {
        "gzip" => Compress(raw, true, level),
        "zlib" => Compress(raw, false, level),
        "lz4" => McaLz4BlockStream.Compress(raw, level >= 9 ? Lz4CompressionLevel.Max : level >= 5 ? Lz4CompressionLevel.Hc : Lz4CompressionLevel.Fast),
        _ => raw,
      };
      var timestampText = options.GetOption($"Timestamp.{x}.{z}", options.GetOption("Timestamp", "0"));
      if (!uint.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        throw new ArgumentException($"MCA timestamp '{timestampText}' is not an unsigned Unix timestamp.", nameof(options));
      chunks.Add(new EncodedChunk(x, z, timestamp, checked((byte)(method switch { "gzip" => 1, "zlib" => 2, "stored" => 3, _ => 4 })), payload));
    }

    using var region = new MemoryStream();
    region.SetLength(8192);
    region.Position = 8192;
    Span<byte> header = stackalloc byte[5];
    Span<byte> value = stackalloc byte[4];
    foreach (var chunk in chunks.OrderBy(c => c.Z).ThenBy(c => c.X)) {
      var position = checked((int)region.Position);
      var sector = checked(position / 4096);
      if (sector > 0xFFFFFF) throw new InvalidDataException("MCA region exceeds the 24-bit sector-offset limit.");
      var length = checked(chunk.Payload.Length + 1);
      var sectors = checked((length + 4 + 4095) / 4096);
      if (sectors is < 1 or > 255) throw new ArgumentException($"MCA chunk ({chunk.X},{chunk.Z}) exceeds the 255-sector in-region limit; external .mcc chunks are not written.", nameof(inputs));
      BinaryPrimitives.WriteInt32BigEndian(header, length);
      header[4] = chunk.Compression;
      region.Write(header);
      region.Write(chunk.Payload);
      region.SetLength(checked((long)(sector + sectors) * 4096));
      region.Position = region.Length;

      var index = chunk.Z * 32 + chunk.X;
      BinaryPrimitives.WriteUInt32BigEndian(value, ((uint)sector << 8) | (uint)sectors);
      region.Position = index * 4L;
      region.Write(value);
      BinaryPrimitives.WriteUInt32BigEndian(value, chunk.Timestamp);
      region.Position = 4096 + index * 4L;
      region.Write(value);
      region.Position = region.Length;
    }
    if (output.CanSeek) { output.Position = 0; output.SetLength(0); }
    region.Position = 0;
    region.CopyTo(output);
    if (output.CanSeek) output.SetLength(output.Position);
  }

  private static string NormalizeMethod(string? value) => value?.Trim().ToLowerInvariant() switch {
    null or "" or "zlib" or "deflate" => "zlib",
    "gzip" or "stored" or "store" or "lz4" => value.Trim().ToLowerInvariant() switch { "store" => "stored", var m => m },
    var other => throw new NotSupportedException($"Unsupported MCA compression method '{other}'. Choose gzip, zlib, stored, or lz4."),
  };

  private static byte[] Compress(byte[] data, bool gzip, int level) {
    using var output = new MemoryStream();
    var compressionLevel = level switch {
      0 => DeflateCompressionLevel.None,
      <= 2 => DeflateCompressionLevel.Fast,
      >= 8 => DeflateCompressionLevel.Best,
      _ => DeflateCompressionLevel.Default,
    };
    using (Stream compressor = gzip
             ? new GzipStream(output, CompressionStreamMode.Compress, compressionLevel, leaveOpen: true)
             : new ZlibStream(output, CompressionStreamMode.Compress, compressionLevel, leaveOpen: true))
      compressor.Write(data);
    return output.ToArray();
  }
}
