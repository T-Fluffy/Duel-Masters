using DuelMasters.Domain;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>Pins the ranked-ladder math: equal players split K/2, underdog wins
/// pay more than favourite wins, and every game is zero-sum so rating points
/// are never created or destroyed by the winner seam.</summary>
public class EloRatingTests
{
    [Fact]
    public void EqualRatings_SplitSixteenEachWay()
    {
        var (winner, loser) = EloRating.Apply(1000, 1000);
        Assert.Equal(1016, winner);
        Assert.Equal(984, loser);
    }

    [Fact]
    public void UnderdogWin_PaysTwentyFour()
    {
        var (winner, loser) = EloRating.Apply(1000, 1200);
        Assert.Equal(1024, winner);
        Assert.Equal(1176, loser);
    }

    [Fact]
    public void FavouriteWin_PaysOnlyEight()
    {
        var (winner, loser) = EloRating.Apply(1200, 1000);
        Assert.Equal(1208, winner);
        Assert.Equal(992, loser);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1000, 1200)]
    [InlineData(1200, 1000)]
    [InlineData(800, 1600)]
    public void EveryGame_IsZeroSum(int winnerRating, int loserRating)
    {
        var (winner, loser) = EloRating.Apply(winnerRating, loserRating);
        Assert.Equal(0, (winner - winnerRating) + (loser - loserRating));
    }

    [Fact]
    public void Expected_EqualRatings_IsHalf()
    {
        Assert.Equal(0.5, EloRating.Expected(1000, 1000));
    }

    [Fact]
    public void DefaultRating_IsOneThousand()
    {
        Assert.Equal(1000, EloRating.DefaultRating);
    }
}
