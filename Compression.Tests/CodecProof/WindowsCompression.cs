using System.Runtime.InteropServices;

namespace Compression.Tests.CodecProof;

/// <summary>
/// Microsoft's own compressors as test oracles: ntdll's RtlCompressBuffer / RtlDecompressBufferEx
/// (LZNT1, XPRESS, XPRESS Huffman, the formats of [MS-XCA]) and cabinet.dll's Compression API
/// in raw mode. Windows only; callers skip elsewhere.
/// </summary>
internal static class WindowsCompression {
  /// <summary>Skips the calling test when not on Windows.</summary>
  public static void Require() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("The Windows compression APIs are the oracle here.");
  }

  /// <summary>ntdll's buffer compression (the NTFS / hibernation-file codecs).</summary>
  public static class Rtl {
    public const ushort Lznt1 = 2;
    public const ushort Xpress = 3;
    public const ushort XpressHuffman = 4;
    public const ushort EngineMaximum = 0x100;

    /// <summary>STATUS_BUFFER_ALL_ZEROS: the input was all zero bytes and nothing was written.</summary>
    public const uint StatusBufferAllZeros = 0x117;

    [DllImport("ntdll.dll")]
    private static extern uint RtlGetCompressionWorkSpaceSize(ushort format, out uint bufferWorkspace, out uint fragmentWorkspace);

    [DllImport("ntdll.dll")]
    private static extern uint RtlCompressBuffer(ushort format, byte[] source, uint sourceLength, byte[] destination, uint destinationLength, uint chunkSize, out uint finalSize, byte[] workspace);

    [DllImport("ntdll.dll")]
    private static extern uint RtlDecompressBufferEx(ushort format, byte[] destination, uint destinationLength, byte[] source, uint sourceLength, out uint finalSize, byte[] workspace);

    /// <summary>Compresses with RtlCompressBuffer; null when Windows reports the input all zeros.</summary>
    public static byte[]? Compress(ushort format, byte[] data) {
      Assert.That(RtlGetCompressionWorkSpaceSize(format, out var workspaceSize, out _), Is.Zero);
      var output = new byte[data.Length + data.Length / 8 + 4096];
      var status = RtlCompressBuffer(format, data, (uint)data.Length, output, (uint)output.Length, 4096, out var size, new byte[workspaceSize]);
      if (status == StatusBufferAllZeros)
        return null;

      Assert.That(status, Is.Zero, $"RtlCompressBuffer 0x{status:X8}");
      return output[..(int)size];
    }

    /// <summary>Decompresses with RtlDecompressBufferEx.</summary>
    public static byte[] Decompress(ushort format, byte[] packed, int size) {
      Assert.That(RtlGetCompressionWorkSpaceSize(format, out var workspaceSize, out _), Is.Zero);
      var output = new byte[size];
      var status = RtlDecompressBufferEx(format, output, (uint)size, packed, (uint)packed.Length, out var final, new byte[workspaceSize]);
      Assert.That(status, Is.Zero, $"RtlDecompressBufferEx 0x{status:X8}");
      return output[..(int)final];
    }
  }

  /// <summary>
  /// cabinet.dll's Compression API with COMPRESS_RAW. Raw mode takes one block of at most 32 KB and
  /// stores a block that does not shrink as plain bytes, so only compressible inputs say anything
  /// about the codec.
  /// </summary>
  public static class CompressionApi {
    public const uint MsZip = 2;
    public const uint Xpress = 3;
    public const uint XpressHuffman = 4;
    public const uint Lzms = 5;
    private const uint Raw = 1u << 29;

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool CreateCompressor(uint algorithm, IntPtr allocationRoutines, out IntPtr handle);

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool Compress(IntPtr handle, byte[] source, nuint sourceSize, byte[] destination, nuint destinationSize, out nuint compressedSize);

    [DllImport("cabinet.dll")]
    private static extern bool CloseCompressor(IntPtr handle);

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool CreateDecompressor(uint algorithm, IntPtr allocationRoutines, out IntPtr handle);

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool Decompress(IntPtr handle, byte[] source, nuint sourceSize, byte[] destination, nuint destinationSize, out nuint decompressedSize);

    [DllImport("cabinet.dll")]
    private static extern bool CloseDecompressor(IntPtr handle);

    /// <summary>Compresses one block.</summary>
    public static byte[] Compress(uint algorithm, byte[] data) {
      Assert.That(CreateCompressor(algorithm | Raw, IntPtr.Zero, out var handle), Is.True, $"CreateCompressor {Marshal.GetLastWin32Error()}");
      try {
        var output = new byte[data.Length + data.Length / 4 + 65536];
        Assert.That(Compress(handle, data, (nuint)data.Length, output, (nuint)output.Length, out var size), Is.True, $"Compress {Marshal.GetLastWin32Error()}");
        return output[..(int)size];
      } finally {
        CloseCompressor(handle);
      }
    }

    /// <summary>Decompresses one block of known size.</summary>
    public static byte[] Decompress(uint algorithm, byte[] packed, int size) {
      Assert.That(CreateDecompressor(algorithm | Raw, IntPtr.Zero, out var handle), Is.True, $"CreateDecompressor {Marshal.GetLastWin32Error()}");
      try {
        var output = new byte[size];
        Assert.That(Decompress(handle, packed, (nuint)packed.Length, output, (nuint)size, out var final), Is.True, $"Decompress {Marshal.GetLastWin32Error()}");
        return output[..(int)final];
      } finally {
        CloseDecompressor(handle);
      }
    }
  }
}
