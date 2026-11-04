using System.Security.Cryptography;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.Streams;
using Compression.Core.Transforms;

namespace FileFormat.SevenZip;

/// <summary>
/// One file the writer will store: its bytes in memory, or a way to open them when its solid
/// block is encoded.
/// </summary>
internal sealed record SevenZipSource(SevenZipEntry Entry, byte[]? Data, Func<Stream>? Open, long Size);

public sealed partial class SevenZipWriter {
  /// <summary>
  /// Whether <paramref name="plan"/> is encoded as a stream: when a file of it is not in memory or
  /// the block is too large to assemble, and its coders can stream — LZMA2, Deflate, BZip2 or
  /// Copy, any filter but BCJ2.
  /// </summary>
  private bool MustStream(PlannedBlock plan) {
    var large = plan.Files.Exists(static f => f.Data == null) || plan.Files.Sum(static f => f.Size) > InMemoryBlockLimit;
    return large
      && plan.Codec is SevenZipCodec.Lzma2 or SevenZipCodec.Deflate or SevenZipCodec.BZip2 or SevenZipCodec.Copy
      && plan.Filter != SevenZipFilter.Bcj2;
  }

  /// <summary>
  /// Encodes <paramref name="plan"/> straight into the archive: the files are read one after
  /// another (each CRC computed on the way), filtered, compressed and — with a password —
  /// encrypted as they stream, so memory stays at the coders' windows. The folder has the same
  /// layout <see cref="EncodeBlock"/> writes. Returns the packed size and the folder.
  /// </summary>
  private (long PackedSize, SevenZipFolder Folder) StreamBlock(PlannedBlock plan) {
    var total = plan.Files.Sum(static f => f.Size);
    var packed = new SevenZipCountingStream(this._stream);
    Stream sink = packed;
    SevenZipAesEncryptStream? aes = null;
    if (this._password != null) {
      aes = this.CreateEncryptor(packed);
      sink = aes;
    }
    var compressedSink = new SevenZipCountingStream(sink);

    using var source = new SevenZipSourceStream(plan.Files);
    Stream input = source;
    SevenZipCoder? filterCoder = null;
    if (plan.Filter != SevenZipFilter.None)
      input = new SevenZipFilterReadStream(source, this.EncodingFilter(plan.Filter, out filterCoder), total);

    var compressor = CompressStreaming(input, total, compressedSink, plan.Codec, plan.DictionarySize);
    if (source.Position != total)
      throw new IOException("7z: a file delivered fewer bytes than its declared size while it was archived.");
    aes?.Complete();

    var folder = new SevenZipFolder { PackedStreams = [0] };
    var sizes = new List<long>();
    if (aes != null) {
      folder.Coders.Add(aes.Coder);
      sizes.Add(compressedSink.Count);
    }
    folder.Coders.Add(compressor);
    sizes.Add(total);
    if (filterCoder != null) {
      folder.Coders.Add(filterCoder);
      sizes.Add(total);
    }
    for (var i = 1; i < folder.Coders.Count; ++i)
      folder.BindPairs.Add((i, i - 1));
    folder.UnpackSizes = [.. sizes];
    return (packed.Count, folder);
  }

  private static SevenZipCoder CompressStreaming(Stream input, long length, Stream output, SevenZipCodec codec, int dictionarySize) {
    switch (codec) {
      case SevenZipCodec.Lzma2: {
        var encoder = new Lzma2Encoder(dictionarySize);
        encoder.Encode(output, input, length);
        return new SevenZipCoder { CodecId = SevenZipConstants.CodecLzma2.ToArray(), Properties = [encoder.DictionarySizeByte] };
      }
      case SevenZipCodec.Deflate:
        using (var deflate = new RawDeflateStream(output, CompressionStreamMode.Compress, leaveOpen: true))
          input.CopyTo(deflate, 1 << 16);
        return new SevenZipCoder { CodecId = SevenZipConstants.CodecDeflate.ToArray() };
      case SevenZipCodec.BZip2:
        using (var bz2 = new FileFormat.Bzip2.Bzip2Stream(output, CompressionStreamMode.Compress, leaveOpen: true))
          input.CopyTo(bz2, 1 << 16);
        return new SevenZipCoder { CodecId = SevenZipConstants.CodecBzip2.ToArray() };
      case SevenZipCodec.Copy:
        input.CopyTo(output, 1 << 16);
        return new SevenZipCoder { CodecId = SevenZipConstants.CodecCopy.ToArray() };
      default:
        throw new NotSupportedException($"7z: {codec} cannot be streamed.");
    }
  }

