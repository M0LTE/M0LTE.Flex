namespace M0LTE.Flex.Tools.IqGen;

/// <summary>In-place iterative radix-2 FFT, used to synthesise noise-like signals directly in the
/// frequency domain — fill the bins you want and transform back, which gives exact band edges with
/// no filter design or transition band to argue about.</summary>
internal static class Fft
{
    /// <param name="inverse">True for the inverse transform (no 1/N scaling; callers normalise).</param>
    public static void Transform(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        if ((n & (n - 1)) != 0)
        {
            throw new ArgumentException("FFT length must be a power of two");
        }

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = (inverse ? 2 : -2) * Math.PI / len;
            (double wSin, double wCos) = Math.SinCos(angle);
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1;
                double curIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k;
                    int b = a + (len / 2);
                    double tRe = (re[b] * curRe) - (im[b] * curIm);
                    double tIm = (re[b] * curIm) + (im[b] * curRe);
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    (curRe, curIm) = ((curRe * wCos) - (curIm * wSin), (curRe * wSin) + (curIm * wCos));
                }
            }
        }
    }
}
