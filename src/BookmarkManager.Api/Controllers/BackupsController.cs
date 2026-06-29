using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BackupsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;

    public BackupsController(AppDbContext db, IMapper mapper)
    {
        _db = db;
        _mapper = mapper;
    }

    [HttpGet]
    public async Task<List<BackupManifestDto>> GetAllAsync(CancellationToken ct)
    {
        var backups = await _db.BackupManifests
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
        return _mapper.Map<List<BackupManifestDto>>(backups);
    }

    [HttpPost]
    public async Task<ActionResult<BackupManifestDto>> CreateAsync(CancellationToken ct)
    {
        var bookmarkCount = await _db.BookmarkNodes.CountAsync(n => !n.IsDeleted, ct);
        var nodes = await _db.BookmarkNodes.Where(n => !n.IsDeleted).ToListAsync(ct);
        var nodesDto = _mapper.Map<List<BookmarkNodeDto>>(nodes);

        var id = Guid.NewGuid();
        var backupDir = GetBackupsDirectory();
        var backupPath = Path.Combine(backupDir, $"{id}.json");

        var json = System.Text.Json.JsonSerializer.Serialize(nodesDto, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await System.IO.File.WriteAllTextAsync(backupPath, json, ct);

        var fileInfo = new FileInfo(backupPath);

        var backup = new BackupManifest
        {
            Id = id,
            Name = $"Backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json",
            CreatedAt = DateTime.UtcNow,
            BookmarkCount = bookmarkCount,
            SizeBytes = fileInfo.Length
        };

        _db.BackupManifests.Add(backup);
        await _db.SaveChangesAsync(ct);
        return Ok(_mapper.Map<BackupManifestDto>(backup));
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> DownloadAsync(Guid id, CancellationToken ct)
    {
        var backup = await _db.BackupManifests.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (backup is null) return NotFound();

        var backupPath = Path.Combine(GetBackupsDirectory(), $"{id}.json");
        if (!System.IO.File.Exists(backupPath)) return NotFound();

        var bytes = await System.IO.File.ReadAllBytesAsync(backupPath, ct);
        return File(bytes, "application/json", backup.Name);
    }

    [HttpPost("import")]
    public async Task<ActionResult> ImportAsync([FromBody] ImportBackupRequest request, CancellationToken ct)
    {
        using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var trackedRoots = await _db.TrackedRoots.ToListAsync(ct);
        var trackedRootIds = trackedRoots.Select(r => r.Id).ToList();

        // 1. Overwrite mode: Mark all existing active bookmarks as deleted
        if (request.Overwrite)
        {
            await CreateBackupSnapshotInternalAsync(ct);

            var rootChildren = await _db.BookmarkNodes
                .Where(n => !n.IsDeleted && n.ParentId != null && trackedRootIds.Contains(n.ParentId.Value))
                .ToListAsync(ct);

            foreach (var child in rootChildren)
            {
                child.IsDeleted = true;
                child.DeletedAt = DateTime.UtcNow;
                child.PurgeAfter = DateTime.UtcNow.AddDays(30);
                child.SyncState = SyncState.Pending;

                var deletePayload = new { recursive = true };
                _db.ExtensionCommands.Add(new ExtensionCommandEntry
                {
                    Id = Guid.NewGuid(),
                    OperationId = Guid.NewGuid(),
                    CommandType = "Delete",
                    BookmarkId = child.Id,
                    BrowserNodeId = child.BrowserNodeId,
                    ExpectedVersion = child.Version,
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(deletePayload),
                    CreatedAt = DateTime.UtcNow,
                    Status = "Pending"
                });

                await MarkDeletedRecursiveAsync(child.Id, DateTime.UtcNow, DateTime.UtcNow.AddDays(30), ct);
            }
        }

        // 2. Import the new nodes in topological hierarchy order
        var sortedNodes = SortNodesByHierarchy(request.Nodes);
        var existingNodes = await _db.BookmarkNodes.ToListAsync(ct);
        var existingNodesMap = existingNodes.ToDictionary(n => n.Id);
        var newFolderIds = new HashSet<Guid>();

        foreach (var nodeDto in sortedNodes)
        {
            if (existingNodesMap.TryGetValue(nodeDto.Id, out var existing))
            {
                if (!existing.IsDeleted)
                {
                    // Merge/Update existing node if anything changed
                    if (existing.Title != nodeDto.Title || existing.Url != nodeDto.Url)
                    {
                        existing.Title = nodeDto.Title;
                        existing.Url = nodeDto.Url;
                        existing.Position = nodeDto.Position;
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.SyncState = SyncState.Pending;
                        existing.Version++;

                        var updatePayload = new { title = existing.Title, url = existing.Url };
                        _db.ExtensionCommands.Add(new ExtensionCommandEntry
                        {
                            Id = Guid.NewGuid(),
                            OperationId = Guid.NewGuid(),
                            CommandType = "Update",
                            BookmarkId = existing.Id,
                            BrowserNodeId = existing.BrowserNodeId,
                            ExpectedVersion = existing.Version,
                            PayloadJson = System.Text.Json.JsonSerializer.Serialize(updatePayload),
                            CreatedAt = DateTime.UtcNow,
                            Status = "Pending"
                        });
                    }
                }
                else
                {
                    // Restore previously deleted node
                    existing.IsDeleted = false;
                    existing.DeletedAt = null;
                    existing.PurgeAfter = null;
                    existing.Title = nodeDto.Title;
                    existing.Url = nodeDto.Url;
                    existing.Position = nodeDto.Position;
                    existing.SyncState = SyncState.Pending;
                    existing.Version++;

                    // For restore, if parent is new, don't restore individually (it will be restored by parent's Restore command)
                    if (nodeDto.ParentId == null || !newFolderIds.Contains(nodeDto.ParentId.Value))
                    {
                        var parentNode = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == nodeDto.ParentId, ct);
                        var parentBrowserNodeId = parentNode?.BrowserNodeId ?? "0";

                        var restorePayload = new
                        {
                            bookmarkId = existing.Id,
                            type = existing.Type == NodeType.Folder ? "Folder" : "Bookmark",
                            parentBrowserNodeId = parentBrowserNodeId,
                            title = existing.Title,
                            url = existing.Url,
                            position = existing.Position,
                            children = existing.Type == NodeType.Folder
                                ? await BuildImportRestoreChildrenAsync(existing.Id, request.Nodes)
                                : null
                        };

                        _db.ExtensionCommands.Add(new ExtensionCommandEntry
                        {
                            Id = Guid.NewGuid(),
                            OperationId = Guid.NewGuid(),
                            CommandType = "Restore",
                            BookmarkId = existing.Id,
                            BrowserNodeId = null,
                            ExpectedVersion = existing.Version,
                            PayloadJson = System.Text.Json.JsonSerializer.Serialize(restorePayload),
                            CreatedAt = DateTime.UtcNow,
                            Status = "Pending"
                        });
                    }
                }
            }
            else
            {
                // Create a completely new node
                var newNode = new BookmarkNode
                {
                    Id = nodeDto.Id,
                    ParentId = nodeDto.ParentId,
                    Title = nodeDto.Title,
                    Url = nodeDto.Url,
                    Type = nodeDto.Type,
                    Position = nodeDto.Position,
                    IsDeleted = false,
                    SyncState = SyncState.Pending,
                    Version = 1,
                    UpdatedAt = DateTime.UtcNow
                };

                _db.BookmarkNodes.Add(newNode);

                if (newNode.Type == NodeType.Folder)
                {
                    newFolderIds.Add(newNode.Id);
                }

                // Generate extension commands:
                // Only if the parent is NOT a new folder created in this import (since child will be recursively created by parent's Restore command)
                if (newNode.ParentId == null || !newFolderIds.Contains(newNode.ParentId.Value))
                {
                    var parentNode = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == newNode.ParentId, ct);
                    var parentBrowserNodeId = parentNode?.BrowserNodeId ?? "0";

                    if (newNode.Type == NodeType.Folder)
                    {
                        // Use Restore command for folders so children are recursively created in one batch
                        var restorePayload = new
                        {
                            bookmarkId = newNode.Id,
                            type = "Folder",
                            parentBrowserNodeId = parentBrowserNodeId,
                            title = newNode.Title,
                            url = (string?)null,
                            position = newNode.Position,
                            children = await BuildImportRestoreChildrenAsync(newNode.Id, request.Nodes)
                        };

                        _db.ExtensionCommands.Add(new ExtensionCommandEntry
                        {
                            Id = Guid.NewGuid(),
                            OperationId = Guid.NewGuid(),
                            CommandType = "Restore",
                            BookmarkId = newNode.Id,
                            BrowserNodeId = null,
                            ExpectedVersion = newNode.Version,
                            PayloadJson = System.Text.Json.JsonSerializer.Serialize(restorePayload),
                            CreatedAt = DateTime.UtcNow,
                            Status = "Pending"
                        });
                    }
                    else
                    {
                        // Standard Create command for individual bookmarks
                        var createPayload = new
                        {
                            type = "Bookmark",
                            parentBrowserNodeId = parentBrowserNodeId,
                            title = newNode.Title,
                            url = newNode.Url,
                            position = newNode.Position
                        };

                        _db.ExtensionCommands.Add(new ExtensionCommandEntry
                        {
                            Id = Guid.NewGuid(),
                            OperationId = Guid.NewGuid(),
                            CommandType = "Create",
                            BookmarkId = newNode.Id,
                            BrowserNodeId = null,
                            ExpectedVersion = newNode.Version,
                            PayloadJson = System.Text.Json.JsonSerializer.Serialize(createPayload),
                            CreatedAt = DateTime.UtcNow,
                            Status = "Pending"
                        });
                    }
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
        return Ok();
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<ActionResult> RestoreAsync(Guid id, CancellationToken ct)
    {
        var backup = await _db.BackupManifests.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (backup is null) return NotFound();

        var backupPath = Path.Combine(GetBackupsDirectory(), $"{id}.json");
        if (!System.IO.File.Exists(backupPath)) return NotFound();

        var json = await System.IO.File.ReadAllTextAsync(backupPath, ct);
        var nodes = System.Text.Json.JsonSerializer.Deserialize<List<BookmarkNodeDto>>(json, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });

        if (nodes is null) return BadRequest("Invalid backup file content.");

        var request = new ImportBackupRequest
        {
            Nodes = nodes,
            Overwrite = true
        };

        return await ImportAsync(request, ct);
    }

    private string GetBackupsDirectory()
    {
        var dir = "/data/backups";
        if (!Directory.Exists("/data"))
        {
            dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups");
        }
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    private async Task MarkDeletedRecursiveAsync(Guid folderId, DateTime deletedAt, DateTime purgeAfter, CancellationToken ct)
    {
        var children = await _db.BookmarkNodes
            .Where(n => n.ParentId == folderId && !n.IsDeleted)
            .ToListAsync(ct);

        foreach (var child in children)
        {
            child.IsDeleted = true;
            child.DeletedAt = deletedAt;
            child.PurgeAfter = purgeAfter;
            child.SyncState = SyncState.Pending;

            if (child.Type == NodeType.Folder)
            {
                await MarkDeletedRecursiveAsync(child.Id, deletedAt, purgeAfter, ct);
            }
        }
    }

    private List<BookmarkNodeDto> SortNodesByHierarchy(List<BookmarkNodeDto> nodes)
    {
        var result = new List<BookmarkNodeDto>();
        var remaining = new List<BookmarkNodeDto>(nodes);
        var addedIds = new HashSet<Guid>();

        var existingFolderIds = _db.BookmarkNodes
            .Where(n => n.Type == NodeType.Folder && !n.IsDeleted)
            .Select(n => n.Id)
            .ToList();
        foreach (var id in existingFolderIds) addedIds.Add(id);

        int previousCount = -1;
        while (remaining.Count > 0 && remaining.Count != previousCount)
        {
            previousCount = remaining.Count;
            for (int i = remaining.Count - 1; i >= 0; i--)
            {
                var node = remaining[i];
                if (node.ParentId == null || addedIds.Contains(node.ParentId.Value))
                {
                    result.Add(node);
                    addedIds.Add(node.Id);
                    remaining.RemoveAt(i);
                }
            }
        }

        result.AddRange(remaining);
        return result;
    }

    private async Task<List<object>> BuildImportRestoreChildrenAsync(Guid parentId, List<BookmarkNodeDto> allImportNodes)
    {
        var children = allImportNodes
            .Where(n => n.ParentId == parentId)
            .OrderBy(n => n.Position)
            .ToList();

        var list = new List<object>();
        foreach (var child in children)
        {
            var childPayload = new
            {
                bookmarkId = child.Id,
                type = child.Type == NodeType.Folder ? "Folder" : "Bookmark",
                parentBrowserNodeId = "",
                title = child.Title,
                url = child.Url,
                position = child.Position,
                children = child.Type == NodeType.Folder
                    ? await BuildImportRestoreChildrenAsync(child.Id, allImportNodes)
                    : null
            };
            list.Add(childPayload);
        }
        return list;
    }

    private async Task CreateBackupSnapshotInternalAsync(CancellationToken ct)
    {
        var bookmarkCount = await _db.BookmarkNodes.CountAsync(n => !n.IsDeleted, ct);
        var nodes = await _db.BookmarkNodes.Where(n => !n.IsDeleted).ToListAsync(ct);
        var nodesDto = _mapper.Map<List<BookmarkNodeDto>>(nodes);

        var id = Guid.NewGuid();
        var backupDir = GetBackupsDirectory();
        var backupPath = Path.Combine(backupDir, $"{id}.json");

        var json = System.Text.Json.JsonSerializer.Serialize(nodesDto, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await System.IO.File.WriteAllTextAsync(backupPath, json, ct);

        var fileInfo = new FileInfo(backupPath);

        var backup = new BackupManifest
        {
            Id = id,
            Name = $"AutoBackup_PreRestore_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json",
            CreatedAt = DateTime.UtcNow,
            BookmarkCount = bookmarkCount,
            SizeBytes = fileInfo.Length
        };

        _db.BackupManifests.Add(backup);
        await _db.SaveChangesAsync(ct);
    }
}
