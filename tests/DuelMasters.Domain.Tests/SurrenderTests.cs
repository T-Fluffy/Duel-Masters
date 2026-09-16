using System.Collections.Generic;
using DuelMasters.Domain;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>Pins surrender semantics: the opponent is named winner, the game
/// ends, post-game surrenders and stranger players are no-ops.</summary>
public class SurrenderTests
{
    private static DuelGame NewGame() =>
        new(new Player("A", new List<Card>()), new Player("B", new List<Card>()));

    [Fact]
    public void Surrender_NamesOpponentWinnerAndEndsGame()
    {
        var game = NewGame();
        Assert.False(game.IsGameOver);
        game.Surrender(game.Player1);
        Assert.True(game.IsGameOver);
        Assert.Same(game.Player2, game.Winner);
    }

    [Fact]
    public void Surrender_SecondSideWinsWhenTheyResign()
    {
        var game = NewGame();
        game.Surrender(game.Player2);
        Assert.Same(game.Player1, game.Winner);
    }

    [Fact]
    public void Surrender_AfterGameOver_IsNoOp()
    {
        var game = NewGame();
        game.Surrender(game.Player1);
        game.Surrender(game.Player2);
        Assert.Same(game.Player2, game.Winner);
    }

    [Fact]
    public void Surrender_StrangerPlayer_IsNoOp()
    {
        var game = NewGame();
        game.Surrender(new Player("Stranger", new List<Card>()));
        Assert.False(game.IsGameOver);
        Assert.Null(game.Winner);
    }
}
