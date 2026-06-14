namespace macViz;

public partial class MinimalGameWindow
{
    private void RemoveLfoAndAssignments(int lfoId)
    {
        _lfoEngine.RemoveLfo(lfoId);

        var keysToRemove = _modulationMatrix.Keys.Where(k => k.LfoId == lfoId).ToArray();
        foreach (var key in keysToRemove)
        {
            _modulationMatrix.Remove(key);
        }
    }

    private void ApplyLfoToParameters()
    {
        foreach (var visual in _visuals)
        {
            for (var paramIndex = 0; paramIndex < visual.Parameters.Count; paramIndex++)
            {
                var parameter = visual.Parameters[paramIndex];

                var lfoSum = 0f;
                var hasLfo = false;
                foreach (var lfo in _lfoEngine.Lfos)
                {
                    var key = (parameter, lfo.Id);
                    if (!_modulationMatrix.TryGetValue(key, out var modulation) ||
                        !_lfoEngine.TryGetOutput(lfo.Id, out var lfoValue))
                    {
                        continue;
                    }

                    lfoSum += (lfoValue * modulation.Scale) + modulation.Offset;
                    hasLfo = true;
                }

                var fftSum = 0f;
                var hasFft = false;
                foreach (var fft in _fftSources)
                {
                    var audioKey = (parameter, fft.Id);
                    if (!_audioModulationMatrix.TryGetValue(audioKey, out var audioMod))
                    {
                        continue;
                    }

                    var audioValue = (GetAudioBinValue(fft.Id, audioMod.AudioBinIndex) * audioMod.Scale) + audioMod.Offset;
                    fftSum += audioValue;
                    hasFft = true;
                }

                var modulationFactor = 1f;
                if (hasLfo && hasFft)
                {
                    var mode = _lfoFftInteractionModes.GetValueOrDefault(parameter, ModulationInteractionMode.Add);
                    modulationFactor = mode switch
                    {
                        ModulationInteractionMode.Instead => fftSum,
                        ModulationInteractionMode.Add => lfoSum + fftSum,
                        ModulationInteractionMode.Subtract => lfoSum - fftSum,
                        ModulationInteractionMode.Multiply => lfoSum * fftSum,
                        _ => lfoSum + fftSum
                    };
                }
                else if (hasLfo)
                {
                    modulationFactor = lfoSum;
                }
                else if (hasFft)
                {
                    modulationFactor = fftSum;
                }

                parameter.ApplyCombinedModulation(modulationFactor);
            }
        }
    }

    private void PruneOrphanedParameterAssignments()
    {
        var liveParameters = new HashSet<IParameter>();
        foreach (var visual in _visuals)
        {
            foreach (var parameter in visual.Parameters)
            {
                liveParameters.Add(parameter);
            }
        }

        var lfoKeysToRemove = _modulationMatrix.Keys.Where(k => !liveParameters.Contains(k.Parameter)).ToArray();
        foreach (var key in lfoKeysToRemove)
        {
            _modulationMatrix.Remove(key);
        }

        var fftKeysToRemove = _audioModulationMatrix.Keys.Where(k => !liveParameters.Contains(k.Parameter)).ToArray();
        foreach (var key in fftKeysToRemove)
        {
            _audioModulationMatrix.Remove(key);
        }

        var modeKeysToRemove = _lfoFftInteractionModes.Keys.Where(k => !liveParameters.Contains(k)).ToArray();
        foreach (var key in modeKeysToRemove)
        {
            _lfoFftInteractionModes.Remove(key);
        }
    }

