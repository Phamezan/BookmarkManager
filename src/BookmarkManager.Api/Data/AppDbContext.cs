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
    public DbSet<AnimeScheduleCache> AnimeScheduleCaches => Set<AnimeScheduleCache>();
    public DbSet<UrlMigrationProposal> UrlMigrationProposals => Set<UrlMigrationProposal>();
    public DbSet<LibraryCatalogEntry> LibraryCatalogEntries => Set<LibraryCatalogEntry>();
    public DbSet<LibraryCatalogSyncQueueItem> LibraryCatalogSyncQueue => Set<LibraryCatalogSyncQueueItem>();
    public DbSet<TagProvenance> TagProvenances => Set<TagProvenance>();

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
            entity.Property(e => e.PreviousUrl).HasMaxLength(2048);

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
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Trigger).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Error).HasMaxLength(500);
            entity.HasIndex(e => e.CreatedAt);
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
            entity.Property(e => e.DisabledProviders).HasMaxLength(2048).HasDefaultValue("");
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
            entity.HasIndex(e => e.SourceCommandId);
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

        modelBuilder.Entity<AnimeScheduleCache>(entity =>
        {
            // Keyed by the queried AniList media id; not database-generated.
            entity.HasKey(e => e.AniListId);
            entity.Property(e => e.AniListId).ValueGeneratedNever();
            entity.Property(e => e.Status).HasMaxLength(64);
            entity.Property(e => e.ResolvedTitle).HasMaxLength(500);
            entity.Property(e => e.ResolvedCoverImageUrl).HasMaxLength(2048);
            entity.HasIndex(e => e.ExpiresAtUtc);
        });

        modelBuilder.Entity<UrlMigrationProposal>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DeadHost).HasMaxLength(255).IsRequired();
            entity.Property(e => e.OldUrl).HasMaxLength(2048).IsRequired();
            entity.Property(e => e.ProposedUrl).HasMaxLength(2048);
            entity.Property(e => e.ProposedHost).HasMaxLength(255);
            entity.Property(e => e.SeriesName).HasMaxLength(500);
            entity.Property(e => e.ChapterNumber).HasMaxLength(64);
            entity.Property(e => e.Confidence).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Detail).HasMaxLength(2000);
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();

            entity.HasOne(e => e.Bookmark)
                  .WithMany()
                  .HasForeignKey(e => e.BookmarkId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.RunId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.BookmarkId);
        });

        modelBuilder.Entity<LibraryCatalogEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ProviderId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(500).IsRequired();
            entity.Property(e => e.AlternateTitles).HasMaxLength(2000);
            entity.Property(e => e.Authors).HasMaxLength(1000);
            entity.Property(e => e.CoverImageUrl).HasMaxLength(2048);
            entity.Property(e => e.Genres).HasMaxLength(1000);
            entity.Property(e => e.Status).HasMaxLength(100);
            entity.Property(e => e.LatestChapter).HasMaxLength(100);
            entity.Property(e => e.LatestVolume).HasMaxLength(100);
            entity.Property(e => e.SourceUrl).HasMaxLength(2048).IsRequired();

            entity.HasIndex(e => new { e.Provider, e.ProviderId }).IsUnique();
            entity.HasIndex(e => e.MediaType);
            entity.HasIndex(e => e.PopularityRank);
        });

        modelBuilder.Entity<LibraryCatalogSyncQueueItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(100).IsRequired();
            entity.Property(e => e.MediaTypeQuery).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ContinuationToken).HasMaxLength(256);
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

            entity.HasIndex(e => new { e.Provider, e.Status });
            entity.HasIndex(e => e.NextAttemptAt);
        });

        modelBuilder.Entity<TagProvenance>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Tag).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Provider).HasMaxLength(100).IsRequired();

            entity.HasIndex(e => e.BookmarkId);
            entity.HasIndex(e => new { e.BookmarkId, e.Tag });
        });
    }
}
