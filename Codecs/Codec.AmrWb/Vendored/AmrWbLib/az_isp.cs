
/*-----------------------------------------------------------------------*
 *                         Az_isp.C                                      *
 *-----------------------------------------------------------------------*
 * Compute the ISPs from  the LPC coefficients  (order=M)                *
 *-----------------------------------------------------------------------*
 *                                                                       *
 * The ISPs are the roots of the two polynomials F1(z) and F2(z)         *
 * defined as                                                            *
 *               F1(z) = A(z) + z^-m A(z^-1)                             *
 *  and          F2(z) = A(z) - z^-m A(z^-1)                             *
 *                                                                       *
 * For a even order m=2n, F1(z) has M/2 conjugate roots on the unit      *
 * circle and F2(z) has M/2-1 conjugate roots on the unit circle in      *
 * addition to two roots at 0 and pi.                                    *
 *                                                                       *
 * For a 16th order LP analysis, F1(z) and F2(z) can be written as       *
 *                                                                       *
 *   F1(z) = (1 + a[M])   PRODUCT  (1 - 2 cos(w_i) z^-1 + z^-2 )         *
 *                        i=0,2,4,6,8,10,12,14                           *
 *                                                                       *
 *   F2(z) = (1 - a[M]) (1 - z^-2) PRODUCT (1 - 2 cos(w_i) z^-1 + z^-2 ) *
 *                                 i=1,3,5,7,9,11,13                     *
 *                                                                       *
 * The ISPs are the M-1 frequencies w_i, i=0...M-2 plus the last         *
 * predictor coefficient a[M].                                           *
 *-----------------------------------------------------------------------*/

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private const int M = 16;
    private const int NC = (M / 2);

    private unsafe void Az_isp(
         short* a,                           /* (i) Q12 : predictor coefficients                 */
         short[] isp,                         /* (o) Q15 : Immittance spectral pairs              */
         short[] old_isp                      /* (i)     : old isp[] (in case not found M roots)  */
)
    {
        short i, j, nf, ip, order;
        short xlow, ylow, xhigh, yhigh, xmid, ymid, xint;
        short x, y, sign, exp;
        short[] coef;
        //short f1[NC + 1], f2[NC];
        short[] f1 = new short[NC + 1];
        short[] f2 = new short[NC];
        int t0;

        /*-------------------------------------------------------------*
         * find the sum and diff polynomials F1(z) and F2(z)           *
         *      F1(z) = [A(z) + z^M A(z^-1)]                           *
         *      F2(z) = [A(z) - z^M A(z^-1)]/(1-z^-2)                  *
         *                                                             *
         * for (i=0; i<NC; i++)                                        *
         * {                                                           *
         *   f1[i] = a[i] + a[M-i];                                    *
         *   f2[i] = a[i] - a[M-i];                                    *
         * }                                                           *
         * f1[NC] = 2.0*a[NC];                                         *
         *                                                             *
         * for (i=2; i<NC; i++)            Divide by (1-z^-2)          *
         *   f2[i] += f2[i-2];                                         *
         *-------------------------------------------------------------*/

        for (i = 0; i < NC; i++)
        {
            t0 = L_mult(a[i], 16384);
            f1[i] = round(L_mac(t0, a[M - i], 16384)); /* =(a[i]+a[M-i])/2 */
            f2[i] = round(L_msu(t0, a[M - i], 16384)); /* =(a[i]-a[M-i])/2 */
        }
        f1[NC] = a[NC];

        for (i = 2; i < NC; i++)               /* Divide by (1-z^-2) */
            f2[i] = add(f2[i], f2[i - 2]);

        /*---------------------------------------------------------------------*
         * Find the ISPs (roots of F1(z) and F2(z) ) using the                 *
         * Chebyshev polynomial evaluation.                                    *
         * The roots of F1(z) and F2(z) are alternatively searched.            *
         * We start by finding the first root of F1(z) then we switch          *
         * to F2(z) then back to F1(z) and so on until all roots are found.    *
         *                                                                     *
         *  - Evaluate Chebyshev pol. at grid points and check for sign change.*
         *  - If sign change track the root by subdividing the interval        *
         *    2 times and ckecking sign change.                                *
         *---------------------------------------------------------------------*/

        nf = 0;   /* number of found frequencies */
        ip = 0;   /* indicator for f1 or f2      */

        coef = f1;
        order = NC;

        xlow = grid[0];
        ylow = Chebps2(xlow, coef, order);

        j = 0;
        while ((nf < M - 1) && (j < GRID_POINTS))
        {
            j = add(j, 1);
            xhigh = xlow;
            yhigh = ylow;
            xlow = grid[j];
            ylow = Chebps2(xlow, coef, order);

            if (L_mult(ylow, yhigh) <= (int)0)
            {
                /* divide 2 times the interval */

                for (i = 0; i < 2; i++)
                {
                    xmid = add(shr(xlow, 1), shr(xhigh, 1));        /* xmid = (xlow + xhigh)/2 */

                    ymid = Chebps2(xmid, coef, order);

                    if (L_mult(ylow, ymid) <= (int)0)
                    {
                        yhigh = ymid;
                        xhigh = xmid;
                    }
                    else
                    {
                        ylow = ymid;
                        xlow = xmid;
                    }
                }

                /*-------------------------------------------------------------*
                 * Linear interpolation                                        *
                 *    xint = xlow - ylow*(xhigh-xlow)/(yhigh-ylow);            *
                 *-------------------------------------------------------------*/

                x = sub(xhigh, xlow);
                y = sub(yhigh, ylow);

                if (y == 0)
                {
                    xint = xlow;
                }
                else
                {
                    sign = y;
                    y = abs_s(y);
                    exp = norm_s(y);
                    y = shl(y, exp);
                    y = div_s((short)16383, y);
                    t0 = L_mult(x, y);
                    t0 = L_shr(t0, sub(20, exp));
                    y = extract_l(t0);         /* y= (xhigh-xlow)/(yhigh-ylow) in Q11 */

                    if (sign < 0)
                        y = negate(y);

                    t0 = L_mult(ylow, y);      /* result in Q26 */
                    t0 = L_shr(t0, 11);        /* result in Q15 */
                    xint = sub(xlow, extract_l(t0));        /* xint = xlow - ylow*y */
                }

                isp[nf] = xint;
                xlow = xint;
                nf++;

                if (ip == 0)
                {
                    ip = 1;
                    coef = f2;
                    order = NC - 1;
                }
                else
                {
                    ip = 0;
                    coef = f1;
                    order = NC;
                }
                ylow = Chebps2(xlow, coef, order);
            }
        }

        /* Check if M-1 roots found */

        if (sub(nf, M - 1) < 0)
        {
            for (i = 0; i < M; i++)
            {
                isp[i] = old_isp[i];
            }
        }
        else
        {
            isp[M - 1] = shl(a[M], 3);  /* From Q12 to Q15 with saturation */
        }

        return;
    }


    /*--------------------------------------------------------------*
     * function  Chebps2:                                           *
     *           ~~~~~~~                                            *
     *    Evaluates the Chebishev polynomial series                 *
     *--------------------------------------------------------------*
     *                                                              *
     *  The polynomial order is                                     *
     *     n = M/2   (M is the prediction order)                    *
     *  The polynomial is given by                                  *
     *    C(x) = f(0)T_n(x) + f(1)T_n-1(x) + ... +f(n-1)T_1(x) + f(n)/2 *
     * Arguments:                                                   *
     *  x:     input value of evaluation; x = cos(frequency) in Q15 *
     *  f[]:   coefficients of the pol.                      in Q11 *
     *  n:     order of the pol.                                    *
     *                                                              *
     * The value of C(x) is returned. (Satured to +-1.99 in Q14)    *
     *                                                              *
     *--------------------------------------------------------------*/

    private short Chebps2(short x, short[] f, short n)
    {
        short i, cheb;
        short b0_h, b0_l, b1_h, b1_l, b2_h, b2_l;
        int t0;

        /* Note: All computation are done in Q24. */
        b2_h = 0; b2_l = 0;
        b1_h = 0; b1_l = 0;
        b0_h = 0; b0_l = 0;

        t0 = L_mult(f[0], 4096);
        L_Extract(t0, ref b2_h, ref b2_l);     /* b2 = f[0] in Q24 DPF */

        t0 = Mpy_32_16(b2_h, b2_l, x);         /* t0 = 2.0*x*b2        */
        t0 = L_shl(t0, 1);
        t0 = L_mac(t0, f[1], 4096);            /* + f[1] in Q24        */
        L_Extract(t0, ref b1_h, ref b1_l);     /* b1 = 2*x*b2 + f[1]   */

        for (i = 2; i < n; i++)
        {
            t0 = Mpy_32_16(b1_h, b1_l, x);     /* t0 = 2.0*x*b1              */

            t0 = L_mac(t0, b2_h, -16384);
            t0 = L_mac(t0, f[i], 2048);
            t0 = L_shl(t0, 1);
            t0 = L_msu(t0, b2_l, 1);           /* t0 = 2.0*x*b1 - b2 + f[i]; */

            L_Extract(t0, ref b0_h, ref b0_l);       /* b0 = 2.0*x*b1 - b2 + f[i]; */

            b2_l = b1_l;  /* b2 = b1; */
            b2_h = b1_h;
            b1_l = b0_l;  /* b1 = b0; */
            b1_h = b0_h;
        }

        t0 = Mpy_32_16(b1_h, b1_l, x);         /* t0 = x*b1;              */
        t0 = L_mac(t0, b2_h, (short) - 32768);/* t0 = x*b1 - b2          */
        t0 = L_msu(t0, b2_l, 1);
        t0 = L_mac(t0, f[n], 2048);            /* t0 = x*b1 - b2 + f[i]/2 */

        t0 = L_shl(t0, 6);                     /* Q24 to Q30 with saturation */

        cheb = extract_h(t0);                  /* Result in Q14              */

        if (sub(cheb, -32768) == 0)
        {
            cheb = -32767;                     /* to avoid saturation in Az_isp */
        }
        return (cheb);
    }

}
