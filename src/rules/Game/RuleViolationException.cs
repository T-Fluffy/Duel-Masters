using System;

namespace DuelMasters.Domain;

/// <summary>
/// Thrown by the rules engine when a requested move violates the rules
/// (e.g. playing a card you cannot afford, attacking a sick creature).
/// </summary>
public sealed class RuleViolationException : Exception
{
    public RuleViolationException(string message) : base(message)
    {
    }
}
