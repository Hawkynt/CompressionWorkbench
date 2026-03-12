namespace Compression.Core.Dictionary.Xpress;

public static partial class XpressHuffmanDecompressor {
  /// <summary>Mutable bit-reader state passed by reference to avoid span-capture restrictions.</summary>
  private struct SpanBitReader {
    internal int InputPos;
    internal uint BitBuf;
    internal int BitsAvailable;

    internal SpanBitReader(ReadOnlySpan<byte> _) { }

    internal void AdvanceBytes(int count) => this.InputPos += count;
  }
}
