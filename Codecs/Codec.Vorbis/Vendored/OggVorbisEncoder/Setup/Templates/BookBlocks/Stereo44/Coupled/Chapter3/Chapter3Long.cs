namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter3;

/// <summary>
/// Represents a chapter 3 long.
/// </summary>
public class Chapter3Long : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         5, 6,11,11,11,11,10,10,12,11, 5, 2,11, 5, 6, 6,
         7, 9,11,13,13,10, 7,11, 6, 7, 8, 9,10,12,11, 5,
        11, 6, 8, 7, 9,11,14,15,11, 6, 6, 8, 4, 5, 7, 8,
        10,13,10, 5, 7, 7, 5, 5, 6, 8,10,11,10, 7, 7, 8,
         6, 5, 5, 7, 9, 9,11, 8, 8,11, 8, 7, 6, 6, 7, 9,
        12,11,10,13, 9, 9, 7, 7, 7, 9,11,13,12,15,12,11,
         9, 8, 8, 8,
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
