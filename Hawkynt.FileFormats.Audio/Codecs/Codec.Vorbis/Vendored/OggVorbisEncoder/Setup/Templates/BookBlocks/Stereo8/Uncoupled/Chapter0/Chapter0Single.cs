namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo8.Uncoupled.Chapter0;

/// <summary>
/// Represents a chapter 0 single.
/// </summary>
public class Chapter0Single : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         4, 7,11, 9,12, 8, 7,10, 6, 4, 5, 5, 7, 5, 6,16,
         9, 5, 5, 6, 7, 7, 9,16, 7, 4, 6, 5, 7, 5, 7,17,
        10, 7, 7, 8, 7, 7, 8,18, 7, 5, 6, 4, 5, 4, 5,15,
         7, 6, 7, 5, 6, 4, 5,15,12,13,18,12,17,11, 9,17,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)0;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = 0;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 0;
    /// <summary>
    /// Gets the quant.
    /// </summary>
    public int Quant { get; } = 0;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
    public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
    public int[] QuantList { get; } = null;
}
