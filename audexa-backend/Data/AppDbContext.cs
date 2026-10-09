using audexa_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace audexa_backend.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<AudioFile> AudioFiles => Set<AudioFile>();

    public DbSet<AudioConfiguration> AudioConfigurations => Set<AudioConfiguration>();

    public DbSet<OutputMapping> OutputMappings => Set<OutputMapping>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AudioConfiguration>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<AudioConfiguration>()
            .HasMany(x => x.Mappings)
            .WithOne(x => x.AudioConfiguration)
            .HasForeignKey(x => x.AudioConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OutputMapping>()
            .HasIndex(x => new { x.AudioConfigurationId, x.RoomId })
            .IsUnique();

        modelBuilder.Entity<OutputMapping>()
            .HasIndex(x => new { x.AudioConfigurationId, x.OutputId })
            .IsUnique();
    }
}