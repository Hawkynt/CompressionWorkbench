/*---------------------------------------------------------------------------*
 *                         LEVINSON.C                     *
 *---------------------------------------------------------------------------*
 *                                                                           *
 *      LEVINSON-DURBIN algorithm in double precision                        *
 *      ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~                        *
 *                                                                           *
 * Algorithm                                                                 *
 *                                                                           *
 *       R[i]    autocorrelations.                                           *
 *       A[i]    filter coefficients.                                        *
 *       K       reflection coefficients.                                    *
 *       Alpha   prediction gain.                                            *
 *                                                                           *
 *       Initialization:                                                     *
 *               A[0] = 1                                                    *
 *               K    = -R[1]/R[0]                                           *
 *               A[1] = K                                                    *
 *               Alpha = R[0] * (1-K**2]                                     *
 *                                                                           *
 *       Do for  i = 2 to M                                                  *
 *                                                                           *
 *            S =  SUM ( R[j]*A[i-j] ,j=1,i-1 ) +  R[i]                      *
 *                                                                           *
 *            K = -S / Alpha                                                 *
 *                                                                           *
 *            An[j] = A[j] + K*A[i-j]   for j=1 to i-1                       *
 *                                      where   An[i] = new A[i]             *
 *            An[i]=K                                                        *
 *                                                                           *
 *            Alpha=Alpha * (1-K**2)                                         *
 *                                                                           *
 *       END                                                                 *
 *                                                                           *
 * Remarks on the dynamics of the calculations.                              *
 *                                                                           *
 *       The numbers used are in double precision in the following format :  *
 *       A = AH <<16 + AL<<1.  AH and AL are 16 bit signed integers.         *
 *       Since the LSB's also contain a sign bit, this format does not       *
 *       correspond to standard 32 bit integers.  We use this format since   *
 *       it allows fast execution of multiplications and divisions.          *
 *                                                                           *
 *       "DPF" will refer to this special format in the following text.      *
 *       See oper_32b.c                                                      *
 *                                                                           *
 *       The R[i] were normalized in routine AUTO (hence, R[i] < 1.0).       *
 *       The K[i] and Alpha are theoretically < 1.0.                         *
 *       The A[i], for a sampling frequency of 8 kHz, are in practice        *
 *       always inferior to 16.0.                                            *
 *                                                                           *
 *       These characteristics allow straigthforward fixed-point             *
 *       implementation.  We choose to represent the parameters as           *
 *       follows :                                                           *
 *                                                                           *
 *               R[i]    Q31   +- .99..                                      *
 *               K[i]    Q31   +- .99..                                      *
 *               Alpha   Normalized -> mantissa in Q31 plus exponent         *
 *               A[i]    Q27   +- 15.999..                                   *
 *                                                                           *
 *       The additions are performed in 32 bit.  For the summation used      *
 *       to calculate the K[i], we multiply numbers in Q31 by numbers        *
 *       in Q27, with the result of the multiplications in Q27,              *
 *       resulting in a dynamic of +- 16.  This is sufficient to avoid       *
 *       overflow, since the final result of the summation is                *
 *       necessarily < 1.0 as both the K[i] and Alpha are                    *
 *       theoretically < 1.0.                                                *
 *___________________________________________________________________________*/