    private void UpdateAudioModulationBins()
    {
        if (_fftSources.Count == 0)
        {
            return;
        }

        var srcBins = _latestSpectrum.Length;

        var maxRequestedBins = 1;
        foreach (var source in _fftSources)
        {
            if (source.BinCount <= 0)
            {
                source.BinCount = 1;
            }

            if (source.BinCount > maxRequestedBins)
            {
                maxRequestedBins = source.BinCount;
            }
        }

        var normalizedSpectrum = new float[srcBins];
        for (var i = 0; i < srcBins; i++)
        {
            normalizedSpectrum[i] = SpectrumMath.NormalizeSpectrumDb(_latestSpectrum[i]);
        }

        var maxPrecisionBins = SpectrumMath.AggregateBins(normalizedSpectrum, maxRequestedBins);

        foreach (var source in _fftSources)
        {
            if (source.SmoothedBins.Length != source.BinCount)
            {
                Array.Resize(ref source.SmoothedBins, source.BinCount);
                source.VariabilityHistory.Clear();
            }

            var rawBins = source.BinCount == maxRequestedBins && !source.LogarithmicGrouping
                ? (float[])maxPrecisionBins.Clone()
                : SpectrumMath.AggregateBins(maxPrecisionBins, source.BinCount, source.LogarithmicGrouping);

            var binsForSmoothing = rawBins;
            if (source.ExpandVariability)
            {
                var windowSeconds = Math.Clamp(source.VariabilityWindowSeconds, 0.2f, 10f);
                source.VariabilityWindowSeconds = windowSeconds;

                source.VariabilityHistory.Enqueue((_elapsedTime, (float[])rawBins.Clone()));
                var cutoff = _elapsedTime - windowSeconds;
                while (source.VariabilityHistory.Count > 0 && source.VariabilityHistory.Peek().Time < cutoff)
                {
                    source.VariabilityHistory.Dequeue();
                }

                var expandedBins = new float[source.BinCount];
                for (var outBin = 0; outBin < source.BinCount; outBin++)
                {
                    var min = float.MaxValue;
                    var max = float.MinValue;

                    foreach (var sample in source.VariabilityHistory)
                    {
                        var value = sample.Bins[outBin];
                        if (value < min) min = value;
                        if (value > max) max = value;
                    }

                    if (min == float.MaxValue || max == float.MinValue)
                    {
                        expandedBins[outBin] = rawBins[outBin];
                        continue;
                    }

                    var range = max - min;
                    expandedBins[outBin] = range > 1e-5f
                        ? Math.Clamp((rawBins[outBin] - min) / range, 0f, 1f)
                        : 0f;
                }

                binsForSmoothing = expandedBins;
            }
            else if (source.VariabilityHistory.Count > 0)
            {
                source.VariabilityHistory.Clear();
            }

            var smoothing = Math.Clamp(source.Smoothing, 0f, 0.99f);
            for (var outBin = 0; outBin < source.BinCount; outBin++)
            {
                var target = binsForSmoothing[outBin];
                source.SmoothedBins[outBin] = (source.SmoothedBins[outBin] * smoothing) + (target * (1f - smoothing));
            }
        }
    }

    private float GetAudioBinValue(int fftId, int binIndex)
    {
        var source = _fftSources.FirstOrDefault(x => x.Id == fftId);
        if (source is null || source.SmoothedBins.Length == 0)
        {
            return 0f;
        }

        var clamped = Math.Clamp(binIndex, 0, source.SmoothedBins.Length - 1);
        return source.SmoothedBins[clamped];
    }

    private void AddFftSource()
    {
        var id = _nextFftSourceId++;
        _fftSources.Add(new FftModSource
        {
            Id = id,
            BinCount = 8,
            Smoothing = 0.75f,
            LogarithmicGrouping = false,
            ExpandVariability = false,
            VariabilityWindowSeconds = 2f,
            SmoothedBins = new float[8]
        });
    }

    private void RemoveFftSourceAndAssignments(int fftId)
    {
        _fftSources.RemoveAll(x => x.Id == fftId);

        var keysToRemove = _audioModulationMatrix.Keys.Where(k => k.FftId == fftId).ToArray();
        foreach (var key in keysToRemove)
        {
            _audioModulationMatrix.Remove(key);
        }
    }

    private void SanitizeAudioBinAssignments()
    {
        var sourceById = _fftSources.ToDictionary(x => x.Id, x => x);

        var missingSourceKeys = _audioModulationMatrix.Keys.Where(k => !sourceById.ContainsKey(k.FftId)).ToArray();
        foreach (var key in missingSourceKeys)
        {
            _audioModulationMatrix.Remove(key);
        }

        foreach (var entry in _audioModulationMatrix)
        {
            var source = sourceById[entry.Key.FftId];
            if (entry.Value.AudioBinIndex >= source.BinCount)
            {
                entry.Value.AudioBinIndex = source.BinCount - 1;
            }
        }
    }

}
