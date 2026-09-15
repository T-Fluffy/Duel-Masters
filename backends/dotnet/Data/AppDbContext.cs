using DuelMasters.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace DuelMasters.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Card> Cards => Set<Card>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Deck> Decks => Set<Deck>();
    public DbSet<DeckCard> DeckCards => Set<DeckCard>();

    public DbSet<PlayerProfile> PlayerProfiles => Set<PlayerProfile>();
    public DbSet<DuelResult> DuelResults => Set<DuelResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Card>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).HasMaxLength(128);
            e.Property(c => c.Civilization).HasMaxLength(32);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<Deck>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasOne(d => d.User)
                .WithMany(u => u.Decks)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeckCard>(e =>
        {
            e.HasKey(dc => dc.Id);
            e.HasOne(dc => dc.Deck)
                .WithMany(d => d.Cards)
                .HasForeignKey(dc => dc.DeckId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(dc => dc.Card)
                .WithMany(c => c.DeckCards)
                .HasForeignKey(dc => dc.CardId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlayerProfile>(e =>
        {
            e.HasKey(p => p.UserId);
            e.HasOne(p => p.User)
                .WithOne()
                .HasForeignKey<PlayerProfile>(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(p => p.Nickname).HasMaxLength(64).IsRequired();
            e.Property(p => p.Bio).HasMaxLength(500);
            e.HasIndex(p => p.Nickname).IsUnique();
        });

        modelBuilder.Entity<DuelResult>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasOne(d => d.User)
                .WithMany()
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.Deck)
                .WithMany()
                .HasForeignKey(d => d.DeckId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(d => new { d.UserId, d.PlayedAtUtc });
        });
    }
}