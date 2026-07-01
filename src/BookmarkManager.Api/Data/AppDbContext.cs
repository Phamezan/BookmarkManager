using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<BookmarkNode> BookmarkNodes => Set<BookmarkNode>();
    public DbSet<ActivityLogEntry> ActivityLog => Set<ActivityLogEntry>();
    public DbSet<BackupManifest> BackupManifests => Set<BackupManifest>();

    public DbSet<AdminAccount> AdminAccounts => Set<AdminAccount>();
    public DbSet<ExtensionClient> ExtensionClients => Set<ExtensionClient>();
    public DbSet<AppConfig> AppConfig => Set<AppConfig>();
    public DbSet<ExtensionEventEntry> ExtensionEvents => Set<ExtensionEventEntry>();
    public DbSet<SnapshotBatch> SnapshotBatches => Set<SnapshotBatch>();
    public DbSet<SnapshotNodeMapping> SnapshotNodeMappings => Set<SnapshotNodeMapping>();
    public DbSet<ExtensionCommandEntry> ExtensionCommands => Set<ExtensionCommandEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BookmarkNode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Url).HasMaxLength(2048);
            entity.Property(e => e.Tags).HasMaxLength(2000);
            entity.Property(e => e.Category).HasMaxLength(100);
            entity.Property(e => e.Status).HasMaxLength(100);
            entity.Property(e => e.Notes).HasMaxLength(4000);
            entity.Property(e => e.CoverImageUrl).HasMaxLength(2048);

            entity.HasOne(e => e.Parent)
                  .WithMany(e => e.Children)
                  .HasForeignKey(e => e.ParentId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.ParentId);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.IsDeleted);
            entity.HasIndex(e => e.PurgeAfter);
            entity.HasIndex(e => e.SyncState);
            entity.HasIndex(e => e.Title);
            entity.HasIndex(e => e.Url);
            entity.HasIndex(e => e.Tags);
            entity.Property(e => e.BrowserNodeId).HasMaxLength(128);
            entity.Property(e => e.ParentBrowserNodeId).HasMaxLength(128);
            entity.HasIndex(e => e.BrowserNodeId);
        });

        modelBuilder.Entity<ActivityLogEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Action).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => e.Timestamp);
        });

        modelBuilder.Entity<BackupManifest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(500).IsRequired();
            entity.Property(e => e.FilePath).HasMaxLength(2048);
        });

        modelBuilder.Entity<AdminAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PasswordHash).HasMaxLength(256).IsRequired();
            entity.Property(e => e.PasswordSalt).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<ExtensionClient>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ExtensionVersion).HasMaxLength(64);
            entity.Property(e => e.BraveVersion).HasMaxLength(64);
        });

        modelBuilder.Entity<AppConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<ExtensionEventEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.EventId).IsUnique();
            entity.HasIndex(e => e.ExtensionClientId);
            entity.Property(e => e.EventType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.BrowserNodeId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.TrackedRootBrowserNodeId).HasMaxLength(128);
        });

        modelBuilder.Entity<SnapshotBatch>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.RequestId).IsUnique();
            entity.HasIndex(e => e.ExtensionClientId);
        });

        modelBuilder.Entity<SnapshotNodeMapping>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SnapshotBatchId);
            entity.Property(e => e.BrowserNodeId).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<ExtensionCommandEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OperationId).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.Property(e => e.CommandType).HasMaxLength(32).IsRequired();
            entity.Property(e => e.BrowserNodeId).HasMaxLength(128);
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
        });
    }
}
