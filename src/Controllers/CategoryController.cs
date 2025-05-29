using Microsoft.AspNetCore.Mvc;
using ecommerce.Contracts;
using ecommerce.Db;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ecommerce.Infrastructure.Repository.Interfaces;

namespace ecommerce.Controllers
{
     [Route("api/[controller]")]
    [ApiController]
    public class CategoryController : ControllerBase
    {
        private readonly IProductRepository _repository;
        private readonly ILogger<CategoryController> _logger;

        public CategoryController(IProductRepository repository, ILogger<CategoryController> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // GET: api/category
        [HttpGet]
        public async Task<ActionResult<IEnumerable<Category>>> GetCategories()
        {
            try
            {
                _logger.LogInformation("Fetching all categories.");
                var categories = await _repository.GetCategoriesAsync();
                return Ok(categories);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while fetching categories.");
                return StatusCode(500, "Internal server error");
            }
        }

        // GET: api/category/{id}
        [HttpGet("{id}")]
        public async Task<ActionResult<Category>> GetCategory(Guid id)
        {
            try
            {
                _logger.LogInformation("Fetching category with ID: {CategoryId}.", id);
                var category = await _repository.GetCategoryByIdAsync(id);
                if (category == null)
                {
                    _logger.LogWarning("Category with ID {CategoryId} not found.", id);
                    return NotFound();
                }
                return Ok(category);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while fetching category with ID {CategoryId}.", id);
                return StatusCode(500, "Internal server error");
            }
        }

        // POST: api/category
        [HttpPost]
        public async Task<ActionResult<Category>> AddCategory([FromBody] Category category)
        {
            try
            {
                if (category == null)
                {
                    return BadRequest("Category cannot be null.");
                }
                await _repository.AddCategoryAsync(category);
                await _repository.SaveChangesAsync();
                _logger.LogInformation("Category {CategoryName} added successfully.", category.Name);
                return CreatedAtAction(nameof(GetCategory), new { id = category.Id }, category);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while adding a new category.");
                return StatusCode(500, "Internal server error");
            }
        }

        // PUT: api/category/{id}
        [HttpPut("{id}")]
        public async Task<ActionResult> UpdateCategory(Guid id, [FromBody] Category category)
        {
            try
            {
                if (category == null || category.Id != id)
                {
                    return BadRequest("Category is either null or ID mismatch.");
                }
                var existingCategory = await _repository.GetCategoryByIdAsync(id);
                if (existingCategory == null)
                {
                    return NotFound();
                }
                existingCategory.Name = category.Name; // Simple update example
                await _repository.SaveChangesAsync();
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while updating category with ID {CategoryId}.", id);
                return StatusCode(500, "Internal server error");
            }
        }
    }
}
