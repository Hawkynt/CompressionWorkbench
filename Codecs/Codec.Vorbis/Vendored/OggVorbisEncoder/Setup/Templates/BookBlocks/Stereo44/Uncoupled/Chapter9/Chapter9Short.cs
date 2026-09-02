namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter9;

/// <summary>
/// Represents a chapter 9 short.
/// </summary>
public class Chapter9Short : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         9,16,18,18,17,17,17,17,17,17, 5, 8,11,12,11,12,
        17,17,16,16, 6, 6, 8, 8, 9,10,14,15,16,16, 6, 7,
         7, 4, 6, 9,13,16,16,16, 6, 6, 7, 4, 5, 8,11,15,
        17,16, 7, 6, 7, 6, 6, 8, 9,10,14,16,11, 8, 8, 7,
         6, 6, 3, 4,10,15,14,12,12,10, 5, 6, 3, 3, 8,13,
        15,17,15,11, 6, 8, 6, 6, 9,14,17,15,15,12, 8,10,
         9, 9,12,15,
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
