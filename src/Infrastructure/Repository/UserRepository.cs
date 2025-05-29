using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ecommerce.Infrastructure.Repository.Interfaces;
using ecommerce.Db;


namespace ecommerce.Infrastructure.Repository
{
    public class UserRepository : IUserRepository
    {
        private readonly ecommerceContext _context;

        public UserRepository(ecommerceContext context)
        {
            _context = context;
        }

        public async Task<User?> GetUserByIdAsync(string userId)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u => u.Id == userId);
        }
    }
}