using ShortP2P.LoRa.Reliability;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class SerialReconnectPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void First_Ten_Failures_Use_Fixed_Five_Seconds(int failedAttempts)
    {
        Assert.Equal(TimeSpan.FromSeconds(5), SerialReconnectPolicy.DelayAfterFailures(failedAttempts));
    }

    [Fact]
    public void Beyond_Ten_Failures_Grows_Logarithmically_Up_To_Thirty_Seconds()
    {
        var prev = SerialReconnectPolicy.DelayAfterFailures(10);
        Assert.Equal(TimeSpan.FromSeconds(5), prev);

        for (var attempt = 11; attempt <= 80; attempt++)
        {
            var delay = SerialReconnectPolicy.DelayAfterFailures(attempt);
            Assert.True(delay >= TimeSpan.FromSeconds(5), $"attempt {attempt}: {delay} < 5s");
            Assert.True(delay <= TimeSpan.FromSeconds(30), $"attempt {attempt}: {delay} > 30s");
            Assert.True(delay >= prev, $"attempt {attempt}: {delay} < previous {prev}");
            prev = delay;
        }

        Assert.Equal(TimeSpan.FromSeconds(30), SerialReconnectPolicy.DelayAfterFailures(80));
    }
}
