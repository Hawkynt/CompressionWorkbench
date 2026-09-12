/*--------------------------------------------------------------------------*
 *                         DTX.H                      *
 *--------------------------------------------------------------------------*
 *       Static memory, constants and frametypes for the DTX         *
 *--------------------------------------------------------------------------*/

using System.Runtime.InteropServices;

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private const int DTX_MAX_EMPTY_THRESH = 50;
    private const int DTX_HIST_SIZE = 8;
    private const int DTX_HIST_SIZE_MIN_ONE = 7;
    private const int DTX_ELAPSED_FRAMES_THRESH = (24 + 7 - 1);
    private const int DTX_HANG_CONST = 7;                   /* yields eight frames of SP HANGOVER  */
    private const int INV_MED_THRESH = 14564;
    private const int ISF_GAP = 128;                        /* 50 */
    private const int ONE_MINUS_ISF_GAP = 16384 - ISF_GAP;

    //public const int ISF_GAP = 128;
    private const int ISF_DITH_GAP = 448;
    private const int ISF_FACTOR_LOW = 256;
    private const int ISF_FACTOR_STEP = 2;

    private const int GAIN_THR = 180;
    private const int GAIN_FACTOR = 75;

    private class dtx_encState
    {
        public short[] isf_hist = new short[M * DTX_HIST_SIZE];
        public short[] log_en_hist = new short[DTX_HIST_SIZE];
        public short hist_ptr;
        public short log_en_index;
        public short cng_seed;

        /* DTX handler stuff */
        public short dtxHangoverCount;
        public short decAnaElapsedCount;
        public int[] D = new int[28];
        public int[] sumD = new int[DTX_HIST_SIZE];

        public dtx_encState() { }
    }

    private const int SPEECH = 0;
    private const int DTX = 1;
    private const int DTX_MUTE = 2;

    private const int TX_SPEECH = 0;
    private const int TX_SID_FIRST = 1;
    private const int TX_SID_UPDATE = 2;
    private const int TX_NO_DATA = 3;

    private const int RX_SPEECH_GOOD = 0;
    private const int RX_SPEECH_PROBABLY_DEGRADED = 1;
    private const int RX_SPEECH_LOST = 2;
    private const int RX_SPEECH_BAD = 3;
    private const int RX_SID_FIRST = 4;
    private const int RX_SID_UPDATE = 5;
    private const int RX_SID_BAD = 6;
    private const int RX_NO_DATA = 7;

    /*****************************************************************************
     *
     * DEFINITION OF DATA TYPES
     *****************************************************************************/

    private class dtx_decState
    {
        public dtx_decState() { }

        public short since_last_sid;
        public short true_sid_period_inv;
        public short log_en;
        public short old_log_en;
        public short level = 0;
        public short[] isf = new short[M];
        public short[] isf_old = new short[M];
        public short cng_seed;

        public short[] isf_hist = new short[M * DTX_HIST_SIZE];
        public short[] log_en_hist = new short[DTX_HIST_SIZE];
        public short hist_ptr;

        public short dtxHangoverCount;
        public short decAnaElapsedCount;

        public short sid_frame;
        public short valid_data;
        public short dtxHangoverAdded;

        public short dtxGlobalState;                 /* contains previous state */
    
        /* updated in main decoder */

        public short data_updated;                   /* marker to know if CNI data is ever renewed */

        public short dither_seed;
        public short CN_dith;
        public short dtx_vad_hist;

    }

    //Word16 dtx_enc_init(dtx_encState** st, Word16 isf_init[]);
    //Word16 dtx_enc_reset(dtx_encState* st, Word16 isf_init[]);
    //void dtx_enc_exit(dtx_encState** st);

    //Word16 dtx_enc(
    //     dtx_encState* st,                    /* i/o : State struct                                         */
    //Word16 isf[M],                        /* o   : CN ISF vector                                        */
    //     Word16* exc2,                        /* o   : CN excitation                                        */
    //     Word16** prms
    //);

    //Word16 dtx_buffer(
    //     dtx_encState* st,                    /* i/o : State struct                    */
    //     Word16 isf_new[],                     /* i   : isf vector                      */
    //     Word32 enr,                           /* i   : residual energy (in L_FRAME)    */
    //     Word16 codec_mode
    //);

    //void tx_dtx_handler(dtx_encState* st,     /* i/o : State struct           */
    //     Word16 vad_flag,                      /* i   : vad decision           */
    //     Word16* usedMode                     /* i/o : mode changed or not    */
    //);

    //void Qisf_ns(
    //     Word16* isf1,                        /* input : ISF in the frequency domain (0..0.5) */
    //     Word16* isf_q,                       /* output: quantized ISF                        */
    //     Word16* indice                       /* output: quantization indices                 */
    //);


    //Word16 dtx_dec_init(dtx_decState** st, Word16 isf_init[]);
    //Word16 dtx_dec_reset(dtx_decState* st, Word16 isf_init[]);
    //void dtx_dec_exit(dtx_decState** st);

    //Word16 dtx_dec(
    //     dtx_decState* st,                    /* i/o : State struct                                          */
    //     Word16* exc2,                        /* o   : CN excitation                                          */
    //     Word16 new_state,                     /* i   : New DTX state                                          */
    //     Word16 isf[],                         /* o   : CN ISF vector                                          */
    //     Word16** prms
    //);

    //void dtx_dec_activity_update(
    //     dtx_decState* st,
    //     Word16 isf[],
    //     Word16 exc[]);


    //Word16 rx_dtx_handler(
    //     dtx_decState* st,                    /* i/o : State struct     */
    //     Word16 frame_type                     /* i   : Frame type       */
    //);

    //void Disf_ns(
    //     Word16* indice,                      /* input:  quantization indices                  */
    //     Word16* isf_q                        /* input : ISF in the frequency domain (0..0.5)  */
    //);

    /*-------------------------------------------------------------------*
     *                         DTX.C                                     *
     *-------------------------------------------------------------------*
     * DTX functions                                                     *
     *-------------------------------------------------------------------*/

    /* excitation energy adjustment depending on speech coder mode used, Q7 */
    private static short[] en_adjust = new short[9]
    {
        230,                                   /* mode0 = 7k  :  -5.4dB  */
        179,                                   /* mode1 = 9k  :  -4.2dB  */
        141,                                   /* mode2 = 12k :  -3.3dB  */
        128,                                   /* mode3 = 14k :  -3.0dB  */
        122,                                   /* mode4 = 16k :  -2.85dB */
        115,                                   /* mode5 = 18k :  -2.7dB  */
        115,                                   /* mode6 = 20k :  -2.7dB  */
        115,                                   /* mode7 = 23k :  -2.7dB  */
        115                                    /* mode8 = 24k :  -2.7dB  */
    };

    /**************************************************************************
     *
     *
     * Function    : dtx_enc_init
     *
     *
     **************************************************************************/
    private short dtx_enc_init(ref dtx_encState st, short[] isf_init)
    {
        dtx_encState s = new dtx_encState();

        /* allocate memory */
        dtx_enc_reset(s, isf_init);
        st = s;

        return 0;
    }

    /**************************************************************************
     *
     *
     * Function    : dtx_enc_reset
     *
     *
     **************************************************************************/
    private short dtx_enc_reset(dtx_encState st, short[] isf_init)
    {
        short i;

        st.hist_ptr = 0;
        st.log_en_index = 0;

        /* Init isf_hist[] */
        for (i = 0; i < DTX_HIST_SIZE; i++)
        {
            //Copy(isf_init, &st.isf_hist[i * M], M);
            for (int j=0; j < M; j++)
                st.isf_hist[j + i * M] = isf_init[j];
        }

        st.cng_seed = RANDOM_INITSEED;

        /* Reset energy history */
        Set_zero(st.log_en_hist, DTX_HIST_SIZE);

        st.dtxHangoverCount = DTX_HANG_CONST;
        st.decAnaElapsedCount = 32767;

        for (i = 0; i < 28; i++)
        {
            st.D[i] = 0;
        }

        for (i = 0; i < DTX_HIST_SIZE - 1; i++)
        {
            st.sumD[i] = 0;
        }

        return 1;
    }

    /**************************************************************************
     *
     *
     * Function    : dtx_enc
     *
     *
     **************************************************************************/
    private unsafe short dtx_enc(
         dtx_encState st,                /* i/o : State struct                                         */
         short[] isf,                        /* o   : CN ISF vector                                        */
         short* exc2,                       /* o   : CN excitation                                        */
         short[] prms,
         ref int CurrentIndex   // 9 Jan 24 PHR
    )
    {
        short i, j;
        short[] indice = new short[7];
        short log_en, gain, level, exp, exp0, tmp;
        short log_en_int_e, log_en_int_m;
        int[] L_isf = new int[M];
        int ener32, level32;
        short[] isf_order = new short[3];
        short CN_dith;

        /* VOX mode computation of SID parameters */
        log_en = 0;
        for (i = 0; i < M; i++)
        {
            L_isf[i] = 0;
        }

        /* average energy and isf */
        for (i = 0; i < DTX_HIST_SIZE; i++)
        {
            /* Division by DTX_HIST_SIZE = 8 has been done in dtx_buffer. log_en is in Q10 */
            log_en = add(log_en, st.log_en_hist[i]);

        }
        find_frame_indices(st.isf_hist, isf_order, st);
        aver_isf_history(st.isf_hist, isf_order, L_isf);

        for (j = 0; j < M; j++)
        {
            isf[j] = extract_l(L_shr(L_isf[j], 3));  /* divide by 8 */
        }

        /* quantize logarithmic energy to 6 bits (-6 : 66 dB) which corresponds to -2:22 in log2(E).  */
        /* st.log_en_index = (short)( (log_en + 2.0) * 2.625 ); */

        /* increase dynamics to 7 bits (Q8) */
        log_en = shr(log_en, 2);

        /* Add 2 in Q8 = 512 to get log2(E) between 0:24 */
        log_en = add(log_en, 512);

        /* Multiply by 2.625 to get full 6 bit range. 2.625 = 21504 in Q13. The result is in Q6 */
        log_en = mult(log_en, 21504);

        /* Quantize Energy */
        st.log_en_index = shr(log_en, 6);

        if (sub(st.log_en_index, 63) > 0)
        {
            st.log_en_index = 63;
        }

        if (st.log_en_index < 0)
        {
            st.log_en_index = 0;
        }
        /* Quantize ISFs */
        Qisf_ns(isf, isf, indice);

        Parm_serial(indice[0], 6, prms, ref CurrentIndex);
        Parm_serial(indice[1], 6, prms, ref CurrentIndex);
        Parm_serial(indice[2], 6, prms, ref CurrentIndex);
        Parm_serial(indice[3], 5, prms, ref CurrentIndex);
        Parm_serial(indice[4], 5, prms, ref CurrentIndex);

        Parm_serial((st.log_en_index), 6, prms, ref CurrentIndex);

        CN_dith = dithering_control(st);
        Parm_serial(CN_dith, 1, prms, ref CurrentIndex);

        /* level = (float)( pow( 2.0f, (float)st.log_en_index / 2.625 - 2.0 ) );    */
        /* log2(E) in Q9 (log2(E) lies in between -2:22) */
        log_en = shl(st.log_en_index, 15 - 6);

        /* Divide by 2.625; log_en will be between 0:24  */
        log_en = mult(log_en, 12483);
        /* the result corresponds to log2(gain) in Q10 */

        /* Find integer part  */
        log_en_int_e = shr(log_en, 10);

        /* Find fractional part */
        log_en_int_m = (short)(log_en & 0x3ff);
        log_en_int_m = shl(log_en_int_m, 5);

        /* Subtract 2 from log_en in Q9, i.e divide the gain by 2 (energy by 4) */
        /* Add 16 in order to have the result of pow2 in Q16 */
        log_en_int_e = add(log_en_int_e, 16 - 1);

        level32 = Pow2(log_en_int_e, log_en_int_m); /* Q16 */
        exp0 = norm_l(level32);
        level32 = L_shl(level32, exp0);        /* level in Q31 */
        exp0 = sub(15, exp0);
        level = extract_h(level32);            /* level in Q15 */

        /* generate white noise vector */
        for (i = 0; i < L_FRAME; i++)
        {
            exc2[i] = shr(Random(ref st.cng_seed), 4);
        }

        /* gain = level / sqrt(ener) * sqrt(L_FRAME) */

        /* energy of generated excitation */
        exp = 0;
        ener32 = Dot_product12(exc2, exc2, L_FRAME, ref exp);

        Isqrt_n(ref ener32, ref exp);

        gain = extract_h(ener32);

        gain = mult(level, gain);              /* gain in Q15 */

        exp = add(exp0, exp);

        /* Multiply by sqrt(L_FRAME)=16, i.e. shift left by 4 */
        exp = add(exp, 4);

        for (i = 0; i < L_FRAME; i++)
        {
            tmp = mult(exc2[i], gain);         /* Q0 * Q15 */
            exc2[i] = shl(tmp, exp);
        }

        return 0;
    }

    /**************************************************************************
     *
     *
     * Function    : dtx_buffer Purpose     : handles the DTX buffer
     *
     *
     **************************************************************************/
    private short dtx_buffer(
         dtx_encState st,                    /* i/o : State struct                    */
         short[] isf_new,                     /* i   : isf vector                      */
         int enr,                           /* i   : residual energy (in L_FRAME)    */
         short codec_mode
    )
    {
        short log_en;

        short log_en_e;
        short log_en_m;

        st.hist_ptr = add(st.hist_ptr, 1);

        if (sub(st.hist_ptr, DTX_HIST_SIZE) == 0)
        {
            st.hist_ptr = 0;
        }

        /* copy lsp vector into buffer */
        //Copy(isf_new, &st.isf_hist[st.hist_ptr * M], M);
        Copy(isf_new, st.isf_hist, st.hist_ptr * M, M); // 10 Jan 24 PHR

        /* log_en = (float)log10(enr*0.0059322)/(float)log10(2.0f);  */
        log_en_e = 0;   // 10 Jan 24 PHR
        log_en_m = 0;
        Log2(enr, ref log_en_e, ref log_en_m);

        /* convert exponent and mantissa to Word16 Q7. Q7 is used to simplify averaging in dtx_enc */
        log_en = shl(log_en_e, 7);             /* Q7 */
        log_en = add(log_en, shr(log_en_m, 15 - 7));

        /* Find energy per sample by multiplying with 0.0059322, i.e subtract log2(1/0.0059322) = 7.39722 The
         * constant 0.0059322 takes into account windowings and analysis length from autocorrelation
         * computations; 7.39722 in Q7 = 947  */
        /* Subtract 3 dB = 0.99658 in log2(E) = 127 in Q7. */
        /* log_en = sub( log_en, 947 + en_adjust[codec_mode] ); */

        /* Find energy per sample (divide by L_FRAME=256), i.e subtract log2(256) = 8.0  (1024 in Q7) */
        /* Subtract 3 dB = 0.99658 in log2(E) = 127 in Q7. */

        log_en = sub(log_en, add(1024, en_adjust[codec_mode]));

        /* Insert into the buffer */
        st.log_en_hist[st.hist_ptr] = log_en;
        return 0;
    }

    /**************************************************************************
     *
     *
     * Function    : tx_dtx_handler Purpose     : adds extra speech hangover
     *                                            to analyze speech on
     *                                            the decoding side.
     *
     **************************************************************************/
    private void tx_dtx_handler(dtx_encState st,     /* i/o : State struct           */
         short vad_flag,                      /* i   : vad decision           */
         ref short usedMode)                 /* i/o : mode changed or not    */
    {

        /* this state machine is in synch with the GSMEFR txDtx machine      */
        st.decAnaElapsedCount = add(st.decAnaElapsedCount, 1);

        if (vad_flag != 0)
        {
            st.dtxHangoverCount = DTX_HANG_CONST;
        }
        else
        {                                      /* non-speech */
            if (st.dtxHangoverCount == 0)
            {                                  /* out of decoder analysis hangover  */
                st.decAnaElapsedCount = 0;
                usedMode = MRDTX;
            }
            else
            {                                  /* in possible analysis hangover */
                st.dtxHangoverCount = sub(st.dtxHangoverCount, 1);

                /* decAnaElapsedCount + dtxHangoverCount < DTX_ELAPSED_FRAMES_THRESH */
                if (sub(add(st.decAnaElapsedCount, st.dtxHangoverCount),
                        DTX_ELAPSED_FRAMES_THRESH) < 0)
                {
                    usedMode = MRDTX;
                    /* if short time since decoder update, do not add extra HO */
                }
                /* else override VAD and stay in speech mode *usedMode and add extra hangover */
            }
        }

        return;
    }

    /**************************************************************************
     *
     *
     * Function    : dtx_dec_init
     *
     *
     **************************************************************************/
    private short dtx_dec_init(dtx_decState st, short[] isf_init)
    {
        dtx_decState s = new dtx_decState();

        //if (st == (dtx_decState**)NULL)
        //{
        //    fprintf(stderr, "dtx_dec_init: invalid parameter\n");
        //    return -1;
        //}

        dtx_dec_reset(ref s, isf_init);
        st = s;

        return 0;
    }

    /**************************************************************************
     *
     *
     * Function    : dtx_dec_reset
     *
     *
     **************************************************************************/
    short dtx_dec_reset(ref dtx_decState st, short[] isf_init)
    {
        short i;

        st.since_last_sid = 0;
        st.true_sid_period_inv = (1 << 13);  /* 0.25 in Q15 */

        st.log_en = 3500;
        st.old_log_en = 3500;
        /* low level noise for better performance in  DTX handover cases */

        st.cng_seed = RANDOM_INITSEED;

        st.hist_ptr = 0;

        /* Init isf_hist[] and decoder log frame energy */
        Copy(isf_init, st.isf, M);
        Copy(isf_init, st.isf_old, M);

        for (i = 0; i < DTX_HIST_SIZE; i++)
        {
            //Copy(isf_init, &st.isf_hist[i * M], M);
            Copy(isf_init, st.isf_hist, i * M, M);  // 10 Jan 24 PHR

            st.log_en_hist[i] = (3500 >> 3);  /* scale down value in history properly by 2^3 */
        }

        st.dtxHangoverCount = DTX_HANG_CONST;
        st.decAnaElapsedCount = 32767;

        st.sid_frame = 0;
        st.valid_data = 0;
        st.dtxHangoverAdded = 0;

        st.dtxGlobalState = SPEECH;
        st.data_updated = 0;

        st.dither_seed = RANDOM_INITSEED;
        st.CN_dith = 0;
        st.dtx_vad_hist = 0;

        return 0;
    }

    /**************************************************************************
     *
     *
     * Function    : dtx_dec_exit
     *
     *
     **************************************************************************/
    private void dtx_dec_exit(ref dtx_decState st)
    {

        return;
    }

    /*
         Table of new SPD synthesis states

                               |     previous SPD_synthesis_state
         Incoming              |
         frame_type            | SPEECH       | DTX           | DTX_MUTE
         ---------------------------------------------------------------
         RX_SPEECH_GOOD ,      |              |               |
         RX_SPEECH_PR_DEGRADED | SPEECH       | SPEECH        | SPEECH
         ----------------------------------------------------------------
         RX_SPEECH_BAD,        | SPEECH       | DTX           | DTX_MUTE
         ----------------------------------------------------------------
         RX_SID_FIRST,         | DTX          | DTX/(DTX_MUTE)| DTX_MUTE
         ----------------------------------------------------------------
         RX_SID_UPDATE,        | DTX          | DTX           | DTX
         ----------------------------------------------------------------
         RX_SID_BAD,           | DTX          | DTX/(DTX_MUTE)| DTX_MUTE
         ----------------------------------------------------------------
         RX_NO_DATA,           | SPEECH       | DTX/(DTX_MUTE)| DTX_MUTE
         RX_SPARE              |(class2 garb.)|               |
         ----------------------------------------------------------------
    */

    /**************************************************************************
     *
     *
     * Function    : dtx_dec
     *
     *
     **************************************************************************/
    private unsafe short dtx_dec(
         dtx_decState st,                    /* i/o : State struct                                          */
         short* exc2,                        /* o   : CN excitation                                          */
         short new_state,                     /* i   : New DTX state                                          */
         short[] isf,                         /* o   : CN ISF vector                                          */
         //Word16** prms
         short[] prms,
         ref int CurrentPrmsIndex)          // 12 May 25 PHR -- Index into prms[]
    {
        short log_en_index;
        short[] ind = new short[7];
        short i, j;
        short int_fac;
        short gain;

        int[] L_isf = new int[M];
        int L_log_en_int, level32, ener32;
        short ptr;
        short tmp_int_length;
        short tmp, exp, exp0, log_en_int_e, log_en_int_m, level;

        /* This function is called if synthesis state is not SPEECH the globally passed  inputs to this function
         * are st.sid_frame st.valid_data st.dtxHangoverAdded new_state  (SPEECH, DTX, DTX_MUTE) */
        if ((st.dtxHangoverAdded != 0) &&  (st.sid_frame != 0))
        {
            /* sid_first after dtx hangover period */
            /* or sid_upd after dtxhangover        */

            /* consider  twice the last frame */
            ptr = add(st.hist_ptr, 1);
            if (sub(ptr, DTX_HIST_SIZE) == 0)
                ptr = 0;

            //Copy(&st.isf_hist[st.hist_ptr * M], &st.isf_hist[ptr * M], M);
            Array.ConstrainedCopy(st.isf_hist, st.hist_ptr * M, st.isf_hist, ptr * M, M); // 10 Jan 24 PHR

            st.log_en_hist[ptr] = st.log_en_hist[st.hist_ptr];

            /* compute mean log energy and isf from decoded signal (SID_FIRST) */
            st.log_en = 0;
            for (i = 0; i < M; i++)
            {
                L_isf[i] = 0;
            }

            /* average energy and isf */
            for (i = 0; i < DTX_HIST_SIZE; i++)
            {
                /* Division by DTX_HIST_SIZE = 8 has been done in dtx_buffer log_en is in Q10 */
                st.log_en = add(st.log_en, st.log_en_hist[i]);

                for (j = 0; j < M; j++)
                {
                    L_isf[j] = L_add(L_isf[j], L_deposit_l(st.isf_hist[i * M + j]));
                }
            }

            /* st.log_en in Q9 */
            st.log_en = shr(st.log_en, 1);

            /* Add 2 in Q9, in order to have only positive values for Pow2 */
            /* this value is subtracted back after Pow2 function */
            st.log_en = add(st.log_en, 1024);
            if (st.log_en < 0)
                st.log_en = 0;

            for (j = 0; j < M; j++)
            {
                st.isf[j] = extract_l(L_shr(L_isf[j], 3));  /* divide by 8 */
            }

        }

        if (st.sid_frame != 0)
        {
            /* Set old SID parameters, always shift */
            /* even if there is no new valid_data   */

            Copy(st.isf, st.isf_old, M);
            st.old_log_en = st.log_en;
            if (st.valid_data != 0)           /* new data available (no CRC) */
            {
                /* st.true_sid_period_inv = 1.0f/st.since_last_sid; */
                /* Compute interpolation factor, since the division only works * for values of since_last_sid <
                 * 32 we have to limit the      * interpolation to 32 frames                                  */
                tmp_int_length = st.since_last_sid;

                if (sub(tmp_int_length, 32) > 0)
                {
                    tmp_int_length = 32;
                }
                if (sub(tmp_int_length, 2) >= 0)
                {
                    st.true_sid_period_inv = div_s(1 << 10, shl(tmp_int_length, 10));
                }
                else
                {
                    st.true_sid_period_inv = 1 << 14;      /* 0.5 it Q15 */
                }

                ind[0] = Serial_parm(6, prms, ref CurrentPrmsIndex);
                ind[1] = Serial_parm(6, prms, ref CurrentPrmsIndex);
                ind[2] = Serial_parm(6, prms, ref CurrentPrmsIndex);
                ind[3] = Serial_parm(5, prms, ref CurrentPrmsIndex);
                ind[4] = Serial_parm(5, prms, ref CurrentPrmsIndex);

                Disf_ns(ind, st.isf);

                log_en_index = Serial_parm(6, prms, ref CurrentPrmsIndex);

                /* read background noise stationarity information */
                st.CN_dith = Serial_parm(1, prms, ref CurrentPrmsIndex);

                /* st.log_en = (float)log_en_index / 2.625 - 2.0;  */
                /* log2(E) in Q9 (log2(E) lies in between -2:22) */
                st.log_en = shl(log_en_index, 15 - 6);

                /* Divide by 2.625  */
                st.log_en = mult(st.log_en, 12483);
                /* Subtract 2 in Q9 is done later, after Pow2 function  */

                /* no interpolation at startup after coder reset        */
                /* or when SID_UPD has been received right after SPEECH */
                if ((st.data_updated == 0) ||
                    (sub(st.dtxGlobalState, SPEECH) == 0))
                {
                    Copy(st.isf, st.isf_old, M);
                    st.old_log_en = st.log_en;
                }
            }                                  /* endif valid_data */
        }                                      /* endif sid_frame */
        
        
        if ((st.sid_frame != 0) && (st.valid_data != 0))
        {
            st.since_last_sid = 0; 
        }
        /* Interpolate SID info */
        int_fac = shl(st.since_last_sid, 10); /* Q10 */
        int_fac = mult(int_fac, st.true_sid_period_inv);   /* Q10 * Q15 . Q10 */

        /* Maximize to 1.0 in Q10 */
        
        if (sub(int_fac, 1024) > 0)
        {
            int_fac = 1024; 
        }
        int_fac = shl(int_fac, 4);             /* Q10 . Q14 */

        L_log_en_int = L_mult(int_fac, st.log_en); /* Q14 * Q9 . Q24 */

        for (i = 0; i < M; i++)
        {
            isf[i] = mult(int_fac, st.isf[i]);/* Q14 * Q15 . Q14 */
        }

        int_fac = sub(16384, int_fac);         /* 1-k in Q14 */

        /* ( Q14 * Q9 . Q24 ) + Q24 . Q24 */
        L_log_en_int = L_mac(L_log_en_int, int_fac, st.old_log_en);

        for (i = 0; i < M; i++)
        {
            /* Q14 + (Q14 * Q15 . Q14) . Q14 */
            isf[i] = add(isf[i], mult(int_fac, st.isf_old[i])); 
            isf[i] = shl(isf[i], 1);           /* Q14 . Q15 */
        }

        /* If background noise is non-stationary, insert comfort noise dithering */
        if (st.CN_dith != 0)
        {
            CN_dithering(isf, ref L_log_en_int, ref st.dither_seed);
        }
        /* L_log_en_int corresponds to log2(E)+2 in Q24, i.e log2(gain)+1 in Q25 */
        /* Q25 . Q16 */
        L_log_en_int = L_shr(L_log_en_int, 9);

        /* Find integer part  */
        log_en_int_e = extract_h(L_log_en_int);

        /* Find fractional part */
        log_en_int_m = extract_l(L_shr(L_sub(L_log_en_int, L_deposit_h(log_en_int_e)), 1));

        /* Subtract 2 from L_log_en_int in Q9, i.e divide the gain by 2 (energy by 4) */
        /* Add 16 in order to have the result of pow2 in Q16 */
        log_en_int_e = add(log_en_int_e, 16 - 1);

        /* level = (float)( pow( 2.0f, log_en ) );  */
        level32 = Pow2(log_en_int_e, log_en_int_m); /* Q16 */
        exp0 = norm_l(level32);
        level32 = L_shl(level32, exp0);        /* level in Q31 */
        exp0 = sub(15, exp0);
        level = extract_h(level32);            /* level in Q15 */

        /* generate white noise vector */
        for (i = 0; i < L_FRAME; i++)
        {
            exc2[i] = shr(Random(ref st.cng_seed), 4); 
        }

        /* gain = level / sqrt(ener) * sqrt(L_FRAME) */

        /* energy of generated excitation */
        exp = 0;
        ener32 = Dot_product12(exc2, exc2, L_FRAME, ref exp);

        Isqrt_n(ref ener32, ref exp);

        gain = extract_h(ener32);

        gain = mult(level, gain);              /* gain in Q15 */

        exp = add(exp0, exp);

        /* Multiply by sqrt(L_FRAME)=16, i.e. shift left by 4 */
        exp = add(exp, 4);

        for (i = 0; i < L_FRAME; i++)
        {
            tmp = mult(exc2[i], gain);         /* Q0 * Q15 */
            exc2[i] = shl(tmp, exp); 
        }
        
        if (sub(new_state, DTX_MUTE) == 0)
        {
            /* mute comfort noise as it has been quite a long time since last SID update  was performed                            */

            tmp_int_length = st.since_last_sid; 
            
            if (sub(tmp_int_length, 32) > 0)
            {
                tmp_int_length = 32; 
            }

            /* safety guard against division by zero */
            
            if (tmp_int_length <= 0)
            {
                tmp_int_length = 8; 
            }

            
            st.true_sid_period_inv = div_s(1 << 10, shl(tmp_int_length, 10));

            st.since_last_sid = 0; 
            st.old_log_en = st.log_en; 
            /* subtract 1/8 in Q9 (energy), i.e -3/8 dB */
            st.log_en = sub(st.log_en, 64); 
        }
        /* reset interpolation length timer if data has been updated.        */
           
        if ((st.sid_frame != 0) &&
            ((st.valid_data != 0) ||
                ((st.valid_data == 0) && (st.dtxHangoverAdded) != 0)))
        {
            st.since_last_sid = 0; 
            st.data_updated = 1; 
        }
        return 0;
    }


    private unsafe void dtx_dec_activity_update(
         dtx_decState st,
         short[] isf,
         short* exc)
    {
        short i;

        int L_frame_en;
        short log_en_e, log_en_m, log_en;


        st.hist_ptr = add(st.hist_ptr, 1); 
        
        if (sub(st.hist_ptr, DTX_HIST_SIZE) == 0)
        {
            st.hist_ptr = 0; 
        }
        //Copy(isf, &st.isf_hist[st.hist_ptr * M], M);
        Copy(isf, st.isf_hist, st.hist_ptr * M, M); // 10 Jan 24 PHR

        /* compute log energy based on excitation frame energy in Q0 */
        L_frame_en = 0; 
        for (i = 0; i < L_FRAME; i++)
        {
            L_frame_en = L_mac(L_frame_en, exc[i], exc[i]);
        }
        L_frame_en = L_shr(L_frame_en, 1);

        /* log_en = (float)log10(L_frame_en/(float)L_FRAME)/(float)log10(2.0f); */
        log_en_e = 0; log_en_m = 0; // 10 Jan 24 PHR
        Log2(L_frame_en, ref log_en_e, ref log_en_m);

        /* convert exponent and mantissa to Word16 Q7. Q7 is used to simplify averaging in dtx_enc */
        log_en = shl(log_en_e, 7);             /* Q7 */
        log_en = add(log_en, shr(log_en_m, 15 - 7));

        /* Divide by L_FRAME = 256, i.e subtract 8 in Q7 = 1024 */
        log_en = sub(log_en, 1024);

        /* insert into log energy buffer */
        st.log_en_hist[st.hist_ptr] = log_en; 

        return;
    }


    /*
         Table of new SPD synthesis states

                               |     previous SPD_synthesis_state
         Incoming              |
         frame_type            | SPEECH       | DTX           | DTX_MUTE
         ---------------------------------------------------------------
         RX_SPEECH_GOOD ,      |              |               |
         RX_SPEECH_PR_DEGRADED | SPEECH       | SPEECH        | SPEECH
         ----------------------------------------------------------------
         RX_SPEECH_BAD,        | SPEECH       | DTX           | DTX_MUTE
         ----------------------------------------------------------------
         RX_SID_FIRST,         | DTX          | DTX/(DTX_MUTE)| DTX_MUTE
         ----------------------------------------------------------------
         RX_SID_UPDATE,        | DTX          | DTX           | DTX
         ----------------------------------------------------------------
         RX_SID_BAD,           | DTX          | DTX/(DTX_MUTE)| DTX_MUTE
         ----------------------------------------------------------------
         RX_NO_DATA,           | SPEECH       | DTX/(DTX_MUTE)| DTX_MUTE
         RX_SPARE              |(class2 garb.)|               |
         ----------------------------------------------------------------
    */

    private short rx_dtx_handler(
         dtx_decState st,                    /* i/o : State struct     */
         short frame_type                     /* i   : Frame type       */
    )
    {
        short newState;
        short encState;

        /* DTX if SID frame or previously in DTX{_MUTE} and (NO_RX OR BAD_SPEECH) */
          
          
         
        if ((sub(frame_type, RX_SID_FIRST) == 0) ||
            (sub(frame_type, RX_SID_UPDATE) == 0) ||
            (sub(frame_type, RX_SID_BAD) == 0) ||
            (((sub(st.dtxGlobalState, DTX) == 0) ||
                    (sub(st.dtxGlobalState, DTX_MUTE) == 0)) &&
                ((sub(frame_type, RX_NO_DATA) == 0) ||
                    (sub(frame_type, RX_SPEECH_BAD) == 0) ||
                    (sub(frame_type, RX_SPEECH_LOST) == 0))))
        {
            newState = DTX; 

            /* stay in mute for these input types */
                
            if ((sub(st.dtxGlobalState, DTX_MUTE) == 0) &&
                ((sub(frame_type, RX_SID_BAD) == 0) ||
                    (sub(frame_type, RX_SID_FIRST) == 0) ||
                    (sub(frame_type, RX_SPEECH_LOST) == 0) ||
                    (sub(frame_type, RX_NO_DATA) == 0)))
            {
                newState = DTX_MUTE; 
            }
            /* evaluate if noise parameters are too old                     */
            /* since_last_sid is reset when CN parameters have been updated */
            st.since_last_sid = add(st.since_last_sid, 1); 

            /* no update of sid parameters in DTX for a long while */
            /* Due to the delayed update of  st.since_last_sid counter
               SID_UPDATE frames need to be handled separately to avoid
               entering DTX_MUTE for late SID_UPDATE frames
            */
            if ((sub(frame_type, RX_SID_UPDATE) != 0) &&
                (sub(st.since_last_sid, DTX_MAX_EMPTY_THRESH) > 0))
            {
                newState = DTX_MUTE; 
            }
        }
        else
        {
            newState = SPEECH; 
            st.since_last_sid = 0; 
        }

        /* reset the decAnaElapsed Counter when receiving CNI data the first time, to robustify counter missmatch
         * after handover this might delay the bwd CNI analysis in the new decoder slightly. */
         
        if ((st.data_updated == 0) &&
            (sub(frame_type, RX_SID_UPDATE) == 0))
        {
            st.decAnaElapsedCount = 0; 
        }
        /* update the SPE-SPD DTX hangover synchronization */
        /* to know when SPE has added dtx hangover         */
        st.decAnaElapsedCount = add(st.decAnaElapsedCount, 1); 
        st.dtxHangoverAdded = 0; 

        if ((sub(frame_type, RX_SID_FIRST) == 0) ||
            (sub(frame_type, RX_SID_UPDATE) == 0) ||
            (sub(frame_type, RX_SID_BAD) == 0) ||
            ((sub(frame_type, RX_NO_DATA) == 0) &&
            ((sub(st.dtxGlobalState, SPEECH) != 0) ||
            (sub(st.dtx_vad_hist, DTX_HANG_CONST) >= 0))))
        {
            encState = DTX; 
        }
        else
        {
            encState = SPEECH; 
        }

        
        if (sub(encState, SPEECH) == 0)
        {
            st.dtxHangoverCount = DTX_HANG_CONST; 
        }
        else
        {
             
            if (sub(st.decAnaElapsedCount, DTX_ELAPSED_FRAMES_THRESH) > 0)
            {
                st.dtxHangoverAdded = 1; 
                st.decAnaElapsedCount = 0; 
                st.dtxHangoverCount = 0; 
            }
            //else if (test(), st.dtxHangoverCount == 0)
            else if (st.dtxHangoverCount == 0)  // 10 Jan 24 PHR
            {
                st.decAnaElapsedCount = 0; 
            }
            else
            {
                st.dtxHangoverCount = sub(st.dtxHangoverCount, 1); 
            }
        }
        
        if (sub(newState, SPEECH) != 0)
        {
            /* DTX or DTX_MUTE CN data is not in a first SID, first SIDs are marked as SID_BAD but will do
             * backwards analysis if a hangover period has been added according to the state machine above */

            st.sid_frame = 0; 
            st.valid_data = 0; 

              
            if (sub(frame_type, RX_SID_FIRST) == 0)
            {
                st.sid_frame = 1; 
            }
            //else if (test(), sub(frame_type, RX_SID_UPDATE) == 0)
            else if (sub(frame_type, RX_SID_UPDATE) == 0)   // 10 Jan 24 PHR
            {
                st.sid_frame = 1; 
                st.valid_data = 1; 
            }
            //else if (test(), sub(frame_type, RX_SID_BAD) == 0)
            else if (sub(frame_type, RX_SID_BAD) == 0)      // 10 Jan 24 PHR
            {
                st.sid_frame = 1; 
                st.dtxHangoverAdded = 0;      /* use old data */
            }
        }
        return newState;
        /* newState is used by both SPEECH AND DTX synthesis routines */
    }

    private void aver_isf_history(
         short[] isf_old,
         short[] indices,
         int[] isf_aver
)
    {
        short i, j, k;
        short[] isf_tmp = new short[2 * M];
        int L_tmp;

        /* Memorize in isf_tmp[][] the ISF vectors to be replaced by */
        /* the median ISF vector prior to the averaging               */
        for (k = 0; k < 2; k++)
        {
            if (add(indices[k], 1) != 0)
            {
                for (i = 0; i < M; i++)
                {
                    isf_tmp[k * M + i] = isf_old[indices[k] * M + i];
                    isf_old[indices[k] * M + i] = isf_old[indices[2] * M + i];
                }
            }
        }

        /* Perform the ISF averaging */
        for (j = 0; j < M; j++)
        {
            L_tmp = 0;

            for (i = 0; i < DTX_HIST_SIZE; i++)
            {
                L_tmp = L_add(L_tmp, L_deposit_l(isf_old[i * M + j]));
            }
            isf_aver[j] = L_tmp;
        }

        /* Retrieve from isf_tmp[][] the ISF vectors saved prior to averaging */
        for (k = 0; k < 2; k++)
        {
            if (add(indices[k], 1) != 0)
            {
                for (i = 0; i < M; i++)
                {
                    isf_old[indices[k] * M + i] = isf_tmp[k * M + i]; 
                }
            }
        }

        return;
    }

    private void find_frame_indices(
         short[] isf_old_tx,
         short[] indices,
         dtx_encState st
    )
    {
        int L_tmp, summin, summax, summax2nd;
        short i, j, tmp;
        short ptr;

        /* Remove the effect of the oldest frame from the column */
        /* sum sumD[0..DTX_HIST_SIZE-1]. sumD[DTX_HIST_SIZE] is    */
        /* not updated since it will be removed later.           */

        tmp = DTX_HIST_SIZE_MIN_ONE;
        j = -1;
        for (i = 0; i < DTX_HIST_SIZE_MIN_ONE; i++)
        {
            j = add(j, tmp);
            st.sumD[i] = L_sub(st.sumD[i], st.D[j]);
            tmp = sub(tmp, 1);
        }

        /* Shift the column sum sumD. The element sumD[DTX_HIST_SIZE-1]    */
        /* corresponding to the oldest frame is removed. The sum of     */
        /* the distances between the latest isf and other isfs, */
        /* i.e. the element sumD[0], will be computed during this call. */
        /* Hence this element is initialized to zero.                   */

        for (i = DTX_HIST_SIZE_MIN_ONE; i > 0; i--)
        {
            st.sumD[i] = st.sumD[i - 1];
        }
        st.sumD[0] = 0;

        /* Remove the oldest frame from the distance matrix.           */
        /* Note that the distance matrix is replaced by a one-         */
        /* dimensional array to save static memory.                    */

        tmp = 0;
        for (i = 27; i >= 12; i = (short)(i - tmp))
        {
            tmp = add(tmp, 1);
            for (j = tmp; j > 0; j--)
            {
                st.D[i - j + 1] = st.D[i - j - tmp];
            }
        }

        /* Compute the first column of the distance matrix D            */
        /* (squared Euclidean distances from isf1[] to isf_old_tx[][]). */

        ptr = st.hist_ptr;
        for (i = 1; i < DTX_HIST_SIZE; i++)
        {
            /* Compute the distance between the latest isf and the other isfs. */
            ptr = sub(ptr, 1);
            if (ptr < 0)
            {
                ptr = DTX_HIST_SIZE_MIN_ONE;
            }
            L_tmp = 0;
            for (j = 0; j < M; j++)
            {
                tmp = sub(isf_old_tx[st.hist_ptr * M + j], isf_old_tx[ptr * M + j]);
                L_tmp = L_mac(L_tmp, tmp, tmp);
            }
            st.D[i - 1] = L_tmp;

            /* Update also the column sums. */
            st.sumD[0] = L_add(st.sumD[0], st.D[i - 1]);
            st.sumD[i] = L_add(st.sumD[i], st.D[i - 1]);
        }

        /* Find the minimum and maximum distances */
        summax = st.sumD[0];
        summin = st.sumD[0];
        indices[0] = 0; 
        indices[2] = 0;
        for (i = 1; i < DTX_HIST_SIZE; i++)
        {
            if (L_sub(st.sumD[i], summax) > 0)
            {
                indices[0] = i;
                summax = st.sumD[i];
            }
            if (L_sub(st.sumD[i], summin) < 0)
            {
                indices[2] = i;
                summin = st.sumD[i];
            }
        }

        /* Find the second largest distance */
        summax2nd = -2147483647;
        indices[1] = -1;
        for (i = 0; i < DTX_HIST_SIZE; i++)
        {
            if ((L_sub(st.sumD[i], summax2nd) > 0) && (sub(i, indices[0]) != 0))
            {
                indices[1] = i;
                summax2nd = st.sumD[i];
            }
        }

        for (i = 0; i < 3; i++)
        {
            indices[i] = sub(st.hist_ptr, indices[i]);
            if (indices[i] < 0)
            {
                indices[i] = add(indices[i], DTX_HIST_SIZE);
            }
        }

        /* If maximum distance/MED_THRESH is smaller than minimum distance */
        /* then the median ISF vector replacement is not performed         */
        tmp = norm_l(summax);
        summax = L_shl(summax, tmp);
        summin = L_shl(summin, tmp);
        L_tmp = L_mult(round(summax), INV_MED_THRESH);
        if (L_sub(L_tmp, summin) <= 0)
        {
            indices[0] = -1;
        }
        /* If second largest distance/MED_THRESH is smaller than     */
        /* minimum distance then the median ISF vector replacement is    */
        /* not performed                                                 */
        summax2nd = L_shl(summax2nd, tmp);
        L_tmp = L_mult(round(summax2nd), INV_MED_THRESH);
        if (L_sub(L_tmp, summin) <= 0)
        {
            indices[1] = -1;
        }
        return;
    }

    private short dithering_control(dtx_encState st
    )
    {
        short i, tmp, mean, CN_dith, gain_diff;
        int ISF_diff;

        /* determine how stationary the spectrum of background noise is */
        ISF_diff = 0;
        for (i = 0; i < 8; i++)
        {
            ISF_diff = L_add(ISF_diff, st.sumD[i]);
        }
        if (L_shr(ISF_diff, 26) > 0)
        {
            CN_dith = 1;
        }
        else
        {
            CN_dith = 0;
        }

        /* determine how stationary the energy of background noise is */
        mean = 0;
        for (i = 0; i < DTX_HIST_SIZE; i++)
        {
            mean = add(mean, st.log_en_hist[i]);
        }
        mean = shr(mean, 3);
        gain_diff = 0;
        for (i = 0; i < DTX_HIST_SIZE; i++)
        {
            tmp = abs_s(sub(st.log_en_hist[i], mean));
            gain_diff = add(gain_diff, tmp);
        }
        if (sub(gain_diff, GAIN_THR) > 0)
        {
            CN_dith = 1;
        }
        return CN_dith;
    }

    private void CN_dithering(
         short[] isf,
         ref int L_log_en_int,
         ref short dither_seed
    )
    {
        short temp, temp1, i, dither_fac, rand_dith;
        short rand_dith2;

        /* Insert comfort noise dithering for energy parameter */
        rand_dith = shr(Random(ref dither_seed), 1);
        rand_dith2 = shr(Random(ref dither_seed), 1);
        rand_dith = add(rand_dith, rand_dith2);
        L_log_en_int = L_add(L_log_en_int, L_mult(rand_dith, GAIN_FACTOR));
        if (L_log_en_int < 0)
        {
            L_log_en_int = 0;
        }
        /* Insert comfort noise dithering for spectral parameters (ISF-vector) */
        dither_fac = ISF_FACTOR_LOW;

        rand_dith = shr(Random(ref dither_seed), 1);
        rand_dith2 = shr(Random(ref dither_seed), 1);
        rand_dith = add(rand_dith, rand_dith2);
        temp = add(isf[0], mult_r(rand_dith, dither_fac));

        /* Make sure that isf[0] will not get negative values */
        if (sub(temp, ISF_GAP) < 0)
        {
            isf[0] = ISF_GAP;
        }
        else
        {
            isf[0] = temp;
        }

        for (i = 1; i < M - 1; i++)
        {
            dither_fac = add(dither_fac, ISF_FACTOR_STEP);

            rand_dith = shr(Random(ref dither_seed), 1);
            rand_dith2 = shr(Random(ref dither_seed), 1);
            rand_dith = add(rand_dith, rand_dith2);
            temp = add(isf[i], mult_r(rand_dith, dither_fac));
            temp1 = sub(temp, isf[i - 1]);

            /* Make sure that isf spacing remains at least ISF_DITH_GAP Hz */
            if (sub(temp1, ISF_DITH_GAP) < 0)
            {
                isf[i] = add(isf[i - 1], ISF_DITH_GAP);
            }
            else
            {
                isf[i] = temp;
            }
        }

        /* Make sure that isf[M-2] will not get values above 16384 */
        if (sub(isf[M - 2], 16384) > 0)
        {
            isf[M - 2] = 16384;
        }
        return;
    }

}
