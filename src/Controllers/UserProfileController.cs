using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using ecommerce.Db;
using ecommerce.Services;

using System.ComponentModel.DataAnnotations;

namespace ecommerce.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/[controller]")]
    public class UserProfileController : ControllerBase
    {
        private readonly UserManager<User> _userManager;
        private readonly ILogger<UserProfileController> _logger;
        private readonly ecommerceContext _ecommerceContext;
        private readonly ICurrentUserService _currentUserService;

        public UserProfileController(
            UserManager<User> userManager,
            ILogger<UserProfileController> logger,
            ecommerceContext ecommerceContext,
            ICurrentUserService currentUserService)
        {
            _userManager = userManager;
            _logger = logger;
            _ecommerceContext = ecommerceContext;
            _currentUserService = currentUserService;
        }

        [HttpGet]
        public async Task<IActionResult> GetProfile()
        {
            var userId = _currentUserService.UserId;
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return NotFound();
            }

            var profile = await _ecommerceContext.UserProfiles
                .FirstOrDefaultAsync(p => p.UserId == userId);

            // If profile doesn't exist yet, return empty profile data
            if (profile == null)
            {
                return Ok(new
                {
                    firstName = "",
                    lastName = "",
                    email = user.Email,
                    phone = "",
                    address = "",
                    city = "",
                    state = "",
                    postalCode = "",
                    country = "US",
                    bio = "",
                    website = "",
                    avatarUrl = ""
                });
            }

            return Ok(new
            {
                firstName = profile.FirstName,
                lastName = profile.LastName,
                email = user.Email,
                phone = profile.Phone,
                address = profile.Address,
                city = profile.City,
                state = profile.State,
                postalCode = profile.PostalCode,
                country = profile.Country,
                bio = profile.Bio,
                website = profile.Website,
                avatarUrl = profile.AvatarUrl
            });
        }

        [HttpPut]
        public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileDto profileDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var userId = _currentUserService.UserId;
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return NotFound();
            }

            var profile = await _ecommerceContext.UserProfiles
                .FirstOrDefaultAsync(p => p.UserId == userId);

            if (profile == null)
            {
                // Create new profile if it doesn't exist
                profile = new UserProfile
                {
                    UserId = userId,
                    CreatedAt = DateTime.UtcNow
                };
                _ecommerceContext.UserProfiles.Add(profile);
            }

            // Update profile properties
            profile.FirstName = profileDto.FirstName;
            profile.LastName = profileDto.LastName;
            profile.Phone = profileDto.Phone;
            profile.Address = profileDto.Address;
            profile.City = profileDto.City;
            profile.State = profileDto.State;
            profile.PostalCode = profileDto.PostalCode;
            profile.Country = profileDto.Country;
            profile.Bio = profileDto.Bio;
            profile.Website = profileDto.Website;
            profile.UpdatedAt = DateTime.UtcNow;

            await _ecommerceContext.SaveChangesAsync();

            return Ok(new
            {
                message = "Profile updated successfully"
            });
        }

        // Endpoint to save shipping info from checkout
        [HttpPost("shipping-info")]
        public async Task<IActionResult> SaveShippingInfo([FromBody] ShippingInfoDto shippingDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var userId = _currentUserService.UserId;
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var profile = await _ecommerceContext.UserProfiles
                .FirstOrDefaultAsync(p => p.UserId == userId);

            if (profile == null)
            {
                // Create new profile if it doesn't exist
                profile = new UserProfile
                {
                    UserId = userId,
                    CreatedAt = DateTime.UtcNow
                };
                _ecommerceContext.UserProfiles.Add(profile);
            }

            // Update shipping-related profile properties
            profile.FirstName = shippingDto.FirstName;
            profile.LastName = shippingDto.LastName;
            profile.Phone = shippingDto.Phone;
            profile.Address = shippingDto.Address;
            profile.City = shippingDto.City;
            profile.State = shippingDto.State;
            profile.PostalCode = shippingDto.PostalCode;
            profile.Country = shippingDto.Country;
            profile.UpdatedAt = DateTime.UtcNow;

            await _ecommerceContext.SaveChangesAsync();

            return Ok(new
            {
                message = "Shipping information saved successfully"
            });
        }
    }

    public class UpdateProfileDto
    {
        [Required]
        [StringLength(50)]
        public string FirstName { get; set; } = string.Empty;

        [Required]
        [StringLength(50)]
        public string LastName { get; set; } = string.Empty;

        [Phone]
        public string Phone { get; set; } = string.Empty;

        [StringLength(100)]
        public string Address { get; set; } = string.Empty;

        [StringLength(50)]
        public string City { get; set; } = string.Empty;

        [StringLength(50)]
        public string State { get; set; } = string.Empty;

        [StringLength(20)]
        public string PostalCode { get; set; } = string.Empty;

        [StringLength(2)]
        public string Country { get; set; } = "US";

        [StringLength(500)]
        public string Bio { get; set; } = string.Empty;

        [StringLength(100)]
        [Url]
        public string Website { get; set; } = string.Empty;
    }

    public class ShippingInfoDto
    {
        [Required]
        [StringLength(50)]
        public string FirstName { get; set; } = string.Empty;

        [Required]
        [StringLength(50)]
        public string LastName { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string Address { get; set; } = string.Empty;

        [Required]
        [StringLength(50)]
        public string City { get; set; } = string.Empty;

        [StringLength(50)]
        public string State { get; set; } = string.Empty;

        [Required]
        [StringLength(20)]
        public string PostalCode { get; set; } = string.Empty;

        [Required]
        [StringLength(2)]
        public string Country { get; set; } = "US";

        [Phone]
        public string Phone { get; set; } = string.Empty;
    }
}