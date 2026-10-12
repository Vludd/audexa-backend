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

    public DbSet<Room> Rooms => Set<Room>();

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

        modelBuilder.Entity<Room>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<Room>()
            .Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(200);

        modelBuilder.Entity<Room>()
            .Property(x => x.Volume)
            .IsRequired()
            .HasDefaultValue(100);

        modelBuilder.Entity<Room>()
            .HasOne(x => x.AudioFile)
            .WithMany()
            .HasForeignKey(x => x.AudioFileId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Room>()
            .HasMany(x => x.OutputMappings)
            .WithOne()
            .HasForeignKey(x => x.RoomId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}