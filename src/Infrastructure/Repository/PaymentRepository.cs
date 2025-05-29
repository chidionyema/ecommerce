using System;
using System.Threading.Tasks;
using ecommerce.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using ecommerce.Infrastructure.Repository.Interfaces;


namespace ecommerce.Infrastructure.Repository
{
    public class PaymentRepository : IPaymentRepository
    {
        private readonly ecommerceContext _context;
        private readonly ILogger<PaymentRepository> _logger;

        public PaymentRepository(ecommerceContext context, ILogger<PaymentRepository> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<Payment?> GetPaymentByIdAsync(Guid paymentId)
        {
            if (paymentId == Guid.Empty)
            {
                _logger.LogWarning("Attempted to retrieve payment with an empty ID.");
                throw new ArgumentException("Payment ID cannot be empty.", nameof(paymentId));
            }

            try
            {
                _logger.LogInformation("Retrieving payment record with ID: {PaymentId}", paymentId);
                var payment = await _context.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId);

                if (payment == null)
                {
                    _logger.LogWarning("Payment record with ID {PaymentId} not found.", paymentId);
                }

                return payment;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving payment record with ID {PaymentId}.", paymentId);
                throw new RepositoryException($"Error retrieving payment record with ID {paymentId}", ex);
            }
        }
        public async Task<Payment> GetPaymentByStripeSessionIdAsync(string sessionId)
        {
            // Implementation to retrieve payment by Stripe session ID
            return await _context.Payments
                .FirstOrDefaultAsync(p => p.StripeSessionId == sessionId);
        }
        public async Task<Payment?> GetPaymentByOrderIdAsync(Guid orderId)
        {
            if (orderId == Guid.Empty)
            {
                _logger.LogWarning("Attempted to retrieve payment with an empty Order ID.");
                throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));
            }

            try
            {
                _logger.LogInformation("Retrieving payment record for Order ID: {OrderId}", orderId);
                var payment = await _context.Payments.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.OrderId == orderId && p.Status == PaymentStatus.Pending);

                if (payment == null)
                {
                    _logger.LogWarning("No pending payment record found for Order ID {OrderId}.", orderId);
                }

                return payment;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving payment record for Order ID {OrderId}.", orderId);
                throw new RepositoryException($"Error retrieving payment record for Order ID {orderId}", ex);
            }
        }

        public async Task CreatePaymentAsync(Payment payment)
        {
            if (payment == null)
            {
                _logger.LogError("Attempted to create a null payment record.");
                throw new ArgumentNullException(nameof(payment));
            }

            try
            {
                _logger.LogInformation("Creating payment record with ID: {PaymentId}", payment.Id);
                await _context.Payments.AddAsync(payment);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Payment record with ID {PaymentId} created successfully.", payment.Id);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error creating payment record with ID {PaymentId}.", payment.Id);
                throw new RepositoryException($"Error creating payment record with ID {payment.Id}", ex);
            }
        }

        public async Task UpdatePaymentAsync(Payment payment)
        {
            if (payment == null)
            {
                _logger.LogError("Attempted to update a null payment record.");
                throw new ArgumentNullException(nameof(payment));
            }

            try
            {
                _logger.LogInformation("Updating payment record with ID: {PaymentId}", payment.Id);
                _context.Payments.Attach(payment);
                _context.Entry(payment).State = EntityState.Modified;
                await _context.SaveChangesAsync();
                _logger.LogInformation("Payment record with ID {PaymentId} updated successfully.", payment.Id);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(ex, "Concurrency error updating payment record with ID {PaymentId}.", payment.Id);
                throw new RepositoryException($"Error updating payment record with ID {payment.Id}. A concurrency issue occurred.", ex);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error updating payment record with ID {PaymentId}.", payment.Id);
                throw new RepositoryException($"Error updating payment record with ID {payment.Id}", ex);
            }
        }

        public async Task<IDbContextTransaction> BeginTransactionAsync()
        {
            try
            {
                _logger.LogInformation("Starting a new database transaction.");
                return await _context.Database.BeginTransactionAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting database transaction.");
                throw new RepositoryException("Error starting database transaction.", ex);
            }
        }
    }

    public class RepositoryException : Exception
    {
        public RepositoryException(string message, Exception innerException) : base(message, innerException) { }
    }
}