  /// <summary>The encoding side of a length-preserving filter, as a streaming 7z filter, with its coder.</summary>
  private ISevenZipFilter EncodingFilter(SevenZipFilter filter, out SevenZipCoder coder) {
    (BranchArchitecture? branch, byte[] id) = filter switch {
      SevenZipFilter.BcjX86 => (BranchArchitecture.X86, SevenZipConstants.CodecBcj.ToArray()),
      SevenZipFilter.BcjArm => (BranchArchitecture.Arm, SevenZipConstants.CodecBcjArm.ToArray()),
      SevenZipFilter.BcjArmThumb => (BranchArchitecture.ArmThumb, SevenZipConstants.CodecBcjArmThumb.ToArray()),
      SevenZipFilter.BcjPowerPC => (BranchArchitecture.PowerPC, SevenZipConstants.CodecBcjPpc.ToArray()),
      SevenZipFilter.BcjSparc => (BranchArchitecture.Sparc, SevenZipConstants.CodecBcjSparc.ToArray()),
      SevenZipFilter.BcjIA64 => (BranchArchitecture.IA64, SevenZipConstants.CodecBcjIa64.ToArray()),
      _ => ((BranchArchitecture?)null, Array.Empty<byte>()),
    };
    if (branch is { } architecture) {
      coder = new SevenZipCoder { CodecId = id };
      return new SevenZipBranchFilter(architecture, decoding: false, 0);
    }
    switch (filter) {
      case SevenZipFilter.Copy:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecCopy.ToArray() };
        return SevenZipCopyFilter.Instance;
      case SevenZipFilter.Delta:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecDelta.ToArray(), Properties = [(byte)(this._deltaDistance - 1)] };
        return new SevenZipDeltaFilter(this._deltaDistance, decoding: false);
      case SevenZipFilter.Swap2:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecSwap2.ToArray() };
        return new SevenZipSwapFilter(2);
      case SevenZipFilter.Swap4:
        coder = new SevenZipCoder { CodecId = SevenZipConstants.CodecSwap4.ToArray() };
        return new SevenZipSwapFilter(4);
      default:
        throw new NotSupportedException($"7z: the {filter} filter cannot be streamed.");
    }
  }

  /// <summary>An AES coder over <paramref name="output"/> under the archive key, with a fresh IV.</summary>
  private SevenZipAesEncryptStream CreateEncryptor(Stream output) {
    var (salt, key) = this._aesKey ?? throw new InvalidOperationException("7z: no key derived.");
    var iv = RandomNumberGenerator.GetBytes(16);
    var props = new byte[2 + salt.Length + iv.Length];
    props[0] = (byte)(AesCyclesPower | 0x80 | 0x40);
    props[1] = (byte)(((salt.Length - 1) << 4) | (iv.Length - 1));
    salt.CopyTo(props, 2);
    iv.CopyTo(props, 2 + salt.Length);
    return new SevenZipAesEncryptStream(output, key, iv, new SevenZipCoder { CodecId = SevenZipConstants.CodecAes.ToArray(), Properties = props });
  }
}

/// <summary>
/// The files of a solid block as one read stream: each opened in turn, exactly its declared size
/// read, its CRC-32 computed and stored on its entry.
/// </summary>
internal sealed class SevenZipSourceStream(IReadOnlyList<SevenZipSource> files) : Stream {
  private int _index = -1;
  private Stream? _current;
  private long _left;
  private readonly Crc32 _crc = new();
  private long _position;

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => files.Sum(static f => f.Size);
  public override long Position { get => this._position; set => throw new NotSupportedException(); }

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    while (this._left == 0) {
      if (++this._index >= files.Count)
        return 0;
      var file = files[this._index];
      this._current = file.Data != null ? new MemoryStream(file.Data, writable: false) : file.Open!();
      this._left = file.Size;
      this._crc.Reset();
      if (this._left == 0)
        this.Close(this._index);
    }

