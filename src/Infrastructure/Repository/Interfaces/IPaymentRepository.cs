using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ecommerce.Db;
using Microsoft.EntityFrameworkCore.Storage;

namespace ecommerce.Infrastructure.Repository.Interfaces
{
    public interface IPaymentRepository
    {
        Task<Payment?> GetPaymentByIdAsync(Guid paymentId);
        Task<Payment?> GetPaymentByOrderIdAsync(Guid orderId);
        Task<Payment> GetPaymentByStripeSessionIdAsync(string sessionId);
        Task CreatePaymentAsync(Payment payment);
        Task UpdatePaymentAsync(Payment payment);
        Task<IDbContextTransaction> BeginTransactionAsync();
    }
}