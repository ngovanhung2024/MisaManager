namespace MisaManager.Core.Tests;

public sealed class InterferenceRetryTests
{
    [Theory]
    [InlineData("FOCUS_FAILED")]
    [InlineData("FOCUS_LOST")]
    [InlineData("TARGET_NOT_FOUND")]
    [InlineData("STALE_NATIVE_FRAME")]
    [InlineData("GRID_CHANGED")]
    [InlineData("GRID_OFFSCREEN")]
    [InlineData("NATIVE_BUSY")]
    [InlineData("READBACK_UNAVAILABLE")]
    [InlineData("INPUT_UNCERTAIN")]
    [InlineData("ROW_INPUT_UNCERTAIN")]
    [InlineData("SCOPE_IDENTITY_CHANGED")]
    [InlineData("SALES_SURFACE_MISSING")]
    [InlineData("COMMAND_WINDOW_MISSING")]
    public void CausedByUserInterferenceCodesAreTransient(string code) => Assert.True(InterferenceRetry.IsTransient(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("focus_lost")]
    [InlineData("FOCUS_LOST ")]
    [InlineData("ITEM_AMBIGUOUS")]
    [InlineData("ITEM_UNIT_CONFLICT")]
    [InlineData("TOTAL_MISMATCH")]
    [InlineData("SAVE_UNCERTAIN")]
    [InlineData("UNKNOWN")]
    public void BusinessAndUnknownCodesAreNeverRetried(string? code) => Assert.False(InterferenceRetry.IsTransient(code));

    [Theory]
    [InlineData(1, 400)]
    [InlineData(2, 900)]
    [InlineData(3, 1600)]
    [InlineData(4, 1600)]
    [InlineData(10, 1600)]
    public void BackOffGrowsAndThenStaysAtTheCeiling(int attempt, int milliseconds)
        => Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), InterferenceRetry.Delay(attempt));

    [Fact]
    public void BackOffNeverShrinksAcrossAttempts()
    {
        var delays = Enumerable.Range(1, 6).Select(InterferenceRetry.Delay).ToArray();

        Assert.Equal(delays.Order(), delays);
    }

    [Fact]
    public void RetryLimitsAreSmallAndPositive()
    {
        Assert.InRange(InterferenceRetry.StepRetries, 1, 5);
        Assert.InRange(InterferenceRetry.ReconcileRetries, 1, 5);
        Assert.InRange(InterferenceRetry.InvoiceReruns, 1, 5);
    }
}
