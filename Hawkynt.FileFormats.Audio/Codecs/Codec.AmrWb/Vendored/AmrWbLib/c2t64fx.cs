/*------------------------------------------------------------------------*
 *                         C2T64FX.C                                      *
 *------------------------------------------------------------------------*
 * Performs algebraic codebook search for 6.60 kbit/s mode                *
 *------------------------------------------------------------------------*/


/*-----------------------------------------------------------------------*
 * Function  ACELP_2t64_fx()                                             *
 * ~~~~~~~~~~~~~~~~~~~~~~~~~                                             *
 * 12 bits algebraic codebook.                                           *
 * 2 tracks x 32 positions per track = 64 samples.                       *
 *                                                                       *
 * 12 bits --> 2 pulses in a frame of 64 samples.                        *
 *                                                                       *
 * All pulses can have two (2) possible amplitudes: +1 or -1.            *
 * Each pulse can have 32 possible positions.                            *
 *-----------------------------------------------------------------------*/

//#include "typedef.h"
//#include "basic_op.h"
//#include "math_op.h"
//#include "acelp.h"
//#include "count.h"
//#include "cnst.h"

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private unsafe void ACELP_2t64_fx(
         short* dn,                          /* (i) <12b : correlation between target x[] and H[]      */
         short* cn,                          /* (i) <12b : residual after long term prediction         */
         short* H,                           /* (i) Q12: impulse response of weighted synthesis filter */
         short* code,                        /* (o) Q9 : algebraic (fixed) codebook excitation         */
         short* y,                           /* (o) Q9 : filtered fixed codebook excitation            */
         ref short index                      /* (o) : index (12): 5+1+5+1 = 11 bits.                   */
    )
    {
        int NB_TRACK = 2;
        short STEP = 2;
        short NB_POS = 32;
        int MSIZE = 1024;

        short i, j, k, i0, i1, ix, iy, pos, pos2;
        short ps, psk, ps1, ps2, alpk, alp1, alp2, sq;
        short alp, val, exp, k_cn, k_dn;

        short* p0, p1, p2, psign;
        short* h, h_inv, ptr_h1, ptr_h2, ptr_hf;

        //Word16 sign[L_SUBFR], vec[L_SUBFR], dn2[L_SUBFR];
        //Word16 h_buf[4 * L_SUBFR];
        //Word16 rrixix[NB_TRACK][NB_POS] ;
        //Word16 rrixiy[MSIZE];
        short* sign = stackalloc short[L_SUBFR];
        short* vec = stackalloc short[L_SUBFR];
        short* dn2 = stackalloc short[L_SUBFR];

        //short[] h_buf = new short[4 * L_SUBFR];
        short* h_buf = stackalloc short[4 * L_SUBFR];

        //short[,] rrixix = new short[NB_TRACK, NB_POS];
        short* rrixix = stackalloc short[NB_TRACK * NB_POS];

        //short[] rrixiy = new short[MSIZE];
        short* rrixiy = stackalloc short[MSIZE];

        int s, cor;

        /*----------------------------------------------------------------*
         * Find sign for each pulse position.                             *
         *----------------------------------------------------------------*/

        alp = 8192; /* alp = 2.0 (Q12) */

        /* calculate energy for normalization of cn[] and dn[] */

        /* set k_cn = 32..32767 (ener_cn = 2^30..256-0) */
        exp = 0;
        s = Dot_product12(cn, cn, L_SUBFR, ref exp);
        Isqrt_n(ref s, ref exp);
        s = L_shl(s, add(exp, 5));             /* saturation can occur here */
        k_cn = round(s);

        /* set k_dn = 32..512 (ener_dn = 2^30..2^22) */
        s = Dot_product12(dn, dn, L_SUBFR, ref exp);
        Isqrt_n(ref s, ref exp);
        k_dn = round(L_shl(s, add(exp, 5 + 3)));    /* k_dn = 256..4096 */
        k_dn = mult_r(alp, k_dn);              /* alp in Q12 */

        /* mix normalized cn[] and dn[] */
        for (i = 0; i < L_SUBFR; i++)
        {
            s = L_mac(L_mult(k_cn, cn[i]), k_dn, dn[i]);
            dn2[i] = extract_h(L_shl(s, 8));
        }

        /* set sign according to dn2[] = k_cn*cn[] + k_dn*dn[]    */

        for (k = 0; k < NB_TRACK; k++)
        {
            for (i = k; i < L_SUBFR; i += STEP)
            {
                val = dn[i];
                ps = dn2[i];

                if (ps >= 0)
                {
                    sign[i] = 32767; /* sign = +1 (Q12) */
                    vec[i] = -32768;
                } else
                {
                    sign[i] = -32768;  /* sign = -1 (Q12) */
                    vec[i] = 32767;
                    val = negate(val);
                }
                dn[i] = val;  /* modify dn[] according to the fixed sign */
            }
        }

        /*------------------------------------------------------------*
         * Compute h_inv[i].                                          *
         *------------------------------------------------------------*/

        /* impulse response buffer for fast computation */

        h = h_buf;
        h_inv = h_buf + (2 * L_SUBFR);

        for (i = 0; i < L_SUBFR; i++)
        {
            *h++ = 0;
            *h_inv++ = 0;
        }

        for (i = 0; i < L_SUBFR; i++)
        {
            h[i] = H[i];
            h_inv[i] = negate(h[i]);
        }

        /*------------------------------------------------------------*
         * Compute rrixix[][] needed for the codebook search.         *
         * Result is multiplied by 0.5                                *
         *------------------------------------------------------------*/

        /* Init pointers to last position of rrixix[] */
        //p0 = &rrixix[0][NB_POS - 1];
        //p1 = &rrixix[1][NB_POS - 1];
        p0 = &rrixix[NB_POS - 1];
        p1 = &rrixix[1 * NB_POS + NB_POS - 1];

        ptr_h1 = h;
        cor = 0x00010000;  /* for rounding */
        for (i = 0; i < NB_POS; i++)
        {
            cor = L_mac(cor, *ptr_h1, *ptr_h1);
            ptr_h1++;
            *p1-- = extract_h(cor);
            cor = L_mac(cor, *ptr_h1, *ptr_h1);
            ptr_h1++;
            *p0-- = extract_h(cor);
        }

        //p0 = rrixix[0];
        //p1 = rrixix[1];
        p0 = &rrixix[0];
        p1 = &rrixix[NB_POS];
        for (i = 0; i < NB_POS; i++)
        {
            *p0 = shr(*p0, 1);
            p0++;
            *p1 = shr(*p1, 1);
            p1++;
        }

        /*------------------------------------------------------------*
         * Compute rrixiy[][] needed for the codebook search.         *
         *------------------------------------------------------------*/

        pos = (short) (MSIZE - 1);
        pos2 = (short) (MSIZE - 2);
        ptr_hf = h + 1;

        for (k = 0; k < NB_POS; k++)
        {
            p1 = &rrixiy[pos];
            p0 = &rrixiy[pos2];

            cor = 0x00008000;  /* for rounding */
            ptr_h1 = h;
            ptr_h2 = ptr_hf;

            for (i = (short)(k + 1); i < NB_POS; i++)
            {
                cor = L_mac(cor, *ptr_h1, *ptr_h2);
                ptr_h1++;
                ptr_h2++;
                *p1 = extract_h(cor);
                cor = L_mac(cor, *ptr_h1, *ptr_h2);
                ptr_h1++;
                ptr_h2++;
                *p0 = extract_h(cor);

                p1 -= (NB_POS + 1);
                p0 -= (NB_POS + 1);
            }
            cor = L_mac(cor, *ptr_h1, *ptr_h2);
            ptr_h1++;
            ptr_h2++;
            *p1 = extract_h(cor);

            pos -= NB_POS;
            pos2--;
            ptr_hf += STEP;
        }

        /*------------------------------------------------------------*
         * Modification of rrixiy[][] to take signs into account.     *
         *------------------------------------------------------------*/

        p0 = rrixiy;

        for (i = 0; i < L_SUBFR; i += STEP)
        {
            psign = sign;
            if (psign[i] < 0)
            {
                psign = vec;
            }
            for (j = 1; j < L_SUBFR; j += STEP)
            {
                *p0 = mult(*p0, psign[j]);
                p0++;
            }
        }

        /*-------------------------------------------------------------------*
         * search 2 pulses:                                                  *
         * ~@~~~~~~~~~~~~~~                                                  *
         * 32 pos x 32 pos = 1024 tests (all combinaisons is tested)         *
         *-------------------------------------------------------------------*/

        //p0 = rrixix[0];
        //p1 = rrixix[1];
        p0 = &rrixix[0];
        //p1 = &rrixix[1];
        p1 = &rrixix[NB_POS];   // 11 May 25 PHR -- Point to the 2nd row of rrixix
        p2 = rrixiy;

        psk = -1;
        alpk = 1;
        ix = 0;
        iy = 1;

        for (i0 = 0; i0 < L_SUBFR; i0 += STEP)
        {
            ps1 = dn[i0];
            alp1 = (*p0++);

            pos = -1;
            for (i1 = 1; i1 < L_SUBFR; i1 += STEP)
            {
                ps2 = add(ps1, dn[i1]);
                alp2 = add(alp1, add(*p1++, *p2++));

                sq = mult(ps2, ps2);

                s = L_msu(L_mult(alpk, sq), psk, alp2);

                if (s > 0)
                {
                    psk = sq;
                    alpk = alp2;
                    pos = i1;
                }
            }
            p1 -= NB_POS;

            if (pos >= 0)
            {
                ix = i0;
                iy = pos;
            }
        }

        /*-------------------------------------------------------------------*
         * Build the codeword, the filtered codeword and index of codevector.*
         *-------------------------------------------------------------------*/

        for (i = 0; i < L_SUBFR; i++)
        {
            code[i] = 0;
        }

        i0 = shr(ix, 1);                       /* pos of pulse 1 (0..31) */
        i1 = shr(iy, 1);                       /* pos of pulse 2 (0..31) */
        if (sign[ix] > 0)
        {
            code[ix] = 512;  /* codeword in Q9 format */
            p0 = h - ix;
        } else
        {
            code[ix] = -512;
            i0 += NB_POS;
            p0 = h_inv - ix;
        }
        
        if (sign[iy] > 0)
        {
            code[iy] = 512;
            p1 = h - iy;
        } else
        {
            code[iy] = -512;
            i1 += NB_POS;
            p1 = h_inv - iy;
        }

        index = add(shl(i0, 6), i1);

        for (i = 0; i < L_SUBFR; i++)
        {
            y[i] = shr_r(add(*p0++, *p1++), 3);
        }

        return;
    }
}