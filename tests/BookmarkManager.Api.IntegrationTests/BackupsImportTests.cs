using System.Net;
using System.Net.Http.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class BackupsImportTests : IntegrationTestBase
{
    [Fact]
    public async Task OverwriteImport_DoesNotQueueChildCommandsForRecursivelyRestoredFolder()
    {
        var trackedRootId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = trackedRootId,
                ParentId = null,
                Type = NodeType.Folder,
                Title = "Bookmarks Bar",
                Position = 0,
                BrowserNodeId = "1",
                SyncState = SyncState.Synced,
                Version = 1,
                UpdatedAt = DateTime.UtcNow
            });
            db.BookmarkNodes.AddRange(
                new BookmarkNode
                {
                    Id = folderId,
                    ParentId = trackedRootId,
                    Type = NodeType.Folder,
                    Title = "Anime",
                    Position = 0,
                    BrowserNodeId = "200",
                    SyncState = SyncState.Synced,
                    Version = 3,
                    UpdatedAt = DateTime.UtcNow
                },
                new BookmarkNode
                {
                    Id = bookmarkId,
                    ParentId = folderId,
                    Type = NodeType.Bookmark,
                    Title = "Episode 1",
                    Url = "https://example.com/e1",
                    Position = 0,
                    BrowserNodeId = "201",
                    SyncState = SyncState.Synced,
                    Version = 2,
                    UpdatedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/backups/import", new ImportBackupRequest
        {
            Overwrite = true,
            Nodes =
            [
                new BookmarkNodeDto
                {
                    Id = folderId,
                    ParentId = trackedRootId,
                    Type = NodeType.Folder,
                    Title = "Anime",
                    Position = 0
                },
                new BookmarkNodeDto
                {
                    Id = bookmarkId,
                    ParentId = folderId,
                    Type = NodeType.Bookmark,
                    Title = "Episode 1",
                    Url = "https://example.com/e1",
                    Position = 0
                }
            ]
        });

        response.EnsureSuccessStatusCode();

        await using var verifyScope = Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var restoreCommands = verifyDb.ExtensionCommands.Where(c => c.CommandType == "Restore").ToList();
        Assert.Single(restoreCommands, c => c.BookmarkId == folderId);
        Assert.DoesNotContain(restoreCommands, c => c.BookmarkId == bookmarkId);
        Assert.DoesNotContain(verifyDb.ExtensionCommands, c => c.CommandType == "Create" && c.BookmarkId == bookmarkId);
    }

    [Fact]
    public async Task Import_MetadataOnlyUpdate_RestoresMetadataWithoutEnqueuingBrowserCommands()
    {
        var trackedRootId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = trackedRootId,
                ParentId = null,
                Type = NodeType.Folder,
                Title = "Bookmarks Bar",
                Position = 0,
                BrowserNodeId = "1",
                SyncState = SyncState.Synced,
                Version = 1,
                UpdatedAt = DateTime.UtcNow
            });
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = bookmarkId,
                ParentId = trackedRootId,
                Type = NodeType.Bookmark,
                Title = "Solo Leveling",
                Url = "https://example.com/sl",
                Position = 0,
                BrowserNodeId = "301",
                SyncState = SyncState.Synced,
                Version = 5,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/backups/import", new ImportBackupRequest
        {
            Overwrite = false,
            Nodes =
            [
                new BookmarkNodeDto
                {
                    Id = bookmarkId,
                    ParentId = trackedRootId,
                    Type = NodeType.Bookmark,
                    Title = "Solo Leveling",
                    Url = "https://example.com/sl",
                    Position = 0,
                    Metadata = new BookmarkMetadataDto
                    {
                        Category = "Anime",
                        Tags = ["Action", "Hunter"],
                        Rating = 5,
                        Notes = "Imported from backup",
                        IsFavorite = true,
                        CoverImageUrl = "https://cdn.example.com/solo.jpg"
                    }
                }
            ]
        });

        response.EnsureSuccessStatusCode();

        await using var verifyScope = Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bookmark = await verifyDb.BookmarkNodes.FindAsync(bookmarkId);
        Assert.NotNull(bookmark);
        Assert.Equal("Anime", bookmark!.Category);
        Assert.Equal("Action,Hunter", bookmark.Tags);
        Assert.Equal(5, bookmark.Rating);
        Assert.Equal("Imported from backup", bookmark.Notes);
        Assert.True(bookmark.IsFavorite);
        Assert.Equal("https://cdn.example.com/solo.jpg", bookmark.CoverImageUrl);
        Assert.Empty(verifyDb.ExtensionCommands);
    }

    [Fact]
    public async Task Import_RejectsMissingParentReferences()
    {
        using var client = Factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/backups/import", new ImportBackupRequest
        {
            Overwrite = false,
            Nodes =
            [
                new BookmarkNodeDto
                {
                    Id = Guid.NewGuid(),
                    ParentId = Guid.NewGuid(),
                    Type = NodeType.Bookmark,
                    Title = "Broken",
                    Url = "https://example.com/broken",
                    Position = 0
                }
            ]
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("missing parent", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preview_MetadataOnlyUpdate_ReportsMetadataOnlyWithoutBlockingImport()
    {
        var trackedRootId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BookmarkNodes.AddRange(
                new BookmarkNode
                {
                    Id = trackedRootId,
                    ParentId = null,
                    Type = NodeType.Folder,
                    Title = "Bookmarks Bar",
                    Position = 0,
                    BrowserNodeId = "1",
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = DateTime.UtcNow
                },
                new BookmarkNode
                {
                    Id = bookmarkId,
                    ParentId = trackedRootId,
                    Type = NodeType.Bookmark,
                    Title = "Solo Leveling",
                    Url = "https://example.com/sl",
                    Position = 0,
                    BrowserNodeId = "301",
                    SyncState = SyncState.Synced,
                    Version = 5,
                    UpdatedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/backups/preview", new ImportBackupRequest
        {
            Overwrite = false,
            Nodes =
            [
                new BookmarkNodeDto
                {
                    Id = bookmarkId,
                    ParentId = trackedRootId,
                    Type = NodeType.Bookmark,
                    Title = "Solo Leveling",
                    Url = "https://example.com/sl",
                    Position = 0,
                    Metadata = new BookmarkMetadataDto
                    {
                        Category = "Anime",
                        Tags = ["Action"]
                    }
                }
            ]
        });

        response.EnsureSuccessStatusCode();
        var preview = await response.Content.ReadFromJsonAsync<BackupImportPreviewDto>();

        Assert.NotNull(preview);
        Assert.True(preview!.CanImport);
        Assert.Equal(1, preview.MetadataOnlyCount);
        Assert.Empty(preview.Diagnostics);
        Assert.Contains(preview.Items, item => item.Action == "MetadataOnly" && item.NodeId == bookmarkId);
    }

    [Fact]
    public async Task Preview_InvalidGraph_ReturnsDiagnostics()
    {
        using var client = Factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/backups/preview", new ImportBackupRequest
        {
            Overwrite = false,
            Nodes =
            [
                new BookmarkNodeDto
                {
                    Id = Guid.NewGuid(),
                    ParentId = Guid.NewGuid(),
                    Type = NodeType.Bookmark,
                    Title = "Broken",
                    Url = "https://example.com/broken",
                    Position = 0
                }
            ]
        });

        response.EnsureSuccessStatusCode();
        var preview = await response.Content.ReadFromJsonAsync<BackupImportPreviewDto>();

        Assert.NotNull(preview);
        Assert.False(preview!.CanImport);
        Assert.Contains(preview.Diagnostics, diagnostic => diagnostic.Detail.Contains("missing parent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Import_DestinationFolder_RemapsTopLevelNodesUnderSelectedFolder()
    {
        var rootId = Guid.NewGuid();
        var destinationFolderId = Guid.NewGuid();
        var importedFolderId = Guid.NewGuid();
        var importedBookmarkId = Guid.NewGuid();

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BookmarkNodes.AddRange(
                new BookmarkNode
                {
                    Id = rootId,
                    ParentId = null,
                    Type = NodeType.Folder,
                    Title = "Bookmarks Bar",
                    Position = 0,
                    BrowserNodeId = "1",
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = DateTime.UtcNow
                },
                new BookmarkNode
                {
                    Id = destinationFolderId,
                    ParentId = rootId,
                    Type = NodeType.Folder,
                    Title = "Imported",
                    Position = 0,
                    BrowserNodeId = "200",
                    SyncState = SyncState.Synced,
                    Version = 2,
                    UpdatedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/backups/import", new ImportBackupRequest
        {
            Overwrite = false,
            SourceKind = "ChromeBookmarks",
            DestinationFolderId = destinationFolderId,
            Nodes =
            [
                new BookmarkNodeDto
                {
                    Id = importedFolderId,
                    ParentId = null,
                    Type = NodeType.Folder,
                    Title = "Anime",
                    Position = 0
                },
                new BookmarkNodeDto
                {
                    Id = importedBookmarkId,
                    ParentId = importedFolderId,
                    Type = NodeType.Bookmark,
                    Title = "Episode 1",
                    Url = "https://example.com/e1",
                    Position = 0
                }
            ]
        });

        response.EnsureSuccessStatusCode();

        await using var verifyScope = Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var importedFolder = await verifyDb.BookmarkNodes.FindAsync(importedFolderId);
        var importedBookmark = await verifyDb.BookmarkNodes.FindAsync(importedBookmarkId);

        Assert.NotNull(importedFolder);
        Assert.NotNull(importedBookmark);
        Assert.Equal(destinationFolderId, importedFolder!.ParentId);
        Assert.Equal(importedFolderId, importedBookmark!.ParentId);
    }

    [Fact]
    public async Task Import_DestinationFolder_RejectsBookmarkDestination()
    {
        var rootId = Guid.NewGuid();
        var bookmarkDestinationId = Guid.NewGuid();

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BookmarkNodes.AddRange(
                new BookmarkNode
                {
                    Id = rootId,
                    ParentId = null,
                    Type = NodeType.Folder,
                    Title = "Bookmarks Bar",
                    Position = 0,
                    BrowserNodeId = "1",
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = DateTime.UtcNow
                },
                new BookmarkNode
                {
                    Id = bookmarkDestinationId,
                    ParentId = rootId,
                    Type = NodeType.Bookmark,
                    Title = "Not a folder",
                    Url = "https://example.com/destination",
                    Position = 0,
                    BrowserNodeId = "300",
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/backups/import", new ImportBackupRequest
        {
            Overwrite = false,
            SourceKind = "ChromeBookmarks",
            DestinationFolderId = bookmarkDestinationId,
            Nodes =
            [
                new BookmarkNodeDto
                {
                    Id = Guid.NewGuid(),
                    ParentId = null,
                    Type = NodeType.Bookmark,
                    Title = "Broken",
                    Url = "https://example.com/broken",
                    Position = 0
                }
            ]
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("destination folder", body, StringComparison.OrdinalIgnoreCase);
    }
}
