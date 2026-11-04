using Compression.Core.Dictionary.Lzma;
using Compression.Core.Entropy.Ppmd;
using Compression.Core.Streams;
using Compression.Core.Transforms;

namespace FileFormat.SevenZip;

/// <summary>
/// Decodes one 7z folder — a graph of coders whose packed-side inputs are either packed streams
/// or other coders' outputs (bind pairs) — as a stream, with memory bounded by the coders'
/// own windows rather than by the folder's size.
/// </summary>
/// <remarks>
/// <para>
/// The graph follows 7zFormat.txt and 7-Zip's 7zIn.cpp / 7zDecode.cpp: every coder has one
/// output; in-stream indices run over the coders' inputs in coder order; a bind pair connects an
/// in-stream to the output of coder <c>OutIndex</c>; the remaining in-streams take the packed
/// streams in the order the folder lists them; the folder's output is the one coder output no
/// bind pair consumes, and its size is that coder's entry in the unpack sizes.
/// </para>
/// <para>
/// Filters (branch converters, Delta, Swap, AES) and pull-style decompressors (Deflate,
/// Deflate64, BZip2, PPMd, BCJ2) are plain read streams. LZMA and LZMA2 can only push their
/// output, so wherever another coder pulls from one it runs on its own thread into a bounded
/// <see cref="SevenZipPipe"/> — or, below <see cref="InMemoryLimit"/>, straight into memory.
/// </para>
/// </remarks>
internal sealed class SevenZipFolderDecoder {
  /// <summary>Coder outputs up to this size are decoded synchronously into memory instead of through a pipe.</summary>
  internal const long InMemoryLimit = 1 << 24;

  private readonly SevenZipFolder _folder;
  private readonly Func<int, Stream> _openPackStream;
  private readonly SevenZipKeyCache _keys;
  private readonly int[] _inBase;

  /// <summary>
  /// Prepares the folder. <paramref name="openPackStream"/> opens the folder's <c>n</c>-th packed
  /// stream (in the folder's own listing order); <paramref name="keys"/> supplies AES keys.
  /// </summary>
  public SevenZipFolderDecoder(SevenZipFolder folder, Func<int, Stream> openPackStream, SevenZipKeyCache keys) {
    this._folder = folder;
    this._openPackStream = openPackStream;
    this._keys = keys;
    folder.Validate();
    this._inBase = new int[folder.Coders.Count];
    for (int i = 0, s = 0; i < folder.Coders.Count; s += folder.Coders[i].NumInStreams, ++i)
      this._inBase[i] = s;
  }

  /// <summary>The folder's output as a read stream of exactly <see cref="SevenZipFolder.MainUnpackSize"/> bytes.</summary>
  public Stream OpenMain() => this.Pull(this._folder.MainCoderIndex);

  /// <summary>Decodes the whole folder into <paramref name="sink"/>.</summary>
  public void DecodeMain(Stream sink) => this.Push(this._folder.MainCoderIndex, sink);

  private long OutSize(int coder) => this._folder.UnpackSizes[coder];

  /// <summary>The stream feeding input <paramref name="j"/> of <paramref name="coder"/>.</summary>
  private Stream OpenInput(int coder, int j) {
    var inIndex = this._inBase[coder] + j;
    foreach (var (bindIn, bindOut) in this._folder.BindPairs)
      if (bindIn == inIndex)
        return this.Pull(bindOut);

    var packIndex = Array.IndexOf(this._folder.PackedStreams, inIndex);
    if (packIndex < 0)
      throw new InvalidDataException($"7z: coder input {inIndex} is neither bound nor packed.");
    return new BufferedStream(this._openPackStream(packIndex), 1 << 16);
  }

  private Stream[] OpenInputs(int coder) {
    var count = this._folder.Coders[coder].NumInStreams;
    var inputs = new Stream[count];
    for (var j = 0; j < count; ++j)
      inputs[j] = this.OpenInput(coder, j);
    return inputs;
  }

  /// <summary>The output of <paramref name="coder"/> as a read stream.</summary>
  private Stream Pull(int coder) {
    var c = this._folder.Coders[coder];
    var size = this.OutSize(coder);
    if (!IsPushOnly(c))
      return this.OpenPullCoder(coder, c, size);

    if (size <= InMemoryLimit) {
      var memory = new MemoryStream((int)Math.Max(size, 0));
      this.Push(coder, memory);
      memory.Position = 0;
      return memory;
    }
    return SevenZipPipe.Start(sink => this.Push(coder, sink), size);
  }

