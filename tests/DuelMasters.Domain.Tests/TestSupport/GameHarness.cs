using DuelMasters.Domain;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Builds a <see cref="DuelGame"/> already advanced to the Main phase with Player1
/// active, and offers helpers to arrange deterministic board states. Decks are
/// started without shuffling so setup is exactly reproducible; board scenarios
/// inject creatures/hand cards directly via the public zone lists.
/// </summary>
internal sealed class GameHarness
{
    private GameHarness(Player p1, Player p2, DuelGame game)
    {
        P1 = p1;
        P2 = p2;
        Game = game;
    }

    public Player P1 { get; }
    public Player P2 { get; }
    public DuelGame Game { get; }

    public static GameHarness AtMainPhase(int deckSize = 40)
    {
        var p1 = CardFactory.PlayerWithDeck("P1", deckSize);
        var p2 = CardFactory.PlayerWithDeck("P2", deckSize);
        var game = new DuelGame(p1, p2);
        game.StartGame(false);
        game.StartTurn();
        game.Draw();
        return new GameHarness(p1, p2, game);
    }

    /// <summary>Place a creature into a player's battle zone and return it.</summary>
    public CardInstance PutCreature(Player owner, Card card, bool tapped = false, bool sick = false)
    {
        var ci = new CardInstance(card, owner) { Zone = Zone.BattleZone };
        ci.IsTapped = tapped;
        ci.IsSummoningSick = sick;
        owner.BattleZone.Add(ci);
        return ci;
    }

    /// <summary>Place a card into a player's hand and return it.</summary>
    public CardInstance PutInHand(Player owner, Card card)
    {
        var ci = new CardInstance(card, owner) { Zone = Zone.Hand };
        owner.Hand.Add(ci);
        return ci;
    }

    /// <summary>Place a mana card (untapped by default) and return it.</summary>
    public CardInstance PutMana(Player owner, Card card, bool tapped = false)
    {
        var ci = new CardInstance(card, owner) { Zone = Zone.ManaZone };
        ci.IsTapped = tapped;
        owner.ManaZone.Add(ci);
        return ci;
    }

    /// <summary>Replace a player's face-down shields with the given cards.</summary>
    public void SetShields(Player owner, params Card[] shields)
    {
        owner.Shields.Clear();
        owner.Shields.AddRange(shields);
    }

    /// <summary>
    /// Wipe both players' non-deck zones so a test fully controls the board. The
    /// game stays in the Main phase with Player1 active.
    /// </summary>
    public void ResetBoard()
    {
        foreach (var p in new[] { P1, P2 })
        {
            p.Hand.Clear();
            p.ManaZone.Clear();
            p.BattleZone.Clear();
            p.Graveyard.Clear();
            p.Shields.Clear();
        }
    }
}