//#include "typedef.h"
//#include "basic_op.h"
//#include "oper_32b.h"
//#include "acelp.h"
//#include "count.h"

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    //private const int NC = (M / 2);

    private void Init_Levinson(
         short[] mem)                          /* output  :static memory (18 words) */
    {
        Set_zero(mem, 18);                     /* old_A[0..M-1] = 0, old_rc[0..1] = 0 */
    }

    private unsafe void Levinson(
         short[] Rh,                          /* (i)     : Rh[M+1] Vector of autocorrelations (msb) */
         short[] Rl,                          /* (i)     : Rl[M+1] Vector of autocorrelations (lsb) */
         short* A,                           /* (o) Q12 : A[M]    LPC coefficients  (m = 16)       */
         short[] rc,                          /* (o) Q15 : rc[M]   Reflection coefficients.         */
         short[] mem                          /* (i/o)   :static memory (18 words)                  */
    )
    {
        short i, j;
        short hi, lo;
        short Kh, Kl;                         /* reflection coefficient; hi and lo           */
        short alp_h, alp_l, alp_exp;          /* Prediction gain; hi lo and exponent         */
        short[] Ah = new short[M + 1];
        short[] Al = new short[M + 1];           /* LPC coef. in double prec.                   */
        short[] Anh = new short[M + 1];
        short[] Anl = new short[M + 1];         /* LPC coef.for next iteration in double prec. */
        int t0, t1, t2;                     /* temporary variable                          */

        //short* old_A;
        //short* *old_rc;
        //* Last A(z) for case of unstable filter */
        //old_A = mem;
        //old_rc = mem + M;

        // 29 Apr 25 PHR
        short[] old_A;
        Span<short> old_rc = new Span<short>(mem, M, 2);
        /* Last A(z) for case of unstable filter */
        old_A = mem;

        /* K = A[1] = -R[1] / R[0] */

        t1 = L_Comp(Rh[1], Rl[1]);             /* R[1] in Q31      */
        t2 = L_abs(t1);                        /* abs R[1]         */
        t0 = Div_32(t2, Rh[0], Rl[0]);         /* R[1]/R[0] in Q31 */
        if (t1 > 0)
            t0 = L_negate(t0);                 /* -R[1]/R[0]       */

        Kl = 0;
        Kh = 0;
        L_Extract(t0, ref Kh, ref Kl);               /* K in DPF         */
        rc[0] = Kh;
        t0 = L_shr(t0, 4);                     /* A[1] in Q27      */
        L_Extract(t0, ref Ah[1], ref Al[1]);         /* A[1] in DPF      */

        /* Alpha = R[0] * (1-K**2) */

        t0 = Mpy_32(Kh, Kl, Kh, Kl);           /* K*K      in Q31 */
        t0 = L_abs(t0);                        /* Some case <0 !! */
        t0 = L_sub(0x7fffffff, t0);  /* 1 - K*K  in Q31 */
        hi = 0;
        lo = 0;
        L_Extract(t0, ref hi, ref lo);               /* DPF format      */
        t0 = Mpy_32(Rh[0], Rl[0], hi, lo);     /* Alpha in Q31    */

        /* Normalize Alpha */

        alp_exp = norm_l(t0);
        t0 = L_shl(t0, alp_exp);
        alp_h = 0;
        alp_l = 0;
        L_Extract(t0, ref alp_h, ref alp_l);
        /* DPF format    */

        /*--------------------------------------*
         * ITERATIONS  I=2 to M                 *
         *--------------------------------------*/

        for (i = 2; i <= M; i++)
        {

            /* t0 = SUM ( R[j]*A[i-j] ,j=1,i-1 ) +  R[i] */

            t0 = 0;
            for (j = 1; j < i; j++)
                t0 = L_add(t0, Mpy_32(Rh[j], Rl[j], Ah[i - j], Al[i - j]));

            t0 = L_shl(t0, 4);                 /* result in Q27 -> convert to Q31 */
            /* No overflow possible            */
            t1 = L_Comp(Rh[i], Rl[i]);
            t0 = L_add(t0, t1);                /* add R[i] in Q31                 */

            /* K = -t0 / Alpha */

            t1 = L_abs(t0);
            t2 = Div_32(t1, alp_h, alp_l);     /* abs(t0)/Alpha                   */
            
            if (t0 > 0)
                t2 = L_negate(t2);             /* K =-t0/Alpha                    */
            t2 = L_shl(t2, alp_exp);           /* denormalize; compare to Alpha   */
            L_Extract(t2, ref Kh, ref Kl);           /* K in DPF                        */
            rc[i - 1] = Kh;

            /* Test for unstable filter. If unstable keep old A(z) */

            if (sub(abs_s(Kh), 32750) > 0)
            {
                A[0] = 4096;  /* Ai[0] not stored (always 1.0) */
                for (j = 0; j < M; j++)
                {
                    A[j + 1] = old_A[j];
                }
                rc[0] = old_rc[0];             /* only two rc coefficients are needed */
                rc[1] = old_rc[1];
                return;
            }
            /*------------------------------------------*
             *  Compute new LPC coeff. -> An[i]         *
             *  An[j]= A[j] + K*A[i-j]     , j=1 to i-1 *
             *  An[i]= K                                *
             *------------------------------------------*/

            for (j = 1; j < i; j++)
            {
                t0 = Mpy_32(Kh, Kl, Ah[i - j], Al[i - j]);
                t0 = L_add(t0, L_Comp(Ah[j], Al[j]));
                L_Extract(t0, ref Anh[j], ref Anl[j]);
            }
            t2 = L_shr(t2, 4);                 /* t2 = K in Q31 ->convert to Q27  */
            L_Extract(t2, ref Anh[i], ref Anl[i]);   /* An[i] in Q27                    */

            /* Alpha = Alpha * (1-K**2) */

            t0 = Mpy_32(Kh, Kl, Kh, Kl);       /* K*K      in Q31 */
            t0 = L_abs(t0);                    /* Some case <0 !! */
            t0 = L_sub(0x7fffffff, t0);   /* 1 - K*K  in Q31 */
            L_Extract(t0, ref hi, ref lo);           /* DPF format      */
            t0 = Mpy_32(alp_h, alp_l, hi, lo); /* Alpha in Q31    */

            /* Normalize Alpha */

            j = norm_l(t0);
            t0 = L_shl(t0, j);
            L_Extract(t0, ref alp_h, ref alp_l);     /* DPF format    */
            alp_exp = add(alp_exp, j);         /* Add normalization to alp_exp */

            /* A[j] = An[j] */

            for (j = 1; j <= i; j++)
            {
                Ah[j] = Anh[j];
                Al[j] = Anl[j];
            }
        }

        /* Truncate A[i] in Q27 to Q12 with rounding */

        A[0] = 4096;
        for (i = 1; i <= M; i++)
        {
            t0 = L_Comp(Ah[i], Al[i]);
            old_A[i - 1] = A[i] = round(L_shl(t0, 1));
        }
        old_rc[0] = rc[0];
        old_rc[1] = rc[1];
    }


}
