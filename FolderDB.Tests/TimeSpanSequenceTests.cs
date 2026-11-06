using System;
using System.Linq;
using FolderDB.Retry;

namespace FolderDB.Tests;

public class TimeSpanSequenceTests
{
    // Two values: with one, the power is multiplier^0 = 1 even for NaN.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Exponential_WhenMultiplierIsNotFinite_Throws(double multiplier)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TimeSpanSequence.Exponential(TimeSpan.FromMilliseconds(25), 2, multiplier));
    }

    // The last value is capped, but the one before it would overflow below TimeSpan.MinValue.
    [Fact]
    public void Exponential_WhenMultiplierIsNegative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TimeSpanSequence.Exponential(TimeSpan.FromMilliseconds(25), 61, -2, max: TimeSpan.FromDays(1)));
    }

    // Max caps values only from above, so it does not stop a falling sequence.
    [Fact]
    public void Exponential_WhenNegativeStartOverflowsDespiteMax_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TimeSpanSequence.Exponential(TimeSpan.FromMilliseconds(-25), 50, max: TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Exponential_WhenStartIsNegative_ReturnsFallingValues()
    {
        var values = TimeSpanSequence.Exponential(TimeSpan.FromMilliseconds(-25), 3);

        Assert.Equal(
            [-25, -50, -100],
            values.Select(value => value.TotalMilliseconds));
    }

    [Fact]
    public void Exponential_ReturnsGrowingValuesCappedAtMax()
    {
        var values = TimeSpanSequence.Exponential(
            TimeSpan.FromMilliseconds(25),
            5,
            max: TimeSpan.FromMilliseconds(150));

        Assert.Equal(
            [25, 50, 100, 150, 150],
            values.Select(value => value.TotalMilliseconds));
    }

    // 25 ms doubled 49 times is about 1.4e16 ms, past TimeSpan.MaxValue at about 9.2e14 ms.
    [Fact]
    public void Exponential_WhenLastValueOverflowsTimeSpan_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TimeSpanSequence.Exponential(TimeSpan.FromMilliseconds(25), 50));
    }

    [Fact]
    public void Exponential_WhenLastValueOverflowsButMaxIsSet_ReturnsCappedValues()
    {
        var values = TimeSpanSequence.Exponential(TimeSpan.FromMilliseconds(25), 50, max: TimeSpan.FromDays(1));

        Assert.Equal(TimeSpan.FromDays(1), values.Last());
    }

    // Zero times the power that overflowed to infinity is NaN, which TimeSpan rejects.
    [Fact]
    public void Exponential_WhenStartIsZeroAndPowerOverflows_ReturnsZeroValues()
    {
        var values = TimeSpanSequence.Exponential(TimeSpan.Zero, 5, multiplier: 1e200);

        Assert.Equal(Enumerable.Repeat(TimeSpan.Zero, 5), values);
    }
}
