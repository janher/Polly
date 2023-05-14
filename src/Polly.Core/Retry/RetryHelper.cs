using System;

namespace Polly.Retry;

internal static class RetryHelper
{
    private const double ExponentialFactor = 2.0;

    public static bool IsValidDelay(TimeSpan delay) => delay >= TimeSpan.Zero;

    public static TimeSpan GetRetryDelay(RetryBackoffType type, int attempt, TimeSpan baseDelay, ref double state, RandomUtil random)
    {
        if (baseDelay == TimeSpan.Zero)
        {
            return baseDelay;
        }

        return type switch
        {
            RetryBackoffType.Constant => baseDelay,
#if !NETCOREAPP
            RetryBackoffType.Linear => TimeSpan.FromMilliseconds((attempt + 1) * baseDelay.TotalMilliseconds),
            RetryBackoffType.Exponential => TimeSpan.FromMilliseconds(Math.Pow(ExponentialFactor, attempt) * baseDelay.TotalMilliseconds),
#else
            RetryBackoffType.Linear => (attempt + 1) * baseDelay,
            RetryBackoffType.Exponential => Math.Pow(ExponentialFactor, attempt) * baseDelay,
#endif
            RetryBackoffType.ExponentialWithJitter => DecorrelatedJitterBackoffV2(attempt, baseDelay, ref state, random),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The retry backoff type is not supported.")
        };
    }

    /// <summary>
    /// Generates sleep durations in an exponentially backing-off, jittered manner, making sure to mitigate any correlations.
    /// For example: 850ms, 1455ms, 3060ms.
    /// Per discussion in Polly issue https://github.com/App-vNext/Polly/issues/530, the jitter of this implementation exhibits fewer spikes and a smoother distribution than the AWS jitter formula.
    /// </summary>
    /// <param name="attempt">The current attempt.</param>
    /// <param name="baseDelay">The median delay to target before the first retry, call it <c>f (= f * 2^0).</c>
    /// Choose this value both to approximate the first delay, and to scale the remainder of the series.
    /// Subsequent retries will (over a large sample size) have a median approximating retries at time <c>f * 2^1, f * 2^2 ... f * 2^t</c> etc for try t.
    /// The actual amount of delay-before-retry for try t may be distributed between 0 and <c>f * (2^(t+1) - 2^(t-1)) for t >= 2;</c>
    /// or between 0 and <c>f * 2^(t+1)</c>, for t is 0 or 1.</param>
    /// <param name="prev">The previous state value used for calculations.</param>
    /// <param name="random">The random utility to use.</param>
    /// <remarks>
    /// This code was adopted from https://github.com/Polly-Contrib/Polly.Contrib.WaitAndRetry/blob/master/src/Polly.Contrib.WaitAndRetry/Backoff.DecorrelatedJitterV2.cs.
    /// </remarks>
    private static TimeSpan DecorrelatedJitterBackoffV2(int attempt, TimeSpan baseDelay, ref double prev, RandomUtil random)
    {
        // The original author/credit for this jitter formula is @george-polevoy .
        // Jitter formula used with permission as described at https://github.com/App-vNext/Polly/issues/530#issuecomment-526555979
        // Minor adaptations (pFactor = 4.0 and rpScalingFactor = 1 / 1.4d) by @reisenberger, to scale the formula output for easier parameterization to users.

        // A factor used within the formula to help smooth the first calculated delay.
        const double PFactor = 4.0;

        // A factor used to scale the median values of the retry times generated by the formula to be _near_ whole seconds, to aid Polly user comprehension.
        // This factor allows the median values to fall approximately at 1, 2, 4 etc seconds, instead of 1.4, 2.8, 5.6, 11.2.
        const double RpScalingFactor = 1 / 1.4d;

        // Upper-bound to prevent overflow beyond TimeSpan.MaxValue. Potential truncation during conversion from double to long
        // (as described at https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/numeric-conversions)
        // is avoided by the arbitrary subtraction of 1000. Validated by unit-test Backoff_should_not_overflow_to_give_negative_timespan.
        double maxTimeSpanDouble = (double)TimeSpan.MaxValue.Ticks - 1000;

        long targetTicksFirstDelay = baseDelay.Ticks;

        double t = attempt + random.NextDouble();
        double next = Math.Pow(2, t) * Math.Tanh(Math.Sqrt(PFactor * t));

        double formulaIntrinsicValue = next - prev;
        prev = next;

        return TimeSpan.FromTicks((long)Math.Min(formulaIntrinsicValue * RpScalingFactor * targetTicksFirstDelay, maxTimeSpanDouble));
    }
}
