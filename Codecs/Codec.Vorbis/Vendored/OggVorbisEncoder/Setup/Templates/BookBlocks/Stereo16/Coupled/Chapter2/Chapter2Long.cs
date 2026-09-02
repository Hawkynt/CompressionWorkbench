namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo16.Coupled.Chapter2;

/// <summary>
/// Represents a chapter 2 long.
/// </summary>
public class Chapter2Long : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         4, 7, 9, 9, 9, 8, 9,10,13,16, 5, 4, 5, 6, 7, 7,
         8, 9,12,16, 6, 5, 5, 5, 7, 7, 9,10,12,15, 7, 6,
         5, 4, 5, 6, 8, 9,10,13, 8, 7, 7, 5, 5, 5, 7, 9,
        10,12, 7, 7, 7, 6, 5, 5, 6, 7,10,12, 8, 8, 8, 7,
         7, 5, 5, 6, 9,11, 8, 9, 9, 8, 8, 6, 6, 5, 8,11,
        10,11,12,12,11, 9, 9, 8, 9,12,13,14,15,15,14,12,
        12,11,11,13,
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
