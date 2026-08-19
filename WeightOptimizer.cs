namespace 六合分析软件;

public sealed record ModelWeights(double AI, double ML, double State, double V7)
{
    public static ModelWeights Default { get; } = new(0.40, 0.40, 0.20, 0.00);
    public double Sum => AI + ML + State + V7;

    public IReadOnlyDictionary<string, double> AsDictionary() =>
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["AI"] = AI,
            ["ML"] = ML,
            ["State"] = State,
            ["V7"] = V7
        };
}

public sealed record ModelFeedback(IReadOnlyDictionary<string, double> Utility, string Reason);

/// <summary>
/// Applies a deliberately small update and projects the result onto a capped simplex.
/// </summary>
public sealed class WeightOptimizer
{
    public const double MaximumSingleAdjustment = 0.05;
    public const double MaximumWeight = 0.70;

    private static readonly string[] Names = { "AI", "ML", "State", "V7" };

    public ModelWeights Adjust(ModelWeights current, ModelFeedback feedback)
    {
        double[] old = NormalizeCurrent(current);
        double[] utility = Names.Select(name =>
                feedback.Utility.TryGetValue(name, out double value) && double.IsFinite(value) ? value : 0)
            .ToArray();
        double mean = utility.Average();
        double maxDeviation = utility.Select(value => Math.Abs(value - mean)).DefaultIfEmpty(0).Max();
        if (maxDeviation < 1e-12)
            return FromArray(old);

        var lower = old.Select(value => Math.Max(0, value - MaximumSingleAdjustment)).ToArray();
        var upper = old.Select(value => Math.Min(MaximumWeight, value + MaximumSingleAdjustment)).ToArray();
        var candidate = old.Select((value, index) =>
            Math.Clamp(value + MaximumSingleAdjustment * (utility[index] - mean) / maxDeviation,
                lower[index], upper[index])).ToArray();

        ProjectToUnitSum(candidate, lower, upper);
        return FromArray(candidate);
    }

    public static ModelWeights Normalize(ModelWeights weights) => FromArray(NormalizeCurrent(weights));

    private static double[] NormalizeCurrent(ModelWeights weights)
    {
        double[] values = { weights.AI, weights.ML, weights.State, weights.V7 };
        if (values.Any(value => !double.IsFinite(value) || value < 0))
            return new[] { .40, .40, .20, 0d };
        for (int i = 0; i < values.Length; i++) values[i] = Math.Min(MaximumWeight, values[i]);
        double sum = values.Sum();
        if (sum <= 1e-12) return new[] { .40, .40, .20, 0d };
        for (int i = 0; i < values.Length; i++) values[i] /= sum;
        ProjectToUnitSum(values, new double[4], Enumerable.Repeat(MaximumWeight, 4).ToArray());
        return values;
    }

    private static void ProjectToUnitSum(double[] values, double[] lower, double[] upper)
    {
        for (int pass = 0; pass < 16; pass++)
        {
            double residual = 1.0 - values.Sum();
            if (Math.Abs(residual) < 1e-12) break;
            var eligible = Enumerable.Range(0, values.Length)
                .Where(i => residual > 0 ? values[i] < upper[i] - 1e-12 : values[i] > lower[i] + 1e-12)
                .ToArray();
            if (eligible.Length == 0) break;
            double share = residual / eligible.Length;
            foreach (int i in eligible)
                values[i] = Math.Clamp(values[i] + share, lower[i], upper[i]);
        }
        double finalResidual = 1.0 - values.Sum();
        int target = Enumerable.Range(0, values.Length)
            .FirstOrDefault(i => finalResidual >= 0 ? values[i] + finalResidual <= upper[i] + 1e-10 : values[i] + finalResidual >= lower[i] - 1e-10);
        values[target] = Math.Clamp(values[target] + finalResidual, lower[target], upper[target]);
    }

    private static ModelWeights FromArray(IReadOnlyList<double> values) =>
        new(values[0], values[1], values[2], values[3]);
}
