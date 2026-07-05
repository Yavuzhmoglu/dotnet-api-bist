using System;
using CoreApp.Models;

namespace CoreApp.Services
{
    // Tunable via appsettings.json ("AccumulationWeights" section) -- see Program.cs binding.
    public sealed class AccumulationWeights
    {
        public int VolumeWindow { get; set; } = 20;
        public int CompressionWindow { get; set; } = 60;
        public int CmfWindow { get; set; } = 20;
        public int CmfSlopeWindow { get; set; } = 10;
        public int ObvWindow { get; set; } = 20;
        public int VwapWindow { get; set; } = 20;

        public double WeightVolume { get; set; } = 0.25;
        public double WeightCompression { get; set; } = 0.20;
        public double WeightCmf { get; set; } = 0.30;
        public double WeightCmfSlope { get; set; } = 0.10;
        public double WeightObv { get; set; } = 0.10;
        public double WeightVwap { get; set; } = 0.05;

        public double LogisticK { get; set; } = 1.0;
        public double VwapProximityPct { get; set; } = 0.03; // |deviation| at/above which VWAP proximity score hits 0

        public double CompositeThresholdHigh { get; set; } = 70;
        public double CompositeThresholdBreakout { get; set; } = 50;
        public double CmfSignThreshold { get; set; } = 0.05;

        // Minimum bars of history before a bar is considered valid for scoring.
        public int MinHistoryBars { get; set; } = 70;
    }

    public sealed record AccumulationComponents
    {
        public bool DataSufficient { get; init; }

        // Raw indicator readings (signed, for classification / transparency)
        public double VolumeZ { get; init; }
        public double CompressionPercentile { get; init; }
        public double Cmf { get; init; }
        public double CmfSlope { get; init; }
        public double VwapDeviationPct { get; init; }

        // Per-component "notability" (0-100, 0 = nothing unusual, 100 = extreme) --
        // already direction-stripped, safe to average without sign cancellation.
        public double VolumeNotability { get; init; }
        public double CompressionNotability { get; init; }
        public double CmfNotability { get; init; }
        public double CmfSlopeNotability { get; init; }
        public double ObvNotability { get; init; }
        public double VwapNotability { get; init; }

        public double Score { get; init; }        // 0-100 weighted composite of the notability components
        public double Confidence { get; init; }    // 0-100, % of components independently elevated (>=60)
    }

