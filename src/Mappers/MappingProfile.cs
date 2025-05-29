using AutoMapper;
using ecommerce.Db;
using ecommerce.Dto;
using System;
using System.Linq;

namespace ecommerce.Mappings
{
    public class MappingProfile : Profile
    {
        public MappingProfile()
        {
            // Existing mappings
            CreateMap<Product, ProductDto>()
                .ForMember(dest => dest.Contents, opt => opt.MapFrom(src => src.Contents))
                .ForMember(dest => dest.Metadata, opt => opt.MapFrom(src => src.Metadata))
                // Add the new mappings for specifications and reviews
                .ForMember(dest => dest.Specifications, opt => opt.MapFrom(src => src.Specifications))
                .ForMember(dest => dest.Reviews, opt => opt.MapFrom(src => 
                    src.Reviews != null ? src.Reviews.Where(r => r.IsApproved).ToList() : null))
                .ForMember(dest => dest.AverageRating, opt => opt.MapFrom(src => src.AverageRating));

            // Existing mappings
            CreateMap<Content, ContentDto>().ReverseMap();
            CreateMap<ProductMetadata, ProductMetadataDto>().ReverseMap();
            CreateMap<Category, CategoryDto>().ReverseMap();
            CreateMap<ProductCreateDto, Product>()
                .ForMember(dest => dest.Contents, opt => opt.Ignore());

            // New mappings for specifications and reviews
            CreateMap<ProductSpecification, ProductSpecificationDto>();
            CreateMap<ProductSpecificationCreateDto, ProductSpecification>()
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.ProductId, opt => opt.Ignore())
                .ForMember(dest => dest.Product, opt => opt.Ignore());

            CreateMap<ProductReview, ProductReviewDto>();
            CreateMap<ProductReviewCreateDto, ProductReview>()
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.UserId, opt => opt.Ignore())
                .ForMember(dest => dest.IsVerifiedPurchase, opt => opt.Ignore())
                .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => DateTime.UtcNow))
                .ForMember(dest => dest.IsApproved, opt => opt.Ignore())
                .ForMember(dest => dest.Product, opt => opt.Ignore());
        }
    }
}