using System.Threading.Tasks;

namespace ecommerce.Infrastructure.Repository.Interfaces
{
    public interface IConnectionStringProvider
    {
        Task<int> GetLeaseDurationAsync();
        Task<string> GetConnectionStringAsync();
        Task UpdateConnectionStringAsync();
    }
}