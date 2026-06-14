namespace macViz;

internal static class SpectrumMath
{
    public static float[] AggregateBins(float[] inputBins, int outputBinCount, bool logarithmicGrouping = false)
    {
        if (outputBinCount <= 0)
        {
            outputBinCount = 1;
        }

        var sourceCount = inputBins.Length;
        var output = new float[outputBinCount];
        if (sourceCount == 0)
        {
            return output;
        }

        for (var outBin = 0; outBin < outputBinCount; outBin++)
        {
            int start;
            int end;

            if (!logarithmicGrouping)
            {
                start = (outBin * sourceCount) / outputBinCount;
                end = ((outBin + 1) * sourceCount) / outputBinCount;
            }
            else
            {
                const float logBase = 10f;
                var startT = (float)outBin / outputBinCount;
                var endT = (float)(outBin + 1) / outputBinCount;

                var mappedStart = (MathF.Pow(logBase, startT) - 1f) / (logBase - 1f);
                var mappedEnd = (MathF.Pow(logBase, endT) - 1f) / (logBase - 1f);

                start = (int)MathF.Floor(mappedStart * sourceCount);
                end = (int)MathF.Ceiling(mappedEnd * sourceCount);
            }

            start = Math.Clamp(start, 0, sourceCount - 1);
            end = Math.Clamp(end, start + 1, sourceCount);

            var sum = 0f;
            var count = 0;
            for (var i = start; i < end; i++)
            {
                sum += inputBins[i];
                count++;
            }

            output[outBin] = count > 0 ? sum / count : 0f;
        }

        return output;
    }

    public static float NormalizeSpectrumDb(float db)
    {
        const float minDb = -100f;
        const float maxDb = 0f;
        var normalized = (db - minDb) / (maxDb - minDb);
        return Math.Clamp(normalized, 0f, 1f);
    }

    public static void FillFallbackSpectrum(float time, float[] spectrum)
    {
        for (var i = 0; i < spectrum.Length; i++)
        {
            var t = time * 2.2f + (i * 0.04f);
            var wave = (MathF.Sin(t) * 0.5f + 0.5f) * MathF.Exp(-i / 140f);
            spectrum[i] = -95f + (wave * 85f);
        }
    }
}