  /// <summary>Writes the output of <paramref name="coder"/> into <paramref name="sink"/>, checking its size.</summary>
  private void Push(int coder, Stream sink) {
    var c = this._folder.Coders[coder];
    var size = this.OutSize(coder);
    var limited = new SevenZipLimitedWriteStream(sink, size);

    if (IsPushOnly(c))
      this.RunPushCoder(coder, c, size, limited);
    else if (this.CreateFilter(c) is { } filter && c.NumInStreams == 1 && this.InputIsBound(coder, 0, out var source)) {
      // A filter above another coder: let that coder push through the filter, no pipe needed.
      var through = new SevenZipFilterWriteStream(limited, filter);
      this.Push(source, through);
      through.Complete();
      (filter as IDisposable)?.Dispose();
    } else {
      using var pulled = this.Pull(coder);
      pulled.CopyTo(limited, 1 << 16);
    }

    if (limited.Written != size)
      throw new InvalidDataException($"7z: a {SevenZipCoderNames.Describe(c.CodecId)} coder produced {limited.Written} bytes, the header declares {size}.");
  }

  private bool InputIsBound(int coder, int j, out int source) {
    var inIndex = this._inBase[coder] + j;
    foreach (var (bindIn, bindOut) in this._folder.BindPairs)
      if (bindIn == inIndex) {
        source = bindOut;
        return true;
      }
    source = -1;
    return false;
  }

  private static bool IsPushOnly(SevenZipCoder c)
    => c.Is(SevenZipConstants.CodecLzma) || c.Is(SevenZipConstants.CodecLzma2);

  private void RunPushCoder(int coder, SevenZipCoder c, long size, Stream sink) {
    using var input = this.OpenInputs(coder)[0];
    if (c.Is(SevenZipConstants.CodecLzma2)) {
      var props = c.Properties ?? [];
      if (props.Length != 1 || props[0] > 40)
        throw new InvalidDataException("7z: invalid LZMA2 properties.");
      var dictionary = props[0] == 40 ? int.MaxValue : SevenZipCoderNames.Lzma2DictionarySize(props[0]);
      new Lzma2Decoder(input, dictionary).Decode(sink);
      return;
    }

    if (c.Properties is not { Length: >= 5 } lzmaProps)
      throw new InvalidDataException("7z: LZMA coder without its five property bytes.");
    new LzmaDecoder(input, lzmaProps, size).Decode(sink);
  }

  private ISevenZipFilter? CreateFilter(SevenZipCoder c) {
    if (c.Is(SevenZipConstants.CodecCopy))
      return SevenZipCopyFilter.Instance;
    if (c.Is(SevenZipConstants.CodecDelta)) {
      var props = c.Properties ?? [];
      if (props.Length != 1)
        throw new InvalidDataException("7z: the Delta coder needs one property byte.");
      return new SevenZipDeltaFilter(props[0] + 1, decoding: true);
    }
    if (c.Is(SevenZipConstants.CodecSwap2))
      return new SevenZipSwapFilter(2);
    if (c.Is(SevenZipConstants.CodecSwap4))
      return new SevenZipSwapFilter(4);
    if (c.Is(SevenZipConstants.CodecAes))
      return this._keys.CreateDecryptor(c.Properties);
    if (SevenZipCoderNames.BranchArchitectureOf(c.CodecId) is { } architecture)
      return new SevenZipBranchFilter(architecture, decoding: true, BranchStartOffset(c, architecture));
    return null;
  }

  /// <summary>
  /// The start offset of a branch converter: no properties mean 0, otherwise a little-endian
  /// UInt32; 7-Zip refuses an ARM64 or RISC-V offset that is not instruction aligned.
  /// </summary>
  private static uint BranchStartOffset(SevenZipCoder coder, BranchArchitecture architecture) {
    var props = coder.Properties;
    if (props == null || props.Length == 0)
      return 0;
    if (props.Length != 4)
      throw new NotSupportedException($"7z: unsupported branch-filter property size {props.Length}.");
    var pc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(props);
    var alignment = architecture switch { BranchArchitecture.Arm64 => 4u, BranchArchitecture.RiscV => 2u, _ => 1u };
    if (pc % alignment != 0)
      throw new NotSupportedException($"7z: unaligned branch-filter start offset 0x{pc:X8}.");
    return pc;
  }

