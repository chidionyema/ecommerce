using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ecommerce.Db;

namespace ecommerce.Infrastructure.Repository.Interfaces
{
    public interface IContentRepository
    {
        Task<IEnumerable<Content>> GetContentsByEntityIdAsync(Guid entityId, string entityType);
        Task<Content?> GetContentByIdAsync(Guid id);
        Task AddContentsAsync(IEnumerable<Content> contents);
        void RemoveContents(IEnumerable<Content> contents);
        Task RemoveContentAsync(Content content);
        Task SaveChangesAsync();
    }
}