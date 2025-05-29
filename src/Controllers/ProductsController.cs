using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoMapper;
using ecommerce.Contracts;
using ecommerce.Db;
using ecommerce.Dto;
using ecommerce.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ecommerce.Infrastructure.Repository.Interfaces;

namespace ecommerce.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ProductsController : ControllerBase
    {
        private readonly IProductRepository _repository;
        private readonly ILogger<ProductsController> _logger;
        private readonly IMapper _mapper;

        public ProductsController(IProductRepository repository, ILogger<ProductsController> logger, IMapper mapper)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<ProductDto>>> GetProducts(int page = 1, int pageSize = 10)
        {
            try
            {
                var products = await _repository.GetProductsAsync(page, pageSize);
                var productDtos = _mapper.Map<List<ProductDto>>(products);
                return Ok(productDtos);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching products.");
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error.");
            }
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<ProductDto>> GetProduct(Guid id)
        {
            try
            {
                var product = await _repository.GetProductByIdAsync(id, includeCategory: true, includeContents: true, includeMetadata: true);
                if (product == null)
                    return NotFound();

                var productDto = _mapper.Map<ProductDto>(product);
                return Ok(productDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching product with ID {ProductId}.", id);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error.");
            }
        }

        [HttpPost]
        [Authorize]
        public async Task<ActionResult<ProductDto>> CreateProduct([FromBody] ProductCreateDto productCreateDto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            try
            {
                // Check category using the same repository.
                var category = await _repository.GetCategoryByIdAsync(productCreateDto.CategoryId);
                if (category == null)
                    return BadRequest("Invalid category ID.");

                var product = _mapper.Map<ecommerce.Db.Product>(productCreateDto);
                product.Id = Guid.NewGuid();
                await _repository.AddProductAsync(product);
                await _repository.SaveChangesAsync();
                var productDto = _mapper.Map<ProductDto>(product);
                return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, productDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating product.");
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error.");
            }
        }

        [HttpPut("{id}")]
        [Authorize]
        public async Task<ActionResult<ProductDto>> UpdateProduct(Guid id, [FromBody] ProductCreateDto productCreateDto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var product = await _repository.GetProductByIdAsync(id);
            if (product == null)
                return NotFound();

            var category = await _repository.GetCategoryByIdAsync(productCreateDto.CategoryId);
            if (category == null)
                return BadRequest("Invalid category ID.");

            _mapper.Map(productCreateDto, product);

            try
            {
                await _repository.UpdateProductAsync(product);
                var updatedDto = _mapper.Map<ProductDto>(product);
                return Ok(updatedDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating product with ID {ProductId}.", id);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error.");
            }
        }

        [HttpDelete("{id}")]
        [Authorize]
        public async Task<IActionResult> DeleteProduct(Guid id)
        {
            var product = await _repository.GetProductByIdAsync(id);
            if (product == null)
                return NotFound();

            try
            {
                await _repository.DeleteProductAsync(id);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting product with ID {ProductId}.", id);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error.");
            }
        }
    }
}