    var n = this._current!.Read(buffer[..(int)Math.Min(buffer.Length, this._left)]);
    if (n <= 0)
      throw new IOException($"7z: '{files[this._index].Entry.Name}' ended {this._left} bytes before its declared size.");
    this._crc.Update(buffer[..n]);
    this._left -= n;
    this._position += n;
    if (this._left == 0)
      this.Close(this._index); // the CRC is final as soon as the last byte is read
    return n;
  }

  private void Close(int index) {
    if (index < 0 || index >= files.Count || this._current == null)
      return;
    files[index].Entry.Crc = this._crc.Value;
    this._current.Dispose();
    this._current = null;
  }

  protected override void Dispose(bool disposing) {
    if (disposing)
      this._current?.Dispose();
    base.Dispose(disposing);
  }

  public override void Flush() { }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Counts what passes through to <paramref name="inner"/>.</summary>
internal sealed class SevenZipCountingStream(Stream inner) : Stream {
  public long Count { get; private set; }
  public override bool CanRead => false;
  public override bool CanSeek => false;
  public override bool CanWrite => true;
  public override long Length => this.Count;
  public override long Position { get => this.Count; set => throw new NotSupportedException(); }
  public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));
  public override void WriteByte(byte value) => this.Write([value]);
  public override void Write(ReadOnlySpan<byte> buffer) {
    inner.Write(buffer);
    this.Count += buffer.Length;
  }
  public override void Flush() => inner.Flush();
  public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// AES-256-CBC encryption as a write stream, zero-padding the last block the way 7-Zip does
/// (the coder's unpack size cuts the padding off on reading). <see cref="Complete"/> ends it.
/// </summary>
internal sealed class SevenZipAesEncryptStream : Stream {
  private readonly Stream _output;
  private readonly Aes _aes;
  private readonly ICryptoTransform _transform;
  private readonly byte[] _buffer = new byte[1 << 16];
  private int _filled;
  private bool _completed;

  public SevenZipAesEncryptStream(Stream output, byte[] key, byte[] iv, SevenZipCoder coder) {
    this._output = output;
    this.Coder = coder;
    this._aes = Aes.Create();
    this._aes.Mode = CipherMode.CBC;
    this._aes.Padding = PaddingMode.None;
    this._aes.Key = key;
    this._aes.IV = iv;
    this._transform = this._aes.CreateEncryptor();
  }

  /// <summary>The 7z AES coder (properties: cycles, salt, IV) describing this encryption.</summary>
  public SevenZipCoder Coder { get; }

  public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));
  public override void WriteByte(byte value) => this.Write([value]);

  public override void Write(ReadOnlySpan<byte> data) {
    while (!data.IsEmpty) {
      var n = Math.Min(data.Length, this._buffer.Length - this._filled);
      data[..n].CopyTo(this._buffer.AsSpan(this._filled));
      this._filled += n;
      data = data[n..];
      if (this._filled == this._buffer.Length)
        this.Drain(this._buffer.Length);
    }
  }

  private void Drain(int count) {
    this._transform.TransformBlock(this._buffer, 0, count, this._buffer, 0);
    this._output.Write(this._buffer, 0, count);
    Buffer.BlockCopy(this._buffer, count, this._buffer, 0, this._filled - count);
    this._filled -= count;
  }

  /// <summary>Pads the last block with zeros and writes it.</summary>
  public void Complete() {
    if (this._completed)
      return;
    this._completed = true;
    var padded = (this._filled + 15) & ~15;
    this._buffer.AsSpan(this._filled, padded - this._filled).Clear();
    this._filled = padded;
    if (padded > 0)
      this.Drain(padded);
    this._transform.Dispose();
    this._aes.Dispose();
  }

  public override bool CanRead => false;
  public override bool CanSeek => false;
  public override bool CanWrite => true;
  public override long Length => throw new NotSupportedException();
  public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
  public override void Flush() => this._output.Flush();
  public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
}
