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
}