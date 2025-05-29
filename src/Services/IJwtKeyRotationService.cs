using System.Threading.Tasks;

namespace ecommerce.Services
{
    public interface IJwtKeyRotationService
    {
        Task RotateKeysIfNeededAsync();
    }
}