    public static class AccumulationScoring
    {
        /// <summary>
        /// Pure function: reads dailyCandles[0..index] only, never index+1 or beyond.
        /// This is the single choke-point that guarantees the live score can never look ahead --
        /// callers computing historical/backtest scores must use this same function, never a
        /// reimplementation, so backtests validate exactly what runs live.
        ///
        /// Design note: "notability" components are deliberately direction-stripped (0=nothing
        /// unusual, 100=extreme) BEFORE being weighted-averaged, so one strongly elevated component
        /// can't be diluted/cancelled by other neutral or oppositely-signed components. Direction
        /// (accumulation vs. distribution) is a separate decision made from the raw Cmf sign, not
        /// from the magnitude Score.
        /// </summary>
        public static AccumulationComponents Compute(IReadOnlyList<Candle1> dailyCandles, int index, AccumulationWeights w)
        {
            if (index < w.MinHistoryBars)
                return new AccumulationComponents { DataSufficient = false };

            int endExclusive = index + 1;

            // --- Volume anomaly: robust (MAD-based) z-score vs trailing window.
            //     Only elevated (high) volume is "notable" for accumulation purposes -- low volume
            //     isn't evidence of anything, so this is one-sided by construction. ---
            var volumes = new double[endExclusive];
            for (int i = 0; i < endExclusive; i++) volumes[i] = dailyCandles[i].Volume;
            double volumeZ = Indicators.RobustZScore(volumes, endExclusive, w.VolumeWindow);
            double volumeLogistic = Indicators.Logistic0to100(volumeZ, w.LogisticK);
            double volumeNotability = Math.Clamp(2.0 * (volumeLogistic - 50.0), 0, 100);

            // --- Range compression: percentile rank of today's true range vs its own history.
            //     Only a TIGHT range (low percentile) is notable for silent accumulation; a wide
            //     range is simply "not compressed", not an opposing signal -- one-sided. ---
            var trueRanges = new double[endExclusive];
            trueRanges[0] = dailyCandles[0].Range;
            for (int i = 1; i < endExclusive; i++)
                trueRanges[i] = Indicators.TrueRange(dailyCandles[i], dailyCandles[i - 1]);
            double compressionPercentile = Indicators.PercentileRank(trueRanges, endExclusive, w.CompressionWindow);
            double compressionNotability = double.IsNaN(compressionPercentile) ? 0.0 : 100.0 - compressionPercentile;

            // --- Chaikin Money Flow: level (buying/selling pressure) + slope (strengthening/fading).
            //     Genuinely bidirectional -- both strong positive and strong negative CMF are
            //     notable (accumulation vs. distribution), so notability uses |deviation from 50|. ---
            double cmf = Indicators.CMF(dailyCandles, endExclusive, w.CmfWindow);
            double cmfSlope = Indicators.CMFSlope(dailyCandles, endExclusive, w.CmfWindow, w.CmfSlopeWindow);
            double cmfLogistic = Indicators.Logistic0to100(double.IsNaN(cmf) ? double.NaN : cmf / 0.10, w.LogisticK);
            double cmfSlopeLogistic = Indicators.Logistic0to100(double.IsNaN(cmfSlope) ? double.NaN : cmfSlope * 50.0, w.LogisticK);
            double cmfNotability = Math.Clamp(2.0 * Math.Abs(cmfLogistic - 50.0), 0, 100);
            double cmfSlopeNotability = Math.Clamp(2.0 * Math.Abs(cmfSlopeLogistic - 50.0), 0, 100);

            // --- OBV slope, normalized by average volume so it's comparable across symbols of very
            //     different liquidity. Bidirectional (rising vs falling OBV), notability = |dev from 50|. ---
            double obvSlopeRaw = Indicators.OBVSlope(dailyCandles, endExclusive, w.ObvWindow);
            double avgVol = Indicators.SMA(volumes, endExclusive, w.ObvWindow);
            double obvNormalized = (double.IsNaN(avgVol) || avgVol < 1e-9) ? double.NaN : obvSlopeRaw / avgVol;
            double obvLogistic = Indicators.Logistic0to100(double.IsNaN(obvNormalized) ? double.NaN : obvNormalized * 5.0, w.LogisticK);
            double obvNotability = Math.Clamp(2.0 * Math.Abs(obvLogistic - 50.0), 0, 100);

            // --- VWAP deviation: proximity-based directly (0-100), not routed through the logistic --
            //     closer to VWAP (small |deviation|) is more notable ("coiling"), scaled by VwapProximityPct. ---
            double vwapDeviation = Indicators.VWAPDeviation(dailyCandles, index, w.VwapWindow);
            double vwapNotability;
            if (double.IsNaN(vwapDeviation) || w.VwapProximityPct <= 0)
            {
                vwapNotability = 0.0;
            }
            else
            {
                double frac = Math.Min(1.0, Math.Abs(vwapDeviation) / w.VwapProximityPct);
                vwapNotability = Math.Clamp(100.0 * (1.0 - frac), 0, 100);
            }

            double totalWeight = w.WeightVolume + w.WeightCompression + w.WeightCmf
                                 + w.WeightCmfSlope + w.WeightObv + w.WeightVwap;
            if (totalWeight <= 0) totalWeight = 1;

            double score =
                (volumeNotability * w.WeightVolume +
                 compressionNotability * w.WeightCompression +
                 cmfNotability * w.WeightCmf +
                 cmfSlopeNotability * w.WeightCmfSlope +
                 obvNotability * w.WeightObv +
                 vwapNotability * w.WeightVwap) / totalWeight;
            score = Math.Clamp(score, 0, 100);

            Span<double> notabilities = stackalloc double[]
            {
                volumeNotability, compressionNotability, cmfNotability, cmfSlopeNotability, obvNotability, vwapNotability
            };
            int elevated = 0;
            foreach (var n in notabilities)
                if (n >= 60.0) elevated++;
            double confidence = 100.0 * elevated / notabilities.Length;

            return new AccumulationComponents
            {
                DataSufficient = true,
                VolumeZ = volumeZ,
                CompressionPercentile = compressionPercentile,
                Cmf = cmf,
                CmfSlope = cmfSlope,
                VwapDeviationPct = vwapDeviation,
                VolumeNotability = volumeNotability,
                CompressionNotability = compressionNotability,
                CmfNotability = cmfNotability,
                CmfSlopeNotability = cmfSlopeNotability,
                ObvNotability = obvNotability,
                VwapNotability = vwapNotability,
                Score = score,
                Confidence = confidence
            };
        }
    }
}
