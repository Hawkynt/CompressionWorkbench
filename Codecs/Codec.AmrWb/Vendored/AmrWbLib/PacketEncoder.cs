/////////////////////////////////////////////////////////////////////////////////////
//  File:   PacketEncoder.cs                                        1 May 25 PHR
/////////////////////////////////////////////////////////////////////////////////////

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private short m_AllowDtx;
    private short m_EncoderMode;
    private Coder_State m_EncoderState = new Coder_State();
    private TX_State m_EncoderTxState = new TX_State();

    /// <summary>
    /// Allocates and initializes structures and variables for using the AmrWb class instance as an encoder.
    /// This function must be called before calling EncodePacketSamples().
    /// </summary>
    /// <param name="Mode">Specifies the codec encoding mode. Allowable values are in the range of 0 - 8 which
    /// correspond to bit rates: 6.60 kb/s, 8.85 kb/s, 12.65 kb/s, 14.25 kb/s, 15.85 kb/s, 18.25 kb/s, 19.85 kb/s,
    /// 23.05 kb/s and 23.85 kb/s.
    /// </param>
    /// <param name="AllowDtx">Enables or disables Discontinuous Transmit (TX). A value of 0 disables DTX and
    /// a value of 1 enables it. If DTX is enabled then the encoder will not produce speech packets during
    /// periods of silence.</param>
    private void InitializeEncoder(short Mode, short AllowDtx)
    {
        m_EncoderMode = Mode;
        m_AllowDtx = AllowDtx;

        wb_vad_init(ref m_EncoderState.vadSt);
        dtx_enc_init(ref m_EncoderState.dtx_encSt, isf_init);
        Reset_encoder(m_EncoderState, 1);
        Reset_write_serial(m_EncoderTxState);
    }

    /// <summary>
    /// Encodes an array of 16-bit PCM samples into an AMR-WB encoded packet payload that can be sent as the
    /// payload of a RTP packet. The input samples sample rate must be 16k samples/second and the sample period must
    /// be 20 milliseconds. This means that the input array must be 320 16-bit words in length.
    /// The output format is octet aligned with a payload header and a Table of Contents (TOC) byte as specified
    /// in Section 4.4.1 of RFC 4867.
    /// </summary>
    /// <param name="Samples">Input sample array. Must be 320 short values in length.</param>
    /// <returns>Returns a payload that can be attached to an RTP packet. The length of the returned byte
    /// array depends upon the encoder mode specified in the call to the InitializeEncoder() function.
    /// Returns null if an error occurred.</returns>
    public unsafe byte[]? EncodePacketSamples(short[] Samples)
    {
        short* signal = stackalloc short[L_FRAME16k];             /* Buffer for speech @ 16kHz             */
        short[] prms = new short[NB_BITS_MAX];

        short coding_mode = m_EncoderMode;
        short nb_bits = nb_of_bits[coding_mode];
        short i;;
        short reset_flag = 0;

        if (Samples.Length < L_FRAME16k)
            return null;     // Error: The input array is too short

        for (i = 0; i<L_FRAME16k; i++)
            signal[i] = Samples[i];

        /* check for homing frame */
        //reset_flag = encoder_homing_frame_test(signal);
        reset_flag = encoder_homing_frame_test(Samples);

        for (i = 0; i<L_FRAME16k; i++)   /* Delete the 2 LSBs (14-bit input) */
        {
            signal[i] = (short) (signal[i] & 0xfffC);
        }

        coder(ref coding_mode, signal, prms, ref nb_bits, m_EncoderState, m_AllowDtx);

        // Do the equivalent work of Write_serial() to get the byte array return value
        byte[] PacketBytes = WriteSerialPacket(prms, coding_mode);

        if (reset_flag != 0)
            Reset_encoder(m_EncoderState, 1);

        return PacketBytes;
    }

    /// <summary>
    /// Converts an array of audio samples to an array of AMR-WB parameter values. This method is for testing only.
    /// </summary>
    /// <param name="Samples">Input audio samples. The sample format must be linear 16-bit PCM. The length of the array
    /// must be 320 samples.</param>
    /// <returns>Returns an array of parameter values. Each parameter value is either 127 or -127. The length of the array
    /// depends on the mode value that was passed to the CreateAsEncoder() method used to create this AmrWb object.
    /// Returns null if the input array is not 320 samples long.</returns>
    public unsafe short[]? EncodeToPacketParams(short[] Samples)
    {
        short* signal = stackalloc short[L_FRAME16k];             /* Buffer for speech @ 16kHz             */
        short[] prms = new short[NB_BITS_MAX];

        short coding_mode = m_EncoderMode;
        short nb_bits = nb_of_bits[coding_mode];
        short i;
        short reset_flag = 0;

        if (Samples.Length < L_FRAME16k)
            return null;     // Error: The input array is too short

        for (i = 0; i < L_FRAME16k; i++)
            signal[i] = Samples[i];

        /* check for homing frame */
        //reset_flag = encoder_homing_frame_test(signal);
        reset_flag = encoder_homing_frame_test(Samples);

        for (i = 0; i < L_FRAME16k; i++)   /* Delete the 2 LSBs (14-bit input) */
        {
            signal[i] = (short)(signal[i] & 0xfffc);
        }

        short NumOfBits = nb_bits;

        coder(ref coding_mode, signal, prms, ref NumOfBits, m_EncoderState, m_AllowDtx);

        if (reset_flag != 0)
            Reset_encoder(m_EncoderState, 1);

        short[] Params = new short[nb_bits];
        for (i=0; i < nb_bits; i++)
        {
            Params[i] = prms[i];
        }

        return Params;
    }
}
