using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace ecommerce.Services
{
     public interface ICurrentUserService
    {
        /// <summary>
        /// Gets the current user's ID.
        /// </summary>
        string? UserId { get; }

        /// <summary>
        /// Gets the client IP address.
        /// </summary>
        string? ClientIp { get; }
    }
   public class CurrentUserService : ICurrentUserService
   { 
    private readonly IHttpContextAccessor _contextAccessor;

    public CurrentUserService(IHttpContextAccessor contextAccessor)
    {
        _contextAccessor = contextAccessor;
    }

    public string? UserId => 
        _contextAccessor.HttpContext?.User?
            .FindFirstValue(ClaimTypes.NameIdentifier);

    public string? ClientIp =>
        _contextAccessor.HttpContext?.Connection.RemoteIpAddress?
            .ToString();
  }
}
