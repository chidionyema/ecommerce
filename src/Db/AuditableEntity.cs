using System;
using System.ComponentModel.DataAnnotations;

namespace ecommerce.Db
{
    public interface IEntityWithGuid
    {
        Guid Id { get; set; }
    }

    public abstract class AuditableEntity : IEntityWithGuid
    {
        protected AuditableEntity()
        {
            Id = Guid.NewGuid();
            CreatedAt = DateTime.UtcNow;
            RowVersion = new byte[8];
        }

        protected AuditableEntity(Guid id)
        {
            Id = id;
            CreatedAt = DateTime.UtcNow;
            RowVersion = new byte[8];
        }
            


        public Guid Id { get; set; }

        public string? CreatedFromIp { get; set; } = null!;
        public string? ModifiedFromIp { get; set; }
        public byte[] RowVersion { get; set; } = null!;
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? LastModifiedBy { get; set; }
        public DateTime? LastModifiedDate { get; set; }
    }

    public class DatabaseException : Exception
    {
        public DatabaseException(string message, Exception inner) : base(message, inner) { }
    }

    public class DataUpdateException : Exception
    {
        public DataUpdateException(string message, Exception inner)
            : base(message, inner) { }
    }

    public class ConcurrencyException : DatabaseException
    {
        public ConcurrencyException(Exception inner) 
            : base("Optimistic concurrency conflict occurred", inner) { }

        public ConcurrencyException(string message, Exception inner)
            : base(message, inner) { }
    
    }

    public class UniqueViolationException : DatabaseException
    {
        public UniqueViolationException(Exception inner) 
            : base("Unique constraint violation", inner) { }
    }
}
