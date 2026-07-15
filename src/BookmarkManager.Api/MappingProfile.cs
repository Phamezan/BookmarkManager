using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api;

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        CreateMap<BookmarkNode, BookmarkNodeDto>()
            .ForMember(d => d.Metadata, o => o.MapFrom(s => new BookmarkMetadataDto
            {
                Category = s.Category,
                Status = s.Status,
                CurrentProgress = s.CurrentProgress,
                TotalProgress = s.TotalProgress,
                Tags = s.Tags != null ? s.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() : new List<string>(),
                Rating = s.Rating,
                Notes = s.Notes,
                IsFavorite = s.IsFavorite,
                CoverImageUrl = s.CoverImageUrl
            }));

        CreateMap<BookmarkNodeDto, BookmarkNode>()
            .ForMember(d => d.Category, o => o.MapFrom(s => s.Metadata != null ? s.Metadata.Category : null))
            .ForMember(d => d.Status, o => o.MapFrom(s => s.Metadata != null ? s.Metadata.Status : null))
            .ForMember(d => d.CurrentProgress, o => o.MapFrom(s => s.Metadata != null ? s.Metadata.CurrentProgress : null))
            .ForMember(d => d.TotalProgress, o => o.MapFrom(s => s.Metadata != null ? s.Metadata.TotalProgress : null))
            .ForMember(d => d.Tags, o => o.MapFrom(s => s.Metadata != null && s.Metadata.Tags.Count > 0 ? string.Join(",", s.Metadata.Tags) : null))
            .ForMember(d => d.Rating, o => o.MapFrom(s => s.Metadata != null ? s.Metadata.Rating : null))
            .ForMember(d => d.Notes, o => o.MapFrom(s => s.Metadata != null ? s.Metadata.Notes : null))
            .ForMember(d => d.IsFavorite, o => o.MapFrom(s => s.Metadata != null && s.Metadata.IsFavorite))
            .ForMember(d => d.CoverImageUrl, o => o.MapFrom(s => s.Metadata != null ? s.Metadata.CoverImageUrl : null));

        CreateMap<ActivityLogEntry, ActivityEntryDto>();
        CreateMap<BackupManifest, BackupManifestDto>()
            .ForMember(d => d.Status, o => o.MapFrom(s => s.Status))
            .ForMember(d => d.Trigger, o => o.MapFrom(s => s.Trigger));
    }
}
