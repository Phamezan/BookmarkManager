using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Infrastructure;
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

    [HttpPost("preview")]
    public async Task<ActionResult<BackupImportPreviewDto>> PreviewAsync([FromBody] ImportBackupRequest request, CancellationToken ct)
    {
        var preview = await BuildImportPreviewAsync(request, ct);
        return Ok(preview);
    }

    [HttpPost("import")]
    public async Task<ActionResult> ImportAsync([FromBody] ImportBackupRequest request, CancellationToken ct)
    {
        if (request.Nodes is null || request.Nodes.Count == 0)
        {
            return ApiProblem.Result(
                StatusCodes.Status400BadRequest,
                ApiProblem.ValidationCode,
                "Invalid backup file",
                "The backup file does not contain any bookmark nodes.");
        }

        var existingNodesBeforeImport = await _db.BookmarkNodes.ToListAsync(ct);
        var normalizedNodes = NormalizeImportNodes(request.Nodes);
        var diagnostics = CollectDestinationDiagnostics(request, normalizedNodes, existingNodesBeforeImport);
        normalizedNodes = RemapImportedNodesToDestination(request, normalizedNodes);
        var existingActiveFolderIds = existingNodesBeforeImport
            .Where(n => n.Type == NodeType.Folder && !n.IsDeleted)
            .Select(n => n.Id)
            .ToHashSet();
        var existingNodesMap = existingNodesBeforeImport.ToDictionary(n => n.Id);

        diagnostics.AddRange(CollectImportDiagnostics(normalizedNodes, existingActiveFolderIds, existingNodesMap));
        if (diagnostics.Count > 0)
        {
            var first = diagnostics[0];
            return ApiProblem.Result(
                StatusCodes.Status400BadRequest,
                ApiProblem.ValidationCode,
                first.Title,
                first.Detail);
        }

        using var transaction = await _db.Database.BeginTransactionAsync(ct);

        if (request.Overwrite)
        {
            await CreateBackupSnapshotInternalAsync(ct);

            var rootChildren = await _db.BookmarkNodes
                .Where(n => !n.IsDeleted && n.ParentId == null)
                .ToListAsync(ct);

            foreach (var child in rootChildren)
            {
                var deletedAt = DateTime.UtcNow;
                var purgeAfter = deletedAt.AddDays(30);

                child.IsDeleted = true;
                child.DeletedAt = deletedAt;
                child.PurgeAfter = purgeAfter;
                child.SyncState = SyncState.Pending;

                _db.ExtensionCommands.Add(new ExtensionCommandEntry
                {
                    Id = Guid.NewGuid(),
                    OperationId = Guid.NewGuid(),
                    CommandType = "Delete",
                    BookmarkId = child.Id,
                    BrowserNodeId = child.BrowserNodeId,
                    ExpectedVersion = child.Version,
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { recursive = true }),
                    CreatedAt = deletedAt,
                    Status = "Pending"
                });

                await MarkDeletedRecursiveAsync(child.Id, deletedAt, purgeAfter, ct);
            }
        }

        var allNodes = await _db.BookmarkNodes.ToListAsync(ct);
        existingNodesMap = allNodes.ToDictionary(n => n.Id);
        var sortedNodes = SortNodesByHierarchy(
            normalizedNodes,
            allNodes
                .Where(n => n.Type == NodeType.Folder && !n.IsDeleted)
                .Select(n => n.Id)
                .ToHashSet());
        var recursiveRestoreRootIds = new HashSet<Guid>();

        foreach (var nodeDto in sortedNodes)
        {
            if (existingNodesMap.TryGetValue(nodeDto.Id, out var existing))
            {
                if (existing.Type != nodeDto.Type)
                {
                    return ApiProblem.Result(
                        StatusCodes.Status400BadRequest,
                        ApiProblem.ValidationCode,
                        "Invalid backup file",
                        $"Node '{nodeDto.Title}' changes type for ID '{nodeDto.Id}', which is not supported.");
                }

                if (!existing.IsDeleted)
                {
                    var titleChanged = existing.Title != nodeDto.Title;
                    var urlChanged = NormalizeUrl(existing.Url) != NormalizeUrl(nodeDto.Url);
                    var parentChanged = existing.ParentId != nodeDto.ParentId;
                    var positionChanged = existing.Position != nodeDto.Position;
                    var protectedChanged = existing.IsProtected != nodeDto.IsProtected;
                    var metadataChanged = HasMetadataChanges(existing, nodeDto.Metadata);
                    var browserChanged = titleChanged || urlChanged || parentChanged || positionChanged;

                    if (!browserChanged && !protectedChanged && !metadataChanged)
                    {
                        continue;
                    }

                    ApplyImportedNodeFields(existing, nodeDto);
                    existing.UpdatedAt = DateTime.UtcNow;

                    if (!browserChanged)
                    {
                        continue;
                    }

                    existing.SyncState = SyncState.Pending;
                    existing.Version++;

                    if (titleChanged || urlChanged)
                    {
                        var updatePayload = new
                        {
                            title = existing.Title,
                            url = existing.Url
                        };

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

                    if (parentChanged || positionChanged)
                    {
                        var parentNode = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == existing.ParentId, ct);
                        var movePayload = new
                        {
                            parentBrowserNodeId = parentNode?.BrowserNodeId ?? "0",
                            position = existing.Position
                        };

                        _db.ExtensionCommands.Add(new ExtensionCommandEntry
                        {
                            Id = Guid.NewGuid(),
                            OperationId = Guid.NewGuid(),
                            CommandType = "Move",
                            BookmarkId = existing.Id,
                            BrowserNodeId = existing.BrowserNodeId,
                            ExpectedVersion = existing.Version,
                            PayloadJson = System.Text.Json.JsonSerializer.Serialize(movePayload),
                            CreatedAt = DateTime.UtcNow,
                            Status = "Pending"
                        });
                    }
                }
                else
                {
                    existing.IsDeleted = false;
                    existing.DeletedAt = null;
                    existing.PurgeAfter = null;
                    ApplyImportedNodeFields(existing, nodeDto);
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.SyncState = SyncState.Pending;
                    existing.Version++;

                    if (existing.Type == NodeType.Folder)
                    {
                        recursiveRestoreRootIds.Add(existing.Id);
                    }

                    if (nodeDto.ParentId == null || !recursiveRestoreRootIds.Contains(nodeDto.ParentId.Value))
                    {
                        var parentNode = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == nodeDto.ParentId, ct);
                        var parentBrowserNodeId = parentNode?.BrowserNodeId ?? "0";

                        var restorePayload = new
                        {
                            bookmarkId = existing.Id,
                            type = existing.Type == NodeType.Folder ? "Folder" : "Bookmark",
                            parentBrowserNodeId,
                            title = existing.Title,
                            url = existing.Url,
                            position = existing.Position,
                            children = existing.Type == NodeType.Folder
                                ? await BuildImportRestoreChildrenAsync(existing.Id, normalizedNodes)
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
                var newNode = new BookmarkNode
                {
                    Id = nodeDto.Id,
                    ParentId = nodeDto.ParentId,
                    Title = nodeDto.Title,
                    Url = nodeDto.Url,
                    Type = nodeDto.Type,
                    Position = nodeDto.Position,
                    IsProtected = nodeDto.IsProtected,
                    IsDeleted = false,
                    SyncState = SyncState.Pending,
                    Version = 1,
                    UpdatedAt = DateTime.UtcNow
                };

                ApplyImportedMetadata(newNode, nodeDto.Metadata);
                _db.BookmarkNodes.Add(newNode);
                existingNodesMap[newNode.Id] = newNode;

                if (newNode.Type == NodeType.Folder)
                {
                    recursiveRestoreRootIds.Add(newNode.Id);
                }

                if (newNode.ParentId == null || !recursiveRestoreRootIds.Contains(newNode.ParentId.Value))
                {
                    var parentNode = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == newNode.ParentId, ct);
                    var parentBrowserNodeId = parentNode?.BrowserNodeId ?? "0";

                    if (newNode.Type == NodeType.Folder)
                    {
                        var restorePayload = new
                        {
                            bookmarkId = newNode.Id,
                            type = "Folder",
                            parentBrowserNodeId,
                            title = newNode.Title,
                            url = (string?)null,
                            position = newNode.Position,
                            children = await BuildImportRestoreChildrenAsync(newNode.Id, normalizedNodes)
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
                        var createPayload = new
                        {
                            type = "Bookmark",
                            parentBrowserNodeId,
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

    private async Task<BackupImportPreviewDto> BuildImportPreviewAsync(ImportBackupRequest request, CancellationToken ct)
    {
        var preview = new BackupImportPreviewDto
        {
            Overwrite = request.Overwrite
        };

        if (request.Nodes is null || request.Nodes.Count == 0)
        {
            preview.Diagnostics.Add(new BackupImportDiagnosticDto
            {
                Severity = "Error",
                Code = ApiProblem.ValidationCode,
                Title = "Invalid backup file",
                Detail = "The backup file does not contain any bookmark nodes."
            });
            return preview;
        }

        var existingNodes = await _db.BookmarkNodes.ToListAsync(ct);
        var normalizedNodes = NormalizeImportNodes(request.Nodes);
        preview.Diagnostics = CollectDestinationDiagnostics(request, normalizedNodes, existingNodes);
        normalizedNodes = RemapImportedNodesToDestination(request, normalizedNodes);
        var existingNodesMap = existingNodes.ToDictionary(n => n.Id);
        var existingActiveFolderIds = existingNodes
            .Where(n => n.Type == NodeType.Folder && !n.IsDeleted)
            .Select(n => n.Id)
            .ToHashSet();

        preview.Diagnostics.AddRange(CollectImportDiagnostics(normalizedNodes, existingActiveFolderIds, existingNodesMap));
        if (preview.Diagnostics.Count > 0)
        {
            return preview;
        }

        var sortedNodes = SortNodesByHierarchy(normalizedNodes, existingActiveFolderIds);
        var recursiveRestoreRootIds = new HashSet<Guid>();

        if (request.Overwrite)
        {
            foreach (var node in existingNodes.Where(n => !n.IsDeleted && n.ParentId == null).OrderBy(n => n.Title))
            {
                preview.DeleteCount++;
                preview.Items.Add(new BackupImportPreviewItemDto
                {
                    NodeId = node.Id,
                    Title = node.Title,
                    Url = node.Url,
                    Type = node.Type.ToString(),
                    Action = "Delete",
                    Details = "Will be deleted to perform clean overwrite.",
                    IsRecursive = true
                });
            }
        }

        foreach (var nodeDto in sortedNodes)
        {
            if (existingNodesMap.TryGetValue(nodeDto.Id, out var existing))
            {
                if (existing.IsDeleted)
                {
                    if (existing.Type == NodeType.Folder)
                    {
                        recursiveRestoreRootIds.Add(existing.Id);
                    }

                    if (nodeDto.ParentId == null || !recursiveRestoreRootIds.Contains(nodeDto.ParentId.Value))
                    {
                        preview.RestoreCount++;
                        preview.Items.Add(new BackupImportPreviewItemDto
                        {
                            NodeId = nodeDto.Id,
                            Title = nodeDto.Title,
                            Url = nodeDto.Url,
                            Type = nodeDto.Type.ToString(),
                            Action = "Restore",
                            Details = existing.Type == NodeType.Folder
                                ? "Will restore this folder recursively from the backup tree."
                                : "Will restore this deleted bookmark.",
                            IsRecursive = existing.Type == NodeType.Folder
                        });
                    }

                    continue;
                }

                var titleChanged = existing.Title != nodeDto.Title;
                var urlChanged = NormalizeUrl(existing.Url) != NormalizeUrl(nodeDto.Url);
                var parentChanged = existing.ParentId != nodeDto.ParentId;
                var positionChanged = existing.Position != nodeDto.Position;
                var protectedChanged = existing.IsProtected != nodeDto.IsProtected;
                var metadataChanged = HasMetadataChanges(existing, nodeDto.Metadata);
                var browserChanged = titleChanged || urlChanged || parentChanged || positionChanged;

                if (!browserChanged && !protectedChanged && !metadataChanged)
                {
                    preview.SkipCount++;
                    preview.Items.Add(new BackupImportPreviewItemDto
                    {
                        NodeId = nodeDto.Id,
                        Title = nodeDto.Title,
                        Url = nodeDto.Url,
                        Type = nodeDto.Type.ToString(),
                        Action = "Skip",
                        Details = "Already matches the imported snapshot."
                    });
                    continue;
                }

                if (!browserChanged)
                {
                    preview.MetadataOnlyCount++;
                    preview.Items.Add(new BackupImportPreviewItemDto
                    {
                        NodeId = nodeDto.Id,
                        Title = nodeDto.Title,
                        Url = nodeDto.Url,
                        Type = nodeDto.Type.ToString(),
                        Action = "MetadataOnly",
                        Details = protectedChanged && metadataChanged
                            ? "Will update metadata and protected-state fields without browser sync commands."
                            : protectedChanged
                                ? "Will update protected-state only without browser sync commands."
                                : "Will update metadata only without browser sync commands."
                    });
                    continue;
                }

                preview.UpdateCount++;
                preview.Items.Add(new BackupImportPreviewItemDto
                {
                    NodeId = nodeDto.Id,
                    Title = nodeDto.Title,
                    Url = nodeDto.Url,
                    Type = nodeDto.Type.ToString(),
                    Action = "Update",
                    Details = DescribeBrowserChanges(titleChanged, urlChanged, parentChanged, positionChanged, metadataChanged, protectedChanged)
                });
                continue;
            }

            if (nodeDto.Type == NodeType.Folder)
            {
                recursiveRestoreRootIds.Add(nodeDto.Id);
            }

            if (nodeDto.ParentId == null || !recursiveRestoreRootIds.Contains(nodeDto.ParentId.Value))
            {
                preview.CreateCount++;
                preview.Items.Add(new BackupImportPreviewItemDto
                {
                    NodeId = nodeDto.Id,
                    Title = nodeDto.Title,
                    Url = nodeDto.Url,
                    Type = nodeDto.Type.ToString(),
                    Action = nodeDto.Type == NodeType.Folder ? "Restore" : "Create",
                    Details = nodeDto.Type == NodeType.Folder
                        ? "Will create this folder and restore its descendants recursively."
                        : "Will create this bookmark from the backup.",
                    IsRecursive = nodeDto.Type == NodeType.Folder
                });
            }
        }

        return preview;
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

    private static List<BookmarkNodeDto> SortNodesByHierarchy(List<BookmarkNodeDto> nodes, IReadOnlyCollection<Guid> existingFolderIds)
    {
        var result = new List<BookmarkNodeDto>();
        var remaining = new List<BookmarkNodeDto>(nodes);
        var addedIds = existingFolderIds.ToHashSet();

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

    private static List<BookmarkNodeDto> NormalizeImportNodes(List<BookmarkNodeDto> nodes)
        => nodes.Select(node => new BookmarkNodeDto
        {
            Id = node.Id,
            ParentId = node.ParentId,
            Type = node.Type,
            Title = node.Title?.Trim() ?? string.Empty,
            Url = NormalizeUrl(node.Url),
            Position = Math.Max(0, node.Position),
            IsProtected = node.IsProtected,
            SyncState = node.SyncState,
            Version = node.Version,
            UpdatedAt = node.UpdatedAt,
            Metadata = CloneMetadata(node.Metadata),
            IsDeleted = node.IsDeleted,
            DeletedAt = node.DeletedAt,
            PurgeAfter = node.PurgeAfter,
            BrowserNodeId = node.BrowserNodeId,
            ParentBrowserNodeId = node.ParentBrowserNodeId
        }).ToList();

    private static List<BackupImportDiagnosticDto> CollectDestinationDiagnostics(
        ImportBackupRequest request,
        List<BookmarkNodeDto> normalizedNodes,
        IReadOnlyCollection<BookmarkNode> existingNodes)
    {
        if (!request.DestinationFolderId.HasValue)
        {
            return [];
        }

        var diagnostics = new List<BackupImportDiagnosticDto>();
        var destinationFolderId = request.DestinationFolderId.Value;
        var existingNodesById = existingNodes.ToDictionary(node => node.Id);

        if (request.Overwrite)
        {
            diagnostics.Add(new BackupImportDiagnosticDto
            {
                Severity = "Error",
                Code = ApiProblem.ValidationCode,
                NodeId = destinationFolderId,
                Title = "Invalid import destination",
                Detail = "A destination folder can only be used with merge imports."
            });
            return diagnostics;
        }

        if (!existingNodesById.TryGetValue(destinationFolderId, out var destinationNode)
            || destinationNode.IsDeleted
            || destinationNode.Type != NodeType.Folder)
        {
            diagnostics.Add(new BackupImportDiagnosticDto
            {
                Severity = "Error",
                Code = ApiProblem.ValidationCode,
                NodeId = destinationFolderId,
                Title = "Invalid import destination",
                Detail = "The selected destination folder does not exist or is not an active folder."
            });
            return diagnostics;
        }

        foreach (var topLevelFolder in normalizedNodes.Where(node => node.ParentId is null && node.Type == NodeType.Folder))
        {
            if (topLevelFolder.Id == destinationFolderId || IsDescendantOfExistingNode(destinationFolderId, topLevelFolder.Id, existingNodesById))
            {
                diagnostics.Add(new BackupImportDiagnosticDto
                {
                    Severity = "Error",
                    Code = ApiProblem.ValidationCode,
                    NodeId = topLevelFolder.Id,
                    Title = "Invalid import destination",
                    Detail = $"Destination folder '{destinationNode.Title}' cannot be placed inside imported folder '{topLevelFolder.Title}'."
                });
            }
        }

        return diagnostics;
    }

    private static List<BookmarkNodeDto> RemapImportedNodesToDestination(ImportBackupRequest request, List<BookmarkNodeDto> normalizedNodes)
    {
        if (!request.DestinationFolderId.HasValue)
        {
            return normalizedNodes;
        }

        var destinationFolderId = request.DestinationFolderId.Value;
        foreach (var node in normalizedNodes.Where(node => node.ParentId is null))
        {
            node.ParentId = destinationFolderId;
        }

        return normalizedNodes;
    }

    private static bool IsDescendantOfExistingNode(Guid nodeId, Guid ancestorId, IReadOnlyDictionary<Guid, BookmarkNode> existingNodesById)
    {
        var visited = new HashSet<Guid>();
        var currentId = nodeId;

        while (existingNodesById.TryGetValue(currentId, out var node) && node.ParentId.HasValue)
        {
            if (!visited.Add(currentId))
            {
                return false;
            }

            currentId = node.ParentId.Value;
            if (currentId == ancestorId)
            {
                return true;
            }
        }

        return false;
    }

    private static BookmarkMetadataDto? CloneMetadata(BookmarkMetadataDto? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        return new BookmarkMetadataDto
        {
            Category = metadata.Category,
            Status = metadata.Status,
            CurrentProgress = metadata.CurrentProgress,
            TotalProgress = metadata.TotalProgress,
            Tags = metadata.Tags.ToList(),
            Rating = metadata.Rating,
            Notes = metadata.Notes,
            IsFavorite = metadata.IsFavorite,
            CoverImageUrl = metadata.CoverImageUrl
        };
    }

    private static string? NormalizeUrl(string? url)
        => string.IsNullOrWhiteSpace(url) ? null : url.Trim();

    private static List<BackupImportDiagnosticDto> CollectImportDiagnostics(
        List<BookmarkNodeDto> nodes,
        HashSet<Guid> existingFolderIds,
        IReadOnlyDictionary<Guid, BookmarkNode> existingNodes)
    {
        var diagnostics = new List<BackupImportDiagnosticDto>();
        var duplicateGroups = nodes
            .GroupBy(n => n.Id)
            .Where(g => g.Key == Guid.Empty || g.Count() > 1)
            .ToList();

        foreach (var duplicateGroup in duplicateGroups)
        {
            diagnostics.Add(new BackupImportDiagnosticDto
            {
                Severity = "Error",
                Code = ApiProblem.ValidationCode,
                NodeId = duplicateGroup.Key == Guid.Empty ? null : duplicateGroup.Key,
                Title = "Invalid backup file",
                Detail = duplicateGroup.Key == Guid.Empty
                    ? "One or more imported nodes are missing an ID."
                    : $"The backup file contains duplicate node ID '{duplicateGroup.Key}'."
            });
        }

        var importIds = nodes.Select(n => n.Id).ToHashSet();
        foreach (var node in nodes)
        {
            if (node.ParentId == node.Id)
            {
                diagnostics.Add(new BackupImportDiagnosticDto
                {
                    Severity = "Error",
                    Code = ApiProblem.ValidationCode,
                    NodeId = node.Id,
                    Title = "Invalid backup file",
                    Detail = $"Node '{node.Title}' cannot be its own parent."
                });
            }

            if (node.ParentId.HasValue
                && !importIds.Contains(node.ParentId.Value)
                && !existingFolderIds.Contains(node.ParentId.Value))
            {
                diagnostics.Add(new BackupImportDiagnosticDto
                {
                    Severity = "Error",
                    Code = ApiProblem.ValidationCode,
                    NodeId = node.Id,
                    Title = "Invalid backup file",
                    Detail = $"Node '{node.Title}' references a missing parent '{node.ParentId.Value}'."
                });
            }

            if (existingNodes.TryGetValue(node.Id, out var existing) && existing.Type != node.Type)
            {
                diagnostics.Add(new BackupImportDiagnosticDto
                {
                    Severity = "Error",
                    Code = ApiProblem.ValidationCode,
                    NodeId = node.Id,
                    Title = "Invalid backup file",
                    Detail = $"Node '{node.Title}' changes type for ID '{node.Id}', which is not supported."
                });
            }
        }

        var nodesById = nodes.ToDictionary(n => n.Id);
        var visitState = new Dictionary<Guid, int>();
        foreach (var node in nodes)
        {
            if (HasImportCycle(node.Id, nodesById, visitState))
            {
                diagnostics.Add(new BackupImportDiagnosticDto
                {
                    Severity = "Error",
                    Code = ApiProblem.ValidationCode,
                    NodeId = node.Id,
                    Title = "Invalid backup file",
                    Detail = $"The backup file contains a parent cycle involving '{node.Title}'."
                });
            }
        }

        return diagnostics
            .GroupBy(d => (d.NodeId, d.Detail))
            .Select(g => g.First())
            .ToList();
    }

    private static string DescribeBrowserChanges(
        bool titleChanged,
        bool urlChanged,
        bool parentChanged,
        bool positionChanged,
        bool metadataChanged,
        bool protectedChanged)
    {
        var changes = new List<string>();
        if (titleChanged) changes.Add("title");
        if (urlChanged) changes.Add("url");
        if (parentChanged) changes.Add("parent");
        if (positionChanged) changes.Add("position");
        if (metadataChanged) changes.Add("metadata");
        if (protectedChanged) changes.Add("protected-state");
        return changes.Count == 0 ? "No changes." : $"Will update {string.Join(", ", changes)}.";
    }

    private static bool HasImportCycle(
        Guid nodeId,
        IReadOnlyDictionary<Guid, BookmarkNodeDto> nodesById,
        Dictionary<Guid, int> visitState)
    {
        if (visitState.TryGetValue(nodeId, out var state))
        {
            return state == 1;
        }

        visitState[nodeId] = 1;
        if (nodesById.TryGetValue(nodeId, out var node)
            && node.ParentId.HasValue
            && nodesById.ContainsKey(node.ParentId.Value)
            && HasImportCycle(node.ParentId.Value, nodesById, visitState))
        {
            return true;
        }

        visitState[nodeId] = 2;
        return false;
    }

    private static void ApplyImportedNodeFields(BookmarkNode target, BookmarkNodeDto source)
    {
        target.ParentId = source.ParentId;
        target.Title = source.Title;
        target.Url = NormalizeUrl(source.Url);
        target.Position = source.Position;
        target.IsProtected = source.IsProtected;
        ApplyImportedMetadata(target, source.Metadata);
    }

    private static void ApplyImportedMetadata(BookmarkNode target, BookmarkMetadataDto? metadata)
    {
        target.Category = metadata?.Category;
        target.Status = metadata?.Status;
        target.CurrentProgress = metadata?.CurrentProgress;
        target.TotalProgress = metadata?.TotalProgress;
        target.Tags = metadata is { Tags.Count: > 0 } ? string.Join(",", metadata.Tags) : null;
        target.Rating = metadata?.Rating;
        target.Notes = metadata?.Notes;
        target.IsFavorite = metadata?.IsFavorite ?? false;
        target.CoverImageUrl = metadata?.CoverImageUrl;
    }

    private static bool HasMetadataChanges(BookmarkNode target, BookmarkMetadataDto? metadata)
    {
        var incomingTags = metadata is { Tags.Count: > 0 } ? string.Join(",", metadata.Tags) : null;
        return target.Category != metadata?.Category
            || target.Status != metadata?.Status
            || target.CurrentProgress != metadata?.CurrentProgress
            || target.TotalProgress != metadata?.TotalProgress
            || target.Tags != incomingTags
            || target.Rating != metadata?.Rating
            || target.Notes != metadata?.Notes
            || target.IsFavorite != (metadata?.IsFavorite ?? false)
            || target.CoverImageUrl != metadata?.CoverImageUrl;
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
