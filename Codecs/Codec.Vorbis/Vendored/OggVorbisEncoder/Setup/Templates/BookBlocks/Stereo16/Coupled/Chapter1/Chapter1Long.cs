namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo16.Coupled.Chapter1;

/// <summary>
/// Represents a chapter 1 long.
/// </summary>
public class Chapter1Long : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         2, 5,20, 7,10, 7, 8,10,11,11, 4, 2,20, 5, 8, 6,
         7, 9,10,10,20,20,20,20,19,19,19,19,19,19, 7, 5,
        19, 6,10, 7, 9,11,13,17,11, 8,19,10, 7, 7, 8,10,
        11,15, 7, 5,19, 7, 7, 5, 6, 9,11,16, 7, 6,19, 8,
         7, 6, 6, 7, 9,13, 9, 9,19,11, 9, 8, 6, 7, 8,13,
        12,14,19,16,13,10, 9, 8, 9,13,14,17,19,18,18,17,
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
