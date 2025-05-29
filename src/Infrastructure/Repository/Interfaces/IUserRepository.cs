using System.Threading.Tasks;
using ecommerce.Db;

namespace ecommerce.Infrastructure.Repository.Interfaces
{  
     public interface IUserRepository {
        Task<User?> GetUserByIdAsync(string userId);
    }
}