using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using ecommerce.Db;
using ecommerce.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ecommerce.Infrastructure.Repository.Interfaces;

namespace ecommerce.Controllers
{
    [Route("api/products/{productId}/reviews")]
    [ApiController]
    public class ProductReviewsController : ControllerBase
    {
        private readonly IProductRepository _repository;
        private readonly ILogger<ProductReviewsController> _logger;
        private readonly IMapper _mapper;

        public ProductReviewsController(IProductRepository repository, 
                                      ILogger<ProductReviewsController> logger, 
                                      IMapper mapper)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<ProductReviewDto>>> GetReviews(Guid productId, [FromQuery] bool includeUnapproved = false)
        {
            try
            {
                // First check if product exists
                var product = await _repository.GetProductByIdAsync(productId);
                if (product == null)
                    return NotFound($"Product with ID {productId} not found.");

                // Only admins can see unapproved reviews
                bool canSeeUnapproved = User.IsInRole("Admin");
                var reviews = await _repository.GetProductReviewsAsync(productId, 1, 20);
                var reviewDtos = _mapper.Map<List<ProductReviewDto>>(reviews);
                return Ok(reviewDtos);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching reviews for product {ProductId}.", productId);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error.");
            }
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<ProductReviewDto>> GetReview(Guid productId, Guid id)
        {
            try
            {
                var review = await _repository.GetProductReviewByIdAsync(id);
                if (review == null)
                    return NotFound();

                // Verify this review belongs to the specified product
                if (review.ProductId != productId)
                    return BadRequest("Review does not belong to the specified product.");

                // Only show approved reviews to non-admins unless it's their own review
                if (!review.IsApproved && !User.IsInRole("Admin") && review.UserId.ToString() != User.FindFirstValue(ClaimTypes.NameIdentifier))
                    return NotFound();

                var reviewDto = _mapper.Map<ProductReviewDto>(review);
                return Ok(reviewDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching review {ReviewId} for product {ProductId}.", id, productId);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error.");
            }
        }

        [HttpPost]
        [Authorize]
        public async Task<ActionResult<ProductReviewDto>> CreateReview(Guid productId, [FromBody] ProductReviewCreateDto createDto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            try
            {
                // Check if product exists
                var product = await _repository.GetProductByIdAsync(productId);
                if (product == null)
                    return NotFound($"Product with ID {productId} not found.");

                // Verify the product ID in the route matches the one in the DTO
                if (createDto.ProductId != productId)
                    return BadRequest("Product ID in the route must match the one in the request body.");

                var review = _mapper.Map<ProductReview>(createDto);
                review.Id = Guid.NewGuid();
                review.UserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("User ID not found in claims."));
                review.CreatedAt = DateTime.UtcNow;
                
                // TODO: Check if user has purchased the product
                review.IsVerifiedPurchase = false;
                
                // Auto-approve reviews if the user is an admin
                review.IsApproved = User.IsInRole("Admin");

                await _repository.AddProductReviewAsync(review);
                var reviewDto = _mapper.Map<ProductReviewDto>(review);

                return CreatedAtAction(nameof(GetReview), 
                                     new { productId = productId, id = review.Id }, 
                                     reviewDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating review for product {ProductId}.", productId);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error.");
            }
        }

        [HttpPut("{id}")]
        [Authorize]
        public async Task<ActionResult<ProductReviewDto>> UpdateReview(Guid productId, Guid id, [FromBody] ProductReviewCreateDto updateDto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            try
            {
                var review = await _repository.GetProductReviewByIdAsync(id);
                if (review == null)
                    return NotFound();

                // Verify this review belongs to the specified product
                if (review.ProductId != productId)
                    return BadRequest("Review does not belong to the specified product.");

                // Security check: Only the review author or an admin can update a review
                string? currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (review.UserId.ToString() != currentUserId && !User.IsInRole("Admin"))
                    return Forbid();

                // Map the update DTO to the existing entity, preserving metadata
                bool wasApproved = review.IsApproved;
                bool wasVerified = review.IsVerifiedPurchase;
                DateTime created = review.CreatedAt;
                // Since review.UserId is nullable (Guid?), we check and convert explicitly.
                if (!review.UserId.HasValue)
                {
                    throw new InvalidOperationException("Review UserId is null.");
                }
                Guid userId = review.UserId.Value;

                _mapper.Map(updateDto, review);

                // Restore metadata fields that shouldn't be changed
                review.UserId = userId; // Restore the original UserId
                review.CreatedAt = created;
                review.IsVerifiedPurchase = wasVerified;
                
                // Only admins can change approval status; for others, preserve the original status
                if (!User.IsInRole("Admin"))
                {
                    review.IsApproved = wasApproved;
                }

                await _repository.UpdateProductReviewAsync(review);
                var reviewDto = _mapper.Map<ProductReviewDto>(review);

                return Ok(reviewDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating review {ReviewId} for product {ProductId}.", id, productId);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error.");
            }
        }

        [HttpDelete("{id}")]
        [Authorize]
        public async Task<IActionResult> DeleteReview(Guid productId, Guid id)
        {
            try
            {
                var review = await _repository.GetProductReviewByIdAsync(id);
                if (review == null)
                    return NotFound();

                // Verify this review belongs to the specified product
                if (review.ProductId != productId)
                    return BadRequest("Review does not belong to the specified product.");

                // Security check: Only the review author or an admin can delete a review
                string? currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (review.UserId.ToString() != currentUserId && !User.IsInRole("Admin"))
                    return Forbid();

                await _repository.DeleteProductReviewAsync(id);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting review {ReviewId} for product {ProductId}.", id, productId);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error.");
            }
        }

        [HttpPost("{id}/approve")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ApproveReview(Guid productId, Guid id)
        {
            try
            {
                var review = await _repository.GetProductReviewByIdAsync(id);
                if (review == null)
                    return NotFound();

                // Verify this review belongs to the specified product
                if (review.ProductId != productId)
                    return BadRequest("Review does not belong to the specified product.");

                // Skip if already approved
                if (review.IsApproved)
                    return NoContent();

                await _repository.ApproveProductReviewAsync(id, true);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error approving review {ReviewId} for product {ProductId}.", id, productId);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error.");
            }
        }
    }
}
