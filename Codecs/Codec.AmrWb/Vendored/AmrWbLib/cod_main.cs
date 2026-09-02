
namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    /*--------------------------------------------------------------------------*
     *                         COD_MAIN.H                                       *
     *--------------------------------------------------------------------------*
     *       Static memory in the encoder                                *
     *--------------------------------------------------------------------------*/

    private class Coder_State
    {
        public short[] mem_decim = new short[2 * L_FILT16k];       /* speech decimated filter memory */
        public short[] mem_sig_in = new short[6];                  /* hp50 filter memory */
        public short mem_preemph;                                  /* speech preemph filter memory */
        public short[] old_speech = new short[L_TOTAL - L_FRAME];  /* old speech vector at 12.8kHz */
        public short[] old_wsp = new short[PIT_MAX / OPL_DECIM];   /* old decimated weighted speech vector */
        public short[] old_exc = new short[PIT_MAX + L_INTERPOL];  /* old excitation vector */
        public short[] mem_levinson = new short[M + 2];            /* levinson routine memory */
        public short[] ispold = new short[M];                      /* old isp (immittance spectral pairs) */
        public short[] ispold_q = new short[M];                    /* quantized old isp */
        public short[] past_isfq = new short[M];                   /* past isf quantizer */
        public short mem_wsp;                                      /* wsp vector memory */
        public short[] mem_decim2 = new short[3];                  /* wsp decimation filter memory */
        public short mem_w0;                                       /* target vector memory */
        public short[] mem_syn = new short[M];                     /* synthesis memory */
        public short tilt_code;                                    /* tilt of code */
        public short old_wsp_max;                                  /* old wsp maximum value */
        public short old_wsp_shift;                                /* old wsp shift */
        public short Q_old;                                        /* old scaling factor */
        public short[] Q_max = new short[2];                       /* old maximum scaling factor */
        public short[] gp_clip = new short[2];                     /* gain of pitch clipping memory */
        public short[] qua_gain = new short[4];                    /* gain quantizer memory */

        public short old_T0_med;
        public short ol_gain;
        public short ada_w;
        public short ol_wght_flg;
        public short[] old_ol_lag = new short[5];
        public short[] hp_wsp_mem = new short[9];
        public short[] old_hp_wsp = new short[L_FRAME / OPL_DECIM + (PIT_MAX / OPL_DECIM)];
        public VadVars vadSt = new VadVars();
        public dtx_encState dtx_encSt = new dtx_encState();
        public short first_frame;

        public short[] isfold = new short[M];                      /* old isf (frequency domain) */
        public int L_gc_thres;                                     /* threshold for noise enhancer */
        public short[] mem_syn_hi = new short[M];                  /* modified synthesis memory (MSB) */
        public short[] mem_syn_lo = new short[M];                  /* modified synthesis memory (LSB) */
        public short mem_deemph;                                   /* speech deemph filter memory */
        public short[] mem_sig_out = new short[6];                 /* hp50 filter memory for synthesis */
        public short[] mem_hp400 = new short[6];                   /* hp400 filter memory for synthesis */
        public short[] mem_oversamp = new short[2 * L_FILT];       /* synthesis oversampled filter memory */
        public short[] mem_syn_hf = new short[M];                  /* HF synthesis memory */
        public short[] mem_hf = new short[2 * L_FILT16k];          /* HF band-pass filter memory */
        public short[] mem_hf2 = new short[2 * L_FILT16k];         /* HF band-pass filter memory */
        public short seed2;                                        /* random memory for HF generation */
        public short vad_hist;

        public short gain_alpha;

        public Coder_State() { }
    }

    /*------------------------------------------------------------------------*
     *                         COD_MAIN.C                                     *
    *------------------------------------------------------------------------*
    * Performs the main encoder routine                                      *
    *------------------------------------------------------------------------*/

    /*___________________________________________________________________________
     |                                                                           |
     | Fixed-point C simulation of AMR WB ACELP coding algorithm with 20 ms      |
     | speech frames for wideband speech signals.                                |
     |___________________________________________________________________________|
    */

    /* LPC interpolation coef {0.45, 0.8, 0.96, 1.0}; in Q15 */
    static short[] interpol_frac = new short[NB_SUBFR] { 14746, 26214, 31457, 32767 };

    /* isp tables for initialization */

    private static short[] isp_init = new short[M]
    {
       32138, 30274, 27246, 23170, 18205, 12540, 6393, 0,
       -6393, -12540, -18205, -23170, -27246, -30274, -32138, 1475
    };

    private static short[] isf_init = new short[M]
    {
       1024, 2048, 3072, 4096, 5120, 6144, 7168, 8192,
       9216, 10240, 11264, 12288, 13312, 14336, 15360, 3840
    };

    /* High Band encoding */
    private static short[] HP_gain = new short[16]
    {
       3624, 4673, 5597, 6479, 7425, 8378, 9324, 10264,
       11210, 12206, 13391, 14844, 16770, 19655, 24289, 32728
    };

    /*-----------------------------------------------------------------*
     *   Funtion  init_coder                                           *
     *            ~~~~~~~~~~                                           *
     *   ->Initialization of variables for the coder section.          *
     *-----------------------------------------------------------------*/

    //private void Init_coder(Coder_State st)
    //{
    //    //st.vadSt = NULL; move16();
    //    //st->dtx_encSt = NULL; move16();

    //    wb_vad_init(ref st.vadSt);
    //    dtx_enc_init(ref st.dtx_encSt, isf_init);

    //    Reset_encoder(st, 1);
    //}

    private void Reset_encoder(Coder_State st, short reset_all)
    {
        short i;

        Coder_State cod_state = st;

        Set_zero(cod_state.old_exc, PIT_MAX + L_INTERPOL);
        Set_zero(cod_state.mem_syn, M);
        Set_zero(cod_state.past_isfq, M);

        cod_state.mem_w0 = 0;
        cod_state.tilt_code = 0;
        cod_state.first_frame = 1;

        Init_gp_clip(cod_state.gp_clip);

        cod_state.L_gc_thres = 0;

        if (reset_all != 0)
        {
            /* Static vectors to zero */

            Set_zero(cod_state.old_speech, L_TOTAL - L_FRAME);
            Set_zero(cod_state.old_wsp, (PIT_MAX / OPL_DECIM));
            Set_zero(cod_state.mem_decim2, 3);

            /* routines initialization */

            Init_Decim_12k8(cod_state.mem_decim);
            Init_HP50_12k8(cod_state.mem_sig_in);
            Init_Levinson(cod_state.mem_levinson);
            Init_Q_gain2(cod_state.qua_gain);
            Init_Hp_wsp(cod_state.hp_wsp_mem);

            /* isp initialization */

            Copy(isp_init, cod_state.ispold, M);
            Copy(isp_init, cod_state.ispold_q, M);

            /* variable initialization */

            cod_state.mem_preemph = 0;
            cod_state.mem_wsp = 0;
            cod_state.Q_old = 15;
            cod_state.Q_max[0] = 15;
            cod_state.Q_max[1] = 15;
            cod_state.old_wsp_max = 0;
            cod_state.old_wsp_shift = 0;

            /* pitch ol initialization */

            cod_state.old_T0_med = 40;
            cod_state.ol_gain = 0;
            cod_state.ada_w = 0;
            cod_state.ol_wght_flg = 0;
            for (i = 0; i < 5; i++)
            {
                cod_state.old_ol_lag[i] = 40;
            }
            Set_zero(cod_state.old_hp_wsp, (L_FRAME / 2) / OPL_DECIM + (PIT_MAX / OPL_DECIM));

            Set_zero(cod_state.mem_syn_hf, M);
            Set_zero(cod_state.mem_syn_hi, M);
            Set_zero(cod_state.mem_syn_lo, M);

            Init_HP50_12k8(cod_state.mem_sig_out);
            Init_Filt_6k_7k(cod_state.mem_hf);
            Init_HP400_12k8(cod_state.mem_hp400);

            Copy(isf_init, cod_state.isfold, M);

            cod_state.mem_deemph = 0;

            cod_state.seed2 = 21845;

            Init_Filt_6k_7k(cod_state.mem_hf2);
            cod_state.gain_alpha = 32767;

            cod_state.vad_hist = 0;

            wb_vad_reset(cod_state.vadSt);
            dtx_enc_reset(cod_state.dtx_encSt, isf_init);
        }
        return;
    }

    /*-----------------------------------------------------------------*
     *   Funtion  coder                                                *
     *            ~~~~~                                                *
     *   ->Main coder routine.                                         *
     *                                                                 *
     *-----------------------------------------------------------------*/

    // 20 May 24 PHR -- For debug only
    private int CoderFrameCount = 0;

    private unsafe void coder(
         ref short mode,                     /* input :  used mode                             */
         short* speech16k,                   /* input :  320 new speech samples (at 16 kHz)    */
         short[] prms,                       /* output:  output parameters                     */
         ref short ser_size,                 /* output:  bit rate of the used mode             */
         Coder_State spe_state,              /* i/o   :  State structure                       */
         short allow_dtx)                    /* input :  DTX ON/OFF                            */
    {
        /* Coder states */
        Coder_State st;
        Span<short> speech16k_Span = new Span<short>(speech16k, 320);

        /* Speech vector */
        short* old_speech = stackalloc short[L_TOTAL];
        short* new_speech;
        short* speech;
        short* p_window;

        /* Weighted speech vector */
        short* old_wsp = stackalloc short[L_FRAME + (PIT_MAX / OPL_DECIM)];
        short* wsp;

        /* Excitation vector */
        short* old_exc = stackalloc short[(L_FRAME + 1) + PIT_MAX + L_INTERPOL];
        short* exc;

        /* LPC coefficients */
        short[] r_h = new short[M + 1];         /* Autocorrelations of windowed speech  */
        short[] r_l = new short[M + 1];
        short[] rc = new short[M];                          /* Reflection coefficients.             */
        short* Ap = stackalloc short[M + 1];                      /* A(z) with spectral expansion         */
        short[] ispnew = new short[M];                      /* immittance spectral pairs at 4nd sfr */
        short[] ispnew_q = new short[M];                    /* quantized ISPs at 4nd subframe       */
        short[] isf = new short[M];                         /* ISF (frequency domain) at 4nd sfr    */
        short* p_A;                    /* ptr to A(z) for the 4 subframes      */
        short* p_Aq;
        short* A = stackalloc short[NB_SUBFR * (M + 1)];          /* A(z) unquantized for the 4 subframes */
        short* Aq = stackalloc short[NB_SUBFR * (M + 1)];         /* A(z)   quantized for the 4 subframes */

        /* Other vectors */
        short* xn = stackalloc short[L_SUBFR];                    /* Target vector for pitch search     */
        short* xn2 = stackalloc short[L_SUBFR];                   /* Target vector for codebook search  */
        short* dn = stackalloc short[L_SUBFR];                    /* Correlation between xn2 and h1     */
        short* cn = stackalloc short[L_SUBFR];                    /* Target vector in residual domain   */

        short* h1 = stackalloc short[L_SUBFR];                    /* Impulse response vector            */
        short* h2 = stackalloc short[L_SUBFR];                    /* Impulse response vector            */
        short* code = stackalloc short[L_SUBFR];                  /* Fixed codebook excitation          */
        short* y1 = stackalloc short[L_SUBFR];                    /* Filtered adaptive excitation       */
        short* y2 = stackalloc short[L_SUBFR];                    /* Filtered adaptive excitation       */
        short* error = stackalloc short[M + L_SUBFR];             /* error of quantization              */
        short* synth = stackalloc short [L_SUBFR];                 /* 12.8kHz synthesis vector           */
        short* exc2 = stackalloc short[L_FRAME];                  /* excitation vector                  */
        short[] buf = new short[L_FRAME];                   /* VAD buffer                         */

        /* Scalars */
        short i, j, i_subfr, select, pit_flag, clip_gain, vad_flag;
        short codec_mode;
        short T_op, T_op2, T0, T0_min, T0_max, T0_frac, index;
        short gain_pit, gain_code;
        short* g_coeff = stackalloc short[4];
        short* g_coeff2 = stackalloc short[4];
        short tmp, gain1, gain2, exp, Q_new, mu, shift, max;
        short voice_fac;
        short[] indice = new short[8];

        int L_tmp, L_gain_code, L_max;

        short[] code2 = new short[L_SUBFR];                 /* Fixed codebook excitation  */
        short stab_fac, fac, gain_code_lo;

        short corr_gain;

        st = spe_state;

        ser_size = nb_of_bits[mode];
        codec_mode = mode;
        int CurrentPrmsIndex = 0;   // 28 Apr 25 PHR

        // 20 May 25 PHR -- For debug only
        CoderFrameCount += 1;
        if (CoderFrameCount == 103)
        {

        }

        /*--------------------------------------------------------------------------*
         *          Initialize pointers to speech vector.                           *
         *                                                                          *
         *                                                                          *
         *                    |-------|-------|-------|-------|-------|-------|     *
         *                     past sp   sf1     sf2     sf3     sf4    L_NEXT      *
         *                    <-------  Total speech buffer (L_TOTAL)   ------>     *
         *              old_speech                                                  *
         *                    <-------  LPC analysis window (L_WINDOW)  ------>     *
         *                    |       <-- present frame (L_FRAME) ---->             *
         *                   p_window |       <----- new speech (L_FRAME) ---->     *
         *                            |       |                                     *
         *                          speech    |                                     *
         *                                 new_speech                               *
         *--------------------------------------------------------------------------*/

        new_speech = old_speech + L_TOTAL - L_FRAME - L_FILT;  /* New speech     */
        speech = old_speech + L_TOTAL - L_FRAME - L_NEXT;  /* Present frame  */
        p_window = old_speech + L_TOTAL - L_WINDOW;

        exc = old_exc + PIT_MAX + L_INTERPOL;
        wsp = old_wsp + (PIT_MAX / OPL_DECIM);

        /* copy coder memory state into working space (internal memory for DSP) */
        Copy(st.old_speech, old_speech, L_TOTAL - L_FRAME);
        Copy(st.old_wsp, old_wsp, PIT_MAX / OPL_DECIM);
        Copy(st.old_exc, old_exc, PIT_MAX + L_INTERPOL);

        /*---------------------------------------------------------------*
         * Down sampling signal from 16kHz to 12.8kHz                    *
         * -> The signal is extended by L_FILT samples (padded to zero)  *
         * to avoid additional delay (L_FILT samples) in the coder.      *
         * The last L_FILT samples are approximated after decimation and *
         * are used (and windowed) only in autocorrelations.             *
         *---------------------------------------------------------------*/

        Decim_12k8(speech16k, L_FRAME16k, new_speech, st.mem_decim);

        /* last L_FILT samples for autocorrelation window */
        Copy(st.mem_decim, code, 2 * L_FILT16k);
        Set_zero(error, L_FILT16k);            /* set next sample to zero */
        Decim_12k8(error, L_FILT16k, new_speech + L_FRAME, code);

        /*---------------------------------------------------------------*
         * Perform 50Hz HP filtering of input signal.                    *
         *---------------------------------------------------------------*/
        HP50_12k8(new_speech, L_FRAME, st.mem_sig_in);

        /* last L_FILT samples for autocorrelation window */
        Copy(st.mem_sig_in, code, 6);
        HP50_12k8(new_speech + L_FRAME, L_FILT, code);

        /*---------------------------------------------------------------*
         * Perform fixed preemphasis through 1 - g z^-1                  *
         * Scale signal to get maximum of precision in filtering         *
         *---------------------------------------------------------------*/
        mu = shr(PREEMPH_FAC, 1);              /* Q15 --> Q14 */

        /* get max of new preemphased samples (L_FRAME+L_FILT) */
        L_tmp = L_mult(new_speech[0], 16384);
        L_tmp = L_msu(L_tmp, st.mem_preemph, mu);
        L_max = L_abs(L_tmp);

        for (i = 1; i < L_FRAME + L_FILT; i++)
        {
            L_tmp = L_mult(new_speech[i], 16384);
            L_tmp = L_msu(L_tmp, new_speech[i - 1], mu);
            L_tmp = L_abs(L_tmp);
            if (L_sub(L_tmp, L_max) > 0)
            {
                L_max = L_tmp;
            }
        }

        /* get scaling factor for new and previous samples */
        /* limit scaling to Q_MAX to keep dynamic for ringing in low signal */
        /* limit scaling to Q_MAX also to avoid a[0]<1 in syn_filt_32 */
        tmp = extract_h(L_max);
        if (tmp == 0)
        {
            shift = Q_MAX;
        }
        else
        {
            shift = sub(norm_s(tmp), 1);
            if (shift < 0)
            {
                shift = 0;
            }
            if (sub(shift, Q_MAX) > 0)
            {
                shift = Q_MAX;
            }
        }
        Q_new = shift;
        if (sub(Q_new, st.Q_max[0]) > 0)
        {
            Q_new = st.Q_max[0];
        }
        
        if (sub(Q_new, st.Q_max[1]) > 0)
        {
            Q_new = st.Q_max[1];
        }
        exp = sub(Q_new, st.Q_old);
        st.Q_old = Q_new;
        st.Q_max[1] = st.Q_max[0];
        st.Q_max[0] = shift;

        /* preemphasis with scaling (L_FRAME+L_FILT) */

        tmp = new_speech[L_FRAME - 1];

        for (i = L_FRAME + L_FILT - 1; i > 0; i--)
        {
            L_tmp = L_mult(new_speech[i], 16384);
            L_tmp = L_msu(L_tmp, new_speech[i - 1], mu);
            L_tmp = L_shl(L_tmp, Q_new);
            new_speech[i] = round(L_tmp);
        }

        L_tmp = L_mult(new_speech[0], 16384);
        L_tmp = L_msu(L_tmp, st.mem_preemph, mu);
        L_tmp = L_shl(L_tmp, Q_new);
        new_speech[0] = round(L_tmp);

        st.mem_preemph = tmp;

        /* scale previous samples and memory */

        Scale_sig(old_speech, L_TOTAL - L_FRAME - L_FILT, exp);
        Scale_sig(old_exc, PIT_MAX + L_INTERPOL, exp);

        Scale_sig(st.mem_syn, M, exp);
        Scale_sig(st.mem_decim2, 3, exp);
        Scale_sig(ref st.mem_wsp, 1, exp);
        //Scale_sig(&(st.mem_w0), 1, exp);
        Scale_sig(ref st.mem_w0, 1, exp);

        /*------------------------------------------------------------------------*
         *  Call VAD                                                              *
         *  Preemphesis scale down signal in low frequency and keep dynamic in HF.*
         *  Vad work slightly in futur (new_speech = speech + L_NEXT - L_FILT).   *
         *------------------------------------------------------------------------*/

        Copy(new_speech, buf, L_FRAME);

        Scale_sig(buf, L_FRAME, sub(1, Q_new));

        vad_flag = wb_vad(st.vadSt, buf);
        if (vad_flag == 0)
        {
            st.vad_hist = add(st.vad_hist, 1);
        }
        else
        {
            st.vad_hist = 0;
        }

        /* DTX processing */
        if (allow_dtx != 0)
        {
            /* Note that mode may change here */
            tx_dtx_handler(st.dtx_encSt, vad_flag, ref mode);
            ser_size = nb_of_bits[mode];
        }

        if (sub(mode, MRDTX) != 0)
        {
            Parm_serial(vad_flag, 1, prms, ref CurrentPrmsIndex);
        }

        /*------------------------------------------------------------------------*
         *  Perform LPC analysis                                                  *
         *  ~~~~~~~~~~~~~~~~~~~~                                                  *
         *   - autocorrelation + lag windowing                                    *
         *   - Levinson-durbin algorithm to find a[]                              *
         *   - convert a[] to isp[]                                               *
         *   - convert isp[] to isf[] for quantization                            *
         *   - quantize and code the isf[]                                        *
         *   - convert isf[] to isp[] for interpolation                           *
         *   - find the interpolated ISPs and convert to a[] for the 4 subframes  *
         *------------------------------------------------------------------------*/

        /* LP analysis centered at 4nd subframe */
        Autocorr(p_window, M, r_h, r_l);       /* Autocorrelations */
        Lag_window(r_h, r_l);                  /* Lag windowing    */
        Levinson(r_h, r_l, A, rc, st.mem_levinson);        /* Levinson Durbin  */

        Az_isp(A, ispnew, st.ispold);         /* From A(z) to ISP */

        /* Find the interpolated ISPs and convert to a[] for all subframes */
        Int_isp(st.ispold, ispnew, interpol_frac, A);

        /* update ispold[] for the next frame */
        Copy(ispnew, st.ispold, M);

        /* Convert ISPs to frequency domain 0..6400 */
        Isp_isf(ispnew, isf, M);

        /* check resonance for pitch clipping algorithm */
        Gp_clip_test_isf(ser_size, isf, st.gp_clip);

        /*----------------------------------------------------------------------*
         *  Perform PITCH_OL analysis                                           *
         *  ~~~~~~~~~~~~~~~~~~~~~~~~~                                           *
         * - Find the residual res[] for the whole speech frame                 *
         * - Find the weighted input speech wsp[] for the whole speech frame    *
         * - scale wsp[] to avoid overflow in pitch estimation                  *
         * - Find open loop pitch lag for whole speech frame                    *
         *----------------------------------------------------------------------*/

        p_A = A;
        for (i_subfr = 0; i_subfr < L_FRAME; i_subfr += L_SUBFR)
        {
            Weight_a(p_A, Ap, GAMMA1, M);
            Residu(Ap, M, &speech[i_subfr], &wsp[i_subfr], L_SUBFR);
            p_A += (M + 1);
        }

        Deemph2(wsp, TILT_FAC, L_FRAME, ref st.mem_wsp);

        /* find maximum value on wsp[] for 12 bits scaling */
        max = 0;
        for (i = 0; i < L_FRAME; i++)
        {
            tmp = abs_s(wsp[i]);
            if (sub(tmp, max) > 0)
            {
                max = tmp;
            }
        }
        tmp = st.old_wsp_max;
        if (sub(max, tmp) > 0)
        {
            tmp = max;                         /* tmp = max(wsp_max, old_wsp_max) */
        }
        st.old_wsp_max = max;

        shift = sub(norm_s(tmp), 3);
        if (shift > 0)
        {
            shift = 0;                         /* shift = 0..-3 */
        }
        /* decimation of wsp[] to search pitch in LF and to reduce complexity */
        LP_Decim2(wsp, L_FRAME, st.mem_decim2);

        /* scale wsp[] in 12 bits to avoid overflow */
        Scale_sig(wsp, L_FRAME / OPL_DECIM, shift);

        /* scale old_wsp (warning: exp must be Q_new-Q_old) */
        exp = add(exp, sub(shift, st.old_wsp_shift));
        st.old_wsp_shift = shift;
        Scale_sig(old_wsp, PIT_MAX / OPL_DECIM, exp);
        Scale_sig(st.old_hp_wsp, PIT_MAX / OPL_DECIM, exp);
        scale_mem_Hp_wsp(st.hp_wsp_mem, exp);

        /* Find open loop pitch lag for whole speech frame */

        if (sub(ser_size, NBBITS_7k) == 0)
        {
            /* Find open loop pitch lag for whole speech frame */
            T_op = Pitch_med_ol(wsp, PIT_MIN / OPL_DECIM, PIT_MAX / OPL_DECIM,
                L_FRAME / OPL_DECIM, st.old_T0_med, ref st.ol_gain, st.hp_wsp_mem, st.old_hp_wsp, st.ol_wght_flg);
        }
        else
        {
            /* Find open loop pitch lag for first 1/2 frame */
            T_op = Pitch_med_ol(wsp, PIT_MIN / OPL_DECIM, PIT_MAX / OPL_DECIM,
                (L_FRAME / 2) / OPL_DECIM, st.old_T0_med, ref st.ol_gain, st.hp_wsp_mem, st.old_hp_wsp, st.ol_wght_flg);
        }

        if (sub(st.ol_gain, 19661) > 0)       /* 0.6 in Q15 */
        {
            st.old_T0_med = Med_olag(T_op, st.old_ol_lag);
            st.ada_w = 32767;
        }
        else
        {
            st.ada_w = mult(st.ada_w, 29491);
        }

        if (sub(st.ada_w, 26214) < 0)
            st.ol_wght_flg = 0;
        else
            st.ol_wght_flg = 1;

        wb_vad_tone_detection(st.vadSt, st.ol_gain);

        T_op *= OPL_DECIM;

        if (sub(ser_size, NBBITS_7k) != 0)
        {
            /* Find open loop pitch lag for second 1/2 frame */
            T_op2 = Pitch_med_ol(wsp + ((L_FRAME / 2) / OPL_DECIM), PIT_MIN / OPL_DECIM, PIT_MAX / OPL_DECIM,
                (L_FRAME / 2) / OPL_DECIM, st.old_T0_med, ref st.ol_gain, st.hp_wsp_mem, st.old_hp_wsp, st.ol_wght_flg);

            if (sub(st.ol_gain, 19661) > 0)   /* 0.6 in Q15 */
            {
                st.old_T0_med = Med_olag(T_op2, st.old_ol_lag);
                st.ada_w = 32767;
            }
            else
            {
                st.ada_w = mult(st.ada_w, 29491);
            }

            if (sub(st.ada_w, 26214) < 0)
                st.ol_wght_flg = 0;
            else
                st.ol_wght_flg = 1;

            wb_vad_tone_detection(st.vadSt, st.ol_gain);

            T_op2 *= OPL_DECIM;
        }
        else
        {
            T_op2 = T_op;
        }

        /*----------------------------------------------------------------------*
         *                              DTX-CNG                                 *
         *----------------------------------------------------------------------*/

        if (sub(mode, MRDTX) == 0)            /* CNG mode */
        {
            /* Buffer isf's and energy */
            Residu(&A[3 * (M + 1)], M, speech, exc, L_FRAME);

            for (i = 0; i < L_FRAME; i++)
            {
                exc2[i] = shr(exc[i], Q_new);
            }

            L_tmp = 0;
            for (i = 0; i < L_FRAME; i++)
                L_tmp = L_mac(L_tmp, exc2[i], exc2[i]);
            L_tmp = L_shr(L_tmp, 1);

            dtx_buffer(st.dtx_encSt, isf, L_tmp, codec_mode);

            /* Quantize and code the ISFs */
            dtx_enc(st.dtx_encSt, isf, exc2, prms, ref CurrentPrmsIndex);

            /* Convert ISFs to the cosine domain */
            Isf_isp(isf, ispnew_q, M);
            Isp_Az(ispnew_q, Aq, M, 0);

            for (i_subfr = 0; i_subfr < L_FRAME; i_subfr += L_SUBFR)
            {
                corr_gain = synthesis(Aq, &exc2[i_subfr], 0, &speech16k[i_subfr * 5 / 4], st);
            }
            Copy(isf, st.isfold, M);


            /* reset speech coder memories */
            Reset_encoder(st, 0);

            /*--------------------------------------------------*
             * Update signal for next frame.                    *
             * -> save past of speech[] and wsp[].              *
             *--------------------------------------------------*/

            Copy(&old_speech[L_FRAME], st.old_speech, L_TOTAL - L_FRAME);
            Copy(&old_wsp[L_FRAME / OPL_DECIM], st.old_wsp, PIT_MAX / OPL_DECIM);

            return;
        }
        /*----------------------------------------------------------------------*
         *                               ACELP                                  *
         *----------------------------------------------------------------------*/

        /* Quantize and code the ISFs */

        if (sub(ser_size, NBBITS_7k) <= 0)
        {
            Qpisf_2s_36b(isf, isf, st.past_isfq, indice, 4);

            Parm_serial(indice[0], 8, prms, ref CurrentPrmsIndex);
            Parm_serial(indice[1], 8, prms, ref CurrentPrmsIndex);
            Parm_serial(indice[2], 7, prms, ref CurrentPrmsIndex);
            Parm_serial(indice[3], 7, prms, ref CurrentPrmsIndex);
            Parm_serial(indice[4], 6, prms, ref CurrentPrmsIndex);
        }
        else
        {
            Qpisf_2s_46b(isf, isf, st.past_isfq, indice, 4);

            Parm_serial(indice[0], 8, prms, ref CurrentPrmsIndex);
            Parm_serial(indice[1], 8, prms, ref CurrentPrmsIndex);
            Parm_serial(indice[2], 6, prms, ref CurrentPrmsIndex);
            Parm_serial(indice[3], 7, prms, ref CurrentPrmsIndex);
            Parm_serial(indice[4], 7, prms, ref CurrentPrmsIndex);
            Parm_serial(indice[5], 5, prms, ref CurrentPrmsIndex);
            Parm_serial(indice[6], 5, prms, ref CurrentPrmsIndex);
        }

        /* Check stability on isf : distance between old isf and current isf */

        L_tmp = 0;
        for (i = 0; i < M - 1; i++)
        {
            tmp = sub(isf[i], st.isfold[i]);
            L_tmp = L_mac(L_tmp, tmp, tmp);
        }

        tmp = extract_h(L_shl(L_tmp, 8));      /* saturation can occur here */

        tmp = mult(tmp, 26214);                /* tmp = L_tmp*0.8/256 */
        tmp = sub(20480, tmp);                 /* 1.25 - tmp (in Q14) */

        stab_fac = shl(tmp, 1);                /* saturation can occur here */

        if (stab_fac < 0)
        {
            stab_fac = 0;
        }
        Copy(isf, st.isfold, M);

        /* Convert ISFs to the cosine domain */
        Isf_isp(isf, ispnew_q, M);

        if (st.first_frame != 0)
        {
            st.first_frame = 0;
            Copy(ispnew_q, st.ispold_q, M);
        }
        /* Find the interpolated ISPs and convert to a[] for all subframes */

        Int_isp(st.ispold_q, ispnew_q, interpol_frac, Aq);

        /* update ispold[] for the next frame */
        Copy(ispnew_q, st.ispold_q, M);

        p_Aq = Aq;
        for (i_subfr = 0; i_subfr < L_FRAME; i_subfr += L_SUBFR)
        {
            Residu(p_Aq, M, &speech[i_subfr], &exc[i_subfr], L_SUBFR);
            p_Aq += (M + 1);
        }

        /* Buffer isf's and energy for dtx on non-speech frame */

        if (vad_flag == 0)
        {
            for (i = 0; i < L_FRAME; i++)
            {
                exc2[i] = shr(exc[i], Q_new);
            }
            L_tmp = 0;
            for (i = 0; i < L_FRAME; i++)
                L_tmp = L_mac(L_tmp, exc2[i], exc2[i]);
            L_tmp = L_shr(L_tmp, 1);

            dtx_buffer(st.dtx_encSt, isf, L_tmp, codec_mode);
        }
        /* range for closed loop pitch search in 1st subframe */

        T0_min = sub(T_op, 8);
        if (sub(T0_min, PIT_MIN) < 0)
        {
            T0_min = PIT_MIN;
        }
        T0_max = add(T0_min, 15);
        
        if (sub(T0_max, PIT_MAX) > 0)
        {
            T0_max = PIT_MAX;
            T0_min = sub(T0_max, 15);
        }
        /*------------------------------------------------------------------------*
         *          Loop for every subframe in the analysis frame                 *
         *------------------------------------------------------------------------*
         *  To find the pitch and innovation parameters. The subframe size is     *
         *  L_SUBFR and the loop is repeated L_FRAME/L_SUBFR times.               *
         *     - compute the target signal for pitch search                       *
         *     - compute impulse response of weighted synthesis filter (h1[])     *
         *     - find the closed-loop pitch parameters                            *
         *     - encode the pitch dealy                                           *
         *     - find 2 lt prediction (with / without LP filter for lt pred)      *
         *     - find 2 pitch gains and choose the best lt prediction.            *
         *     - find target vector for codebook search                           *
         *     - update the impulse response h1[] for codebook search             *
         *     - correlation between target vector and impulse response           *
         *     - codebook search and encoding                                     *
         *     - VQ of pitch and codebook gains                                   *
         *     - find voicing factor and tilt of code for next subframe.          *
         *     - update states of weighting filter                                *
         *     - find excitation and synthesis speech                             *
         *------------------------------------------------------------------------*/

        p_A = A;
        p_Aq = Aq;

        for (i_subfr = 0; i_subfr < L_FRAME; i_subfr += L_SUBFR)
        {
            pit_flag = i_subfr;
            
            if ((sub(i_subfr, 2 * L_SUBFR) == 0) && (sub(ser_size, NBBITS_7k) > 0))
            {
                pit_flag = 0;

                /* range for closed loop pitch search in 3rd subframe */

                T0_min = sub(T_op2, 8);
                if (sub(T0_min, PIT_MIN) < 0)
                {
                    T0_min = PIT_MIN;
                }
                T0_max = add(T0_min, 15);
                
                if (sub(T0_max, PIT_MAX) > 0)
                {
                    T0_max = PIT_MAX;
                    T0_min = sub(T0_max, 15);
                }
            }
            /*-----------------------------------------------------------------------*
             *                                                                       *
             *        Find the target vector for pitch search:                       *
             *        ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~                        *
             *                                                                       *
             *             |------|  res[n]                                          *
             * speech[n]---| A(z) |--------                                          *
             *             |------|       |   |--------| error[n]  |------|          *
             *                   zero -- (-)--| 1/A(z) |-----------| W(z) |-- target *
             *                   exc          |--------|           |------|          *
             *                                                                       *
             * Instead of subtracting the zero-input response of filters from        *
             * the weighted input speech, the above configuration is used to         *
             * compute the target vector.                                            *
             *                                                                       *
             *-----------------------------------------------------------------------*/

            for (i = 0; i < M; i++)
            {
                error[i] = sub(speech[i + i_subfr - M], st.mem_syn[i]);
            }
            Residu(p_Aq, M, &speech[i_subfr], &exc[i_subfr], L_SUBFR);

            Syn_filt(p_Aq, M, &exc[i_subfr], error + M, L_SUBFR, error, 0);

            Weight_a(p_A, Ap, GAMMA1, M);
            Residu(Ap, M, error + M, xn, L_SUBFR);

            Deemph2(xn, TILT_FAC, L_SUBFR, ref st.mem_w0);

            /*----------------------------------------------------------------------*
             * Find approx. target in residual domain "cn[]" for inovation search.  *
             *----------------------------------------------------------------------*/

            /* first half: xn[] --> cn[] */
            Set_zero(code, M);
            Copy(xn, code + M, L_SUBFR / 2);
            tmp = 0;
            Preemph2(code + M, TILT_FAC, L_SUBFR / 2, &tmp);
            Weight_a(p_A, Ap, GAMMA1, M);
            Syn_filt(Ap, M, code + M, code + M, L_SUBFR / 2, code, 0);
            Residu(p_Aq, M, code + M, cn, L_SUBFR / 2);

            /* second half: res[] --> cn[] (approximated and faster) */
            Copy(&exc[i_subfr + (L_SUBFR / 2)], cn + (L_SUBFR / 2), L_SUBFR / 2);

            /*---------------------------------------------------------------*
             * Compute impulse response, h1[], of weighted synthesis filter  *
             *---------------------------------------------------------------*/

            Set_zero(error, M + L_SUBFR);
            Weight_a(p_A, error + M, GAMMA1, M);

            for (i = 0; i < L_SUBFR; i++)
            {
                L_tmp = L_mult(error[i + M], 16384);        /* x4 (Q12 to Q14) */
                for (j = 1; j <= M; j++)
                    L_tmp = L_msu(L_tmp, p_Aq[j], error[i + M - j]);

                //h1[i] = error[i + M] = round(L_shl(L_tmp, 3));
                error[i + M] = round(L_shl(L_tmp, 3));  // 10 May 25 PHR
                h1[i] = error[i + M];
            }
            /* deemph without division by 2 -> Q14 to Q15 */
            tmp = 0;
            Deemph2(h1, TILT_FAC, L_SUBFR, ref tmp);   /* h1 in Q14 */

            /* h2 in Q12 for codebook search */
            Copy(h1, h2, L_SUBFR);
            Scale_sig(h2, L_SUBFR, -2);

            /*---------------------------------------------------------------*
             * scale xn[] and h1[] to avoid overflow in dot_product12()      *
             *---------------------------------------------------------------*/

            Scale_sig(xn, L_SUBFR, shift);     /* scaling of xn[] to limit dynamic at 12 bits */
            Scale_sig(h1, L_SUBFR, add(1, shift));  /* set h1[] in Q15 with scaling for convolution */

            /*----------------------------------------------------------------------*
             *                 Closed-loop fractional pitch search                  *
             *----------------------------------------------------------------------*/

            /* find closed loop fractional pitch  lag */
            T0_frac = 0;
            if (sub(ser_size, NBBITS_9k) <= 0)
            {
                T0 = Pitch_fr4(&exc[i_subfr], xn, h1, T0_min, T0_max, ref T0_frac,
                    pit_flag, PIT_MIN, PIT_FR1_8b, L_SUBFR);

                /* encode pitch lag */

                if (pit_flag == 0)             /* if 1st/3rd subframe */
                {
                    /*--------------------------------------------------------------*
                     * The pitch range for the 1st/3rd subframe is encoded with     *
                     * 8 bits and is divided as follows:                            *
                     *   PIT_MIN to PIT_FR1-1  resolution 1/2 (frac = 0 or 2)       *
                     *   PIT_FR1 to PIT_MAX    resolution 1   (frac = 0)            *
                     *--------------------------------------------------------------*/

                    if (sub(T0, PIT_FR1_8b) < 0)
                    {
                        index = sub(add(shl(T0, 1), shr(T0_frac, 1)), (PIT_MIN * 2));
                    }
                    else
                    {
                        index = add(sub(T0, PIT_FR1_8b), ((PIT_FR1_8b - PIT_MIN) * 2));
                    }

                    Parm_serial(index, 8, prms, ref CurrentPrmsIndex);

                    /* find T0_min and T0_max for subframe 2 and 4 */

                    T0_min = sub(T0, 8);
                    if (sub(T0_min, PIT_MIN) < 0)
                    {
                        T0_min = PIT_MIN;
                    }
                    T0_max = add(T0_min, 15);
                    
                    if (sub(T0_max, PIT_MAX) > 0)
                    {
                        T0_max = PIT_MAX;
                        T0_min = sub(T0_max, 15);
                    }
                }
                else
                {                              /* if subframe 2 or 4 */
                    /*--------------------------------------------------------------*
                     * The pitch range for subframe 2 or 4 is encoded with 5 bits:  *
                     *   T0_min  to T0_max     resolution 1/2 (frac = 0 or 2)       *
                     *--------------------------------------------------------------*/

                    i = sub(T0, T0_min);
                    index = add(shl(i, 1), shr(T0_frac, 1));

                    Parm_serial(index, 5, prms, ref CurrentPrmsIndex);
                }
            }
            else
            {
                T0 = Pitch_fr4(&exc[i_subfr], xn, h1, T0_min, T0_max, ref T0_frac,
                    pit_flag, PIT_FR2, PIT_FR1_9b, L_SUBFR);

                /* encode pitch lag */

                if (pit_flag == 0)             /* if 1st/3rd subframe */
                {
                    /*--------------------------------------------------------------*
                     * The pitch range for the 1st/3rd subframe is encoded with     *
                     * 9 bits and is divided as follows:                            *
                     *   PIT_MIN to PIT_FR2-1  resolution 1/4 (frac = 0,1,2 or 3)   *
                     *   PIT_FR2 to PIT_FR1-1  resolution 1/2 (frac = 0 or 1)       *
                     *   PIT_FR1 to PIT_MAX    resolution 1   (frac = 0)            *
                     *--------------------------------------------------------------*/

                    if (sub(T0, PIT_FR2) < 0)
                    {
                        index = sub(add(shl(T0, 2), T0_frac), (PIT_MIN * 4));
                    }
                    else if (sub(T0, PIT_FR1_9b) < 0)
                    {
                        index = add(sub(add(shl(T0, 1), shr(T0_frac, 1)), (PIT_FR2 * 2)), ((PIT_FR2 - PIT_MIN) * 4));
                    }
                    else
                    {
                        index = add(add(sub(T0, PIT_FR1_9b), ((PIT_FR2 - PIT_MIN) * 4)), ((PIT_FR1_9b - PIT_FR2) * 2));
                    }

                    Parm_serial(index, 9, prms, ref CurrentPrmsIndex);

                    /* find T0_min and T0_max for subframe 2 and 4 */

                    T0_min = sub(T0, 8);
                    if (sub(T0_min, PIT_MIN) < 0)
                    {
                        T0_min = PIT_MIN;
                    }
                    T0_max = add(T0_min, 15);
                    
                    if (sub(T0_max, PIT_MAX) > 0)
                    {
                        T0_max = PIT_MAX;
                        T0_min = sub(T0_max, 15);
                    }
                }
                else
                {                              /* if subframe 2 or 4 */
                    /*--------------------------------------------------------------*
                     * The pitch range for subframe 2 or 4 is encoded with 6 bits:  *
                     *   T0_min  to T0_max     resolution 1/4 (frac = 0,1,2 or 3)   *
                     *--------------------------------------------------------------*/

                    i = sub(T0, T0_min);
                    index = add(shl(i, 2), T0_frac);

                    Parm_serial(index, 6, prms, ref CurrentPrmsIndex);
                }
            }

            /*-----------------------------------------------------------------*
             * Gain clipping test to avoid unstable synthesis on frame erasure *
             *-----------------------------------------------------------------*/

            clip_gain = Gp_clip(ser_size, st.gp_clip);

            /*-----------------------------------------------------------------*
             * - find unity gain pitch excitation (adaptive codebook entry)    *
             *   with fractional interpolation.                                *
             * - find filtered pitch exc. y1[]=exc[] convolved with h1[])      *
             * - compute pitch gain1                                           *
             *-----------------------------------------------------------------*/

            /* find pitch exitation */

            Pred_lt4(&exc[i_subfr], T0, T0_frac, L_SUBFR + 1);
            if (sub(ser_size, NBBITS_9k) > 0)
            {
                Convolve(&exc[i_subfr], h1, y1, L_SUBFR);
                gain1 = G_pitch(xn, y1, g_coeff, L_SUBFR);

                /* clip gain if necessary to avoid problem at decoder */
                if ((clip_gain != 0) && (sub(gain1, GP_CLIP) > 0))
                {
                    gain1 = GP_CLIP;
                }
                /* find energy of new target xn2[] */
                Updt_tar(xn, dn, y1, gain1, L_SUBFR);       /* dn used temporary */
            }
            else
            {
                gain1 = 0;
            }

            /*-----------------------------------------------------------------*
             * - find pitch excitation filtered by 1st order LP filter.        *
             * - find filtered pitch exc. y2[]=exc[] convolved with h1[])      *
             * - compute pitch gain2                                           *
             *-----------------------------------------------------------------*/

            /* find pitch excitation with lp filter */
            for (i = 0; i < L_SUBFR; i++)
            {
                L_tmp = L_mult(5898, exc[i - 1 + i_subfr]);
                L_tmp = L_mac(L_tmp, 20972, exc[i + i_subfr]);
                L_tmp = L_mac(L_tmp, 5898, exc[i + 1 + i_subfr]);
                code[i] = round(L_tmp);
            }

            Convolve(code, h1, y2, L_SUBFR);
            gain2 = G_pitch(xn, y2, g_coeff2, L_SUBFR);

            /* clip gain if necessary to avoid problem at decoder */
            if ((clip_gain != 0) && (sub(gain2, GP_CLIP) > 0))
            {
                gain2 = GP_CLIP;
            }
            /* find energy of new target xn2[] */
            Updt_tar(xn, xn2, y2, gain2, L_SUBFR);

            /*-----------------------------------------------------------------*
             * use the best prediction (minimise quadratic error).             *
             *-----------------------------------------------------------------*/

            select = 0;
            if (sub(ser_size, NBBITS_9k) > 0)
            {
                L_tmp = 0;
                for (i = 0; i < L_SUBFR; i++)
                    L_tmp = L_mac(L_tmp, dn[i], dn[i]);
                for (i = 0; i < L_SUBFR; i++)
                    L_tmp = L_msu(L_tmp, xn2[i], xn2[i]);

                if (L_tmp <= 0)
                {
                    select = 1;
                }
                Parm_serial(select, 1, prms, ref CurrentPrmsIndex);
            }
            
            if (select == 0)
            {
                /* use the lp filter for pitch excitation prediction */
                gain_pit = gain2;
                Copy(code, &exc[i_subfr], L_SUBFR);
                Copy(y2, y1, L_SUBFR);
                Copy(g_coeff2, g_coeff, 4);
            }
            else
            {
                /* no filter used for pitch excitation prediction */
                gain_pit = gain1;
                Copy(dn, xn2, L_SUBFR);        /* target vector for codebook search */
            }

            /*-----------------------------------------------------------------*
             * - update cn[] for codebook search                               *
             *-----------------------------------------------------------------*/

            Updt_tar(cn, cn, &exc[i_subfr], gain_pit, L_SUBFR);

            Scale_sig(cn, L_SUBFR, shift);     /* scaling of cn[] to limit dynamic at 12 bits */

            /*-----------------------------------------------------------------*
             * - include fixed-gain pitch contribution into impulse resp. h1[] *
             *-----------------------------------------------------------------*/

            tmp = 0;
            Preemph(h2, st.tilt_code, L_SUBFR, ref tmp);

            if (T0_frac > 2)
                T0 = add(T0, 1);
            Pit_shrp(h2, T0, PIT_SHARP, L_SUBFR);

            /*-----------------------------------------------------------------*
             * - Correlation between target xn2[] and impulse response h1[]    *
             * - Innovative codebook search                                    *
             *-----------------------------------------------------------------*/

            cor_h_x(h2, xn2, dn);

            if (sub(ser_size, NBBITS_7k) <= 0)
            {
                ACELP_2t64_fx(dn, cn, h2, code, y2, ref indice[0]);

                Parm_serial(indice[0], 12, prms, ref CurrentPrmsIndex);
            }
            else if (sub(ser_size, NBBITS_9k) <= 0)
            {
                ACELP_4t64_fx(dn, cn, h2, code, y2, 20, ser_size, indice);

                Parm_serial(indice[0], 5, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[1], 5, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[2], 5, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[3], 5, prms, ref CurrentPrmsIndex);
            }
            else if (sub(ser_size, NBBITS_12k) <= 0)
            {
                ACELP_4t64_fx(dn, cn, h2, code, y2, 36, ser_size, indice);

                Parm_serial(indice[0], 9, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[1], 9, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[2], 9, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[3], 9, prms, ref CurrentPrmsIndex);
            }
            else if (sub(ser_size, NBBITS_14k) <= 0)
            {
                ACELP_4t64_fx(dn, cn, h2, code, y2, 44, ser_size, indice);

                Parm_serial(indice[0], 13, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[1], 13, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[2], 9, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[3], 9, prms, ref CurrentPrmsIndex);
            }
            else if (sub(ser_size, NBBITS_16k) <= 0)
            {
                ACELP_4t64_fx(dn, cn, h2, code, y2, 52, ser_size, indice);

                Parm_serial(indice[0], 13, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[1], 13, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[2], 13, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[3], 13, prms, ref CurrentPrmsIndex);
            }
            else if (sub(ser_size, NBBITS_18k) <= 0)
            {
                ACELP_4t64_fx(dn, cn, h2, code, y2, 64, ser_size, indice);

                Parm_serial(indice[0], 2, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[1], 2, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[2], 2, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[3], 2, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[4], 14, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[5], 14, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[6], 14, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[7], 14, prms, ref CurrentPrmsIndex);
            }
            else if (sub(ser_size, NBBITS_20k) <= 0)
            {
                ACELP_4t64_fx(dn, cn, h2, code, y2, 72, ser_size, indice);

                Parm_serial(indice[0], 10, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[1], 10, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[2], 2, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[3], 2, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[4], 10, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[5], 10, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[6], 14, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[7], 14, prms, ref CurrentPrmsIndex);
            }
            else
            {
                ACELP_4t64_fx(dn, cn, h2, code, y2, 88, ser_size, indice);

                Parm_serial(indice[0], 11, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[1], 11, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[2], 11, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[3], 11, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[4], 11, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[5], 11, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[6], 11, prms, ref CurrentPrmsIndex);
                Parm_serial(indice[7], 11, prms, ref CurrentPrmsIndex);
            }

            /*-------------------------------------------------------*
             * - Add the fixed-gain pitch contribution to code[].    *
             *-------------------------------------------------------*/

            tmp = 0;
            Preemph(code, st.tilt_code, L_SUBFR, ref tmp);

            Pit_shrp(code, T0, PIT_SHARP, L_SUBFR);

            /*----------------------------------------------------------*
             *  - Compute the fixed codebook gain                       *
             *  - quantize fixed codebook gain                          *
             *----------------------------------------------------------*/

            if (sub(ser_size, NBBITS_9k) <= 0)
            {
                index = Q_gain2(xn, y1, add(Q_new, shift), y2, code, g_coeff, L_SUBFR, 6,
                    &gain_pit, &L_gain_code, clip_gain, st.qua_gain);
                Parm_serial(index, 6, prms, ref CurrentPrmsIndex);
            }
            else
            {
                index = Q_gain2(xn, y1, add(Q_new, shift), y2, code, g_coeff, L_SUBFR, 7,
                    &gain_pit, &L_gain_code, clip_gain, st.qua_gain);
                Parm_serial(index, 7, prms, ref CurrentPrmsIndex);
            }

            /* test quantized gain of pitch for pitch clipping algorithm */
            Gp_clip_test_gain_pit(ser_size, gain_pit, st.gp_clip);

            L_tmp = L_shl(L_gain_code, Q_new); /* saturation can occur here */
            gain_code = round(L_tmp);          /* scaled gain_code with Qnew */

            /*----------------------------------------------------------*
             * Update parameters for the next subframe.                 *
             * - tilt of code: 0.0 (unvoiced) to 0.5 (voiced)           *
             *----------------------------------------------------------*/

            /* find voice factor in Q15 (1=voiced, -1=unvoiced) */

            Copy(&exc[i_subfr], exc2, L_SUBFR);
            Scale_sig(exc2, L_SUBFR, shift);

            voice_fac = voice_factor(exc2, shift, gain_pit, code, gain_code, L_SUBFR);

            /* tilt of code for next subframe: 0.5=voiced, 0=unvoiced */

            st.tilt_code = add(shr(voice_fac, 2), 8192);

            /*------------------------------------------------------*
             * - Update filter's memory "mem_w0" for finding the    *
             *   target vector in the next subframe.                *
             * - Find the total excitation                          *
             * - Find synthesis speech to update mem_syn[].         *
             *------------------------------------------------------*/

            /* y2 in Q9, gain_pit in Q14 */
            L_tmp = L_mult(gain_code, y2[L_SUBFR - 1]);
            L_tmp = L_shl(L_tmp, add(5, shift));
            L_tmp = L_negate(L_tmp);
            L_tmp = L_mac(L_tmp, xn[L_SUBFR - 1], 16384);
            L_tmp = L_msu(L_tmp, y1[L_SUBFR - 1], gain_pit);
            L_tmp = L_shl(L_tmp, sub(1, shift));
            st.mem_w0 = round(L_tmp);

            if (sub(ser_size, NBBITS_24k) >= 0)
                Copy(&exc[i_subfr], exc2, L_SUBFR);

            for (i = 0; i < L_SUBFR; i++)
            {
                /* code in Q9, gain_pit in Q14 */
                L_tmp = L_mult(gain_code, code[i]);
                L_tmp = L_shl(L_tmp, 5);
                L_tmp = L_mac(L_tmp, exc[i + i_subfr], gain_pit);
                L_tmp = L_shl(L_tmp, 1);       /* saturation can occur here */
                exc[i + i_subfr] = round(L_tmp);
            }

            Syn_filt(p_Aq, M, &exc[i_subfr], synth, L_SUBFR, st.mem_syn, 1);

            if (sub(ser_size, NBBITS_24k) >= 0)
            {
                /*------------------------------------------------------------*
                 * phase dispersion to enhance noise in low bit rate          *
                 *------------------------------------------------------------*/

                /* L_gain_code in Q16 */
                gain_code_lo = 0;
                L_Extract(L_gain_code, ref gain_code, ref gain_code_lo);

                /*------------------------------------------------------------*
                 * noise enhancer                                             *
                 * ~~~~~~~~~~~~~~                                             *
                 * - Enhance excitation on noise. (modify gain of code)       *
                 *   If signal is noisy and LPC filter is stable, move gain   *
                 *   of code 1.5 dB toward gain of code threshold.            *
                 *   This decrease by 3 dB noise energy variation.            *
                 *------------------------------------------------------------*/

                tmp = sub(16384, shr(voice_fac, 1));        /* 1=unvoiced, 0=voiced */
                fac = mult(stab_fac, tmp);

                L_tmp = L_gain_code;
                if (L_sub(L_tmp, st.L_gc_thres) < 0)
                {
                    L_tmp = L_add(L_tmp, Mpy_32_16(gain_code, gain_code_lo, 6226));
                    if (L_sub(L_tmp, st.L_gc_thres) > 0)
                    {
                        L_tmp = st.L_gc_thres;
                    }
                }
                else
                {
                    L_tmp = Mpy_32_16(gain_code, gain_code_lo, 27536);
                    if (L_sub(L_tmp, st.L_gc_thres) < 0)
                    {
                        L_tmp = st.L_gc_thres;
                    }
                }
                st.L_gc_thres = L_tmp;

                L_gain_code = Mpy_32_16(gain_code, gain_code_lo, sub(32767, fac));
                L_Extract(L_tmp, ref gain_code, ref gain_code_lo);
                L_gain_code = L_add(L_gain_code, Mpy_32_16(gain_code, gain_code_lo, fac));

                /*------------------------------------------------------------*
                 * pitch enhancer                                             *
                 * ~~~~~~~~~~~~~~                                             *
                 * - Enhance excitation on voice. (HP filtering of code)      *
                 *   On voiced signal, filtering of code by a smooth fir HP   *
                 *   filter to decrease energy of code in low frequency.      *
                 *------------------------------------------------------------*/

                tmp = add(shr(voice_fac, 3), 4096); /* 0.25=voiced, 0=unvoiced */

                L_tmp = L_deposit_h(code[0]);
                L_tmp = L_msu(L_tmp, code[1], tmp);
                code2[0] = round(L_tmp);

                for (i = 1; i < L_SUBFR - 1; i++)
                {
                    L_tmp = L_deposit_h(code[i]);
                    L_tmp = L_msu(L_tmp, code[i + 1], tmp);
                    L_tmp = L_msu(L_tmp, code[i - 1], tmp);
                    code2[i] = round(L_tmp);
                }

                L_tmp = L_deposit_h(code[L_SUBFR - 1]);
                L_tmp = L_msu(L_tmp, code[L_SUBFR - 2], tmp);
                code2[L_SUBFR - 1] = round(L_tmp);

                /* build excitation */

                gain_code = round(L_shl(L_gain_code, Q_new));

                for (i = 0; i < L_SUBFR; i++)
                {
                    L_tmp = L_mult(code2[i], gain_code);
                    L_tmp = L_shl(L_tmp, 5);
                    L_tmp = L_mac(L_tmp, exc2[i], gain_pit);
                    L_tmp = L_shl(L_tmp, 1);   /* saturation can occur here */
                    exc2[i] = round(L_tmp);
                }

                corr_gain = synthesis(p_Aq, exc2, Q_new, &speech16k[(i_subfr) * 5 / 4], st);
                Parm_serial(corr_gain, 4, prms, ref CurrentPrmsIndex);
            }
            p_A += (M + 1);
            p_Aq += (M + 1);
        }                                      /* end of subframe loop */

        /*--------------------------------------------------*
         * Update signal for next frame.                    *
         * -> save past of speech[], wsp[] and exc[].       *
         *--------------------------------------------------*/

        Copy(&old_speech[L_FRAME], st.old_speech, L_TOTAL - L_FRAME);
        Copy(&old_wsp[L_FRAME / OPL_DECIM], st.old_wsp, PIT_MAX / OPL_DECIM);
        Copy(&old_exc[L_FRAME], st.old_exc, PIT_MAX + L_INTERPOL);
    }

    /*-----------------------------------------------------*
     * Function synthesis()                                *
     *                                                     *
     * Synthesis of signal at 16kHz with HF extension.     *
     *                                                     *
     *-----------------------------------------------------*/

    private unsafe short synthesis(
         short* Aq,                          /* A(z)  : quantized Az               */
         short* exc,                         /* (i)   : excitation at 12kHz        */
         short Q_new,                         /* (i)   : scaling performed on exc   */
         short* synth16k,                    /* (o)   : 16kHz synthesis signal     */
         Coder_State st)                      /* (i/o) : State structure            */
    {
        short i, fac, tmp, exp;
        short ener, exp_ener;
        int L_tmp;

        short* synth_hi = stackalloc short[M + L_SUBFR];
        short* synth_lo = stackalloc short[M + L_SUBFR];
        short[] synth = new short[L_SUBFR];
        short* HF = stackalloc short[L_SUBFR16k];                 /* High Frequency vector      */
        short* Ap = stackalloc short[M + 1];

        short[] HF_SP = new short[L_SUBFR16k];              /* High Frequency vector (from original signal) */

        short HP_est_gain, HP_calc_gain, HP_corr_gain;
        short dist_min, dist;
        short HP_gain_ind = 0;
        short gain1, gain2;
        short weight1, weight2;

        /*------------------------------------------------------------*
         * speech synthesis                                           *
         * ~~~~~~~~~~~~~~~~                                           *
         * - Find synthesis speech corresponding to exc2[].           *
         * - Perform fixed deemphasis and hp 50hz filtering.          *
         * - Oversampling from 12.8kHz to 16kHz.                      *
         *------------------------------------------------------------*/


        Copy(st.mem_syn_hi, synth_hi, M);
        Copy(st.mem_syn_lo, synth_lo, M);

        Syn_filt_32(Aq, M, exc, Q_new, synth_hi + M, synth_lo + M, L_SUBFR);

        Copy(synth_hi + L_SUBFR, st.mem_syn_hi, M);
        Copy(synth_lo + L_SUBFR, st.mem_syn_lo, M);

        Deemph_32(synth_hi + M, synth_lo + M, synth, PREEMPH_FAC, L_SUBFR, ref st.mem_deemph);

        HP50_12k8(synth, L_SUBFR, st.mem_sig_out);

        /* Original speech signal as reference for high band gain quantisation */
        for (i = 0; i < L_SUBFR16k; i++)
        {
            HF_SP[i] = synth16k[i];
        }

        /*------------------------------------------------------*
         * HF noise synthesis                                   *
         * ~~~~~~~~~~~~~~~~~~                                   *
         * - Generate HF noise between 5.5 and 7.5 kHz.         *
         * - Set energy of noise according to synthesis tilt.   *
         *     tilt > 0.8 ==> - 14 dB (voiced)                  *
         *     tilt   0.5 ==> - 6 dB  (voiced or noise)         *
         *     tilt < 0.0 ==>   0 dB  (noise)                   *
         *------------------------------------------------------*/

        /* generate white noise vector */
        for (i = 0; i < L_SUBFR16k; i++)
        {
            HF[i] = shr(Random(ref st.seed2), 3); 
        }

        /* energy of excitation */

        Scale_sig(exc, L_SUBFR, -3);
        Q_new = sub(Q_new, 3);

        exp_ener = 0;
        ener = extract_h(Dot_product12(exc, exc, L_SUBFR, ref exp_ener));
        exp_ener = sub(exp_ener, add(Q_new, Q_new));

        /* set energy of white noise to energy of excitation */
        exp = 0;
        tmp = extract_h(Dot_product12(HF, HF, L_SUBFR16k, ref exp));

        if (sub(tmp, ener) > 0)
        {
            tmp = shr(tmp, 1);                 /* Be sure tmp < ener */
            exp = add(exp, 1);
        }
        L_tmp = L_deposit_h(div_s(tmp, ener)); /* result is normalized */
        exp = sub(exp, exp_ener);
        Isqrt_n(ref L_tmp, ref exp);
        L_tmp = L_shl(L_tmp, add(exp, 1));     /* L_tmp x 2, L_tmp in Q31 */
        tmp = extract_h(L_tmp);                /* tmp = 2 x sqrt(ener_exc/ener_hf) */

        for (i = 0; i < L_SUBFR16k; i++)
        {
            HF[i] = mult(HF[i], tmp);
        }

        /* find tilt of synthesis speech (tilt: 1=voiced, -1=unvoiced) */

        HP400_12k8(synth, L_SUBFR, st.mem_hp400);

        L_tmp = 1;
        for (i = 0; i < L_SUBFR; i++)
            L_tmp = L_mac(L_tmp, synth[i], synth[i]);

        exp = norm_l(L_tmp);
        ener = extract_h(L_shl(L_tmp, exp));   /* ener = r[0] */

        L_tmp = 1;
        for (i = 1; i < L_SUBFR; i++)
            L_tmp = L_mac(L_tmp, synth[i], synth[i - 1]);

        tmp = extract_h(L_shl(L_tmp, exp));    /* tmp = r[1] */

        if (tmp > 0)
        {
            fac = div_s(tmp, ener);
        }
        else
        {
            fac = 0;
        }


        /* modify energy of white noise according to synthesis tilt */
        gain1 = sub(32767, fac);
        gain2 = mult(sub(32767, fac), 20480);
        gain2 = shl(gain2, 1);

        if (st.vad_hist > 0)
        {
            weight1 = 0;
            weight2 = 32767;
        }
        else
        {
            weight1 = 32767;
            weight2 = 0;
        }
        tmp = mult(weight1, gain1);
        tmp = add(tmp, mult(weight2, gain2));

        if (tmp != 0)
        {
            tmp = add(tmp, 1);
        }
        HP_est_gain = tmp;

        if (sub(HP_est_gain, 3277) < 0)
        {
            HP_est_gain = 3277;                /* 0.1 in Q15 */
        }
        /* synthesis of noise: 4.8kHz..5.6kHz --> 6kHz..7kHz */
        Weight_a(Aq, Ap, 19661, M);            /* fac=0.6 */
        Syn_filt(Ap, M, HF, HF, L_SUBFR16k, st.mem_syn_hf, 1);

        /* noise High Pass filtering (1ms of delay) */
        Filt_6k_7k(HF, L_SUBFR16k, st.mem_hf);

        /* filtering of the original signal */
        Filt_6k_7k(HF_SP, L_SUBFR16k, st.mem_hf2);

        /* check the gain difference */
        Scale_sig(HF_SP, L_SUBFR16k, -1);

        ener = extract_h(Dot_product12(HF_SP, HF_SP, L_SUBFR16k, ref exp_ener));

        /* set energy of white noise to energy of excitation */
        tmp = extract_h(Dot_product12(HF, HF, L_SUBFR16k, ref exp));

        if (sub(tmp, ener) > 0)
        {
            tmp = shr(tmp, 1);                 /* Be sure tmp < ener */
            exp = add(exp, 1);
        }
        L_tmp = L_deposit_h(div_s(tmp, ener)); /* result is normalized */
        exp = sub(exp, exp_ener);
        Isqrt_n(ref L_tmp, ref exp);
        L_tmp = L_shl(L_tmp, exp);             /* L_tmp, L_tmp in Q31 */
        HP_calc_gain = extract_h(L_tmp);       /* tmp = sqrt(ener_input/ener_hf) */

        /* st->gain_alpha *= st->dtx_encSt->dtxHangoverCount/7 */
        L_tmp = L_shl(L_mult(st.dtx_encSt.dtxHangoverCount, 4681), 15);
        st.gain_alpha = mult(st.gain_alpha, extract_h(L_tmp));

        if (sub(st.dtx_encSt.dtxHangoverCount, 6) > 0)
            st.gain_alpha = 32767;
        HP_est_gain = shr(HP_est_gain, 1);     /* From Q15 to Q14 */
        HP_corr_gain = add(mult(HP_calc_gain, st.gain_alpha), mult(sub(32767, st.gain_alpha), HP_est_gain));

        /* Quantise the correction gain */
        dist_min = 32767;
        for (i = 0; i < 16; i++)
        {
            dist = mult(sub(HP_corr_gain, HP_gain[i]), sub(HP_corr_gain, HP_gain[i]));
            if (dist_min > dist)
            {
                dist_min = dist;
                HP_gain_ind = i;
            }
        }

        HP_corr_gain = HP_gain[HP_gain_ind];

        /* return the quantised gain index when using the highest mode, otherwise zero */
        return (HP_gain_ind);
    }
}


