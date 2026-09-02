using System;
using System.Collections.Generic;

namespace DuelMasters.Server.Models;

/// <summary>An application user authenticated via JWT.</summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";

    public List<Deck> Decks { get; set; } = new();
}
