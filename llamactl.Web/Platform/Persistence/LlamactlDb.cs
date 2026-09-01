using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Platform.Persistence;

public sealed class LlamactlDb(DbContextOptions<LlamactlDb> options) : DbContext(options)
{
    public DbSet<NodeRecord> Nodes => Set<NodeRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var node = modelBuilder.Entity<NodeRecord>();
        node.ToTable("Nodes");
        node.HasKey(x => x.Id);
        node.HasIndex(x => x.Name).IsUnique();
        node.Property(x => x.Name).HasMaxLength(128);
        node.Property(x => x.BootstrapTokenHash).HasMaxLength(64);
        node.Property(x => x.Version).IsConcurrencyToken();
    }
}