  private Stream OpenPullCoder(int coder, SevenZipCoder c, long size) {
    if (this.CreateFilter(c) is { } filter) {
      if (c.NumInStreams != 1)
        throw new InvalidDataException("7z: a filter coder takes exactly one input.");
      return new SevenZipFilterReadStream(this.OpenInput(coder, 0), filter, size);
    }

    if (c.Is(SevenZipConstants.CodecBcj2)) {
      if (c.NumInStreams != 4)
        throw new InvalidDataException("7z: the BCJ2 coder takes four inputs.");
      var inputs = this.OpenInputs(coder);
      return new Bcj2Filter.DecodeStream(inputs[0], inputs[1], inputs[2], inputs[3], size);
    }

    var input = this.OpenInput(coder, 0);
    if (c.Is(SevenZipConstants.CodecDeflate))
      return new SevenZipPullStream(new Compression.Core.Deflate.DeflateDecompressor(input).Decompress, size);
    if (c.Is(SevenZipConstants.CodecDeflate64)) {
      var d64 = new Compression.Core.Deflate.Deflate64Decompressor(input);
      return new SevenZipPullStream(span => {
        var buffer = new byte[span.Length];
        var n = d64.Decompress(buffer, 0, buffer.Length);
        buffer.AsSpan(0, n).CopyTo(span);
        return n;
      }, size);
    }
    if (c.Is(SevenZipConstants.CodecBzip2)) {
      var bz2 = new FileFormat.Bzip2.Bzip2Stream(input, CompressionStreamMode.Decompress);
      return new SevenZipPullStream(bz2.Read, size);
    }
    if (c.Is(SevenZipConstants.CodecPpmd))
      return OpenPpmd(c, input, size);
    if (c.Is(SevenZipConstants.CodecZstd))
      return new MemoryStream(SevenZipZstdFamilyCoders.DecodeZstd(ReadAll(input)), writable: false);
    if (c.Is(SevenZipConstants.CodecBrotli))
      return new MemoryStream(SevenZipZstdFamilyCoders.DecodeBrotli(ReadAll(input)), writable: false);
    if (c.Is(SevenZipConstants.CodecLz4))
      return new MemoryStream(SevenZipZstdFamilyCoders.DecodeLz4(ReadAll(input)), writable: false);

    throw new NotSupportedException($"7z: unsupported coder {SevenZipCoderNames.Describe(c.CodecId)}.");
  }

  /// <summary>PPMd (7z variant H) decoding hook; the model decodes exactly the declared size.</summary>
  private static Stream OpenPpmd(SevenZipCoder c, Stream input, long size) {
    if (c.Properties is not { Length: >= 5 } props)
      throw new InvalidDataException("7z: PPMd coder without its five property bytes.");
    int order = props[0];
    var memory = props[1] | (props[2] << 8) | (props[3] << 16) | (props[4] << 24);
    var rangeDecoder = new PpmdRangeDecoder(input);
    var model = new PpmdModelH(order, memory);
    return new SevenZipPullStream(span => {
      for (var i = 0; i < span.Length; ++i)
        span[i] = model.DecodeSymbol(rangeDecoder);
      return span.Length;
    }, size);
  }

  private static byte[] ReadAll(Stream input) {
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    return memory.ToArray();
  }
}

/// <summary>Adapts a "fill this span, return how many" decoder to a read stream of a fixed length.</summary>
internal sealed class SevenZipPullStream(SevenZipPullStream.Fill fill, long length) : Stream {
  /// <summary>Fills the span, returning the number of bytes produced (0 at the decoder's end).</summary>
  public delegate int Fill(Span<byte> destination);

  private long _position;

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => length;
  public override long Position { get => this._position; set => throw new NotSupportedException(); }

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    var remaining = length - this._position;
    if (remaining <= 0 || buffer.IsEmpty)
      return 0;
    if (buffer.Length > remaining)
      buffer = buffer[..(int)remaining];
    var n = fill(buffer);
    if (n <= 0)
      throw new InvalidDataException("7z: a coder's stream ended before its declared size.");
    this._position += n;
    return n;
  }

  public override void Flush() { }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
