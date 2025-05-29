using System;
using System.Collections.Generic;

namespace ecommerce.Db
{
    public class Category : AuditableEntity
    {
        // Constructor to be used by Entity Framework for seeding data
        public Category(Guid id, string name) : base(id) 
        {
            Name = name;
        }

        // Alternative constructor for other purposes
        public Category() : base()
        {
        }

        public Category(Guid id, ICollection<Product> products) : base(id) 
        {
            Products = products;
        }

        public Category(Guid id) : base(id) 
        {
        }

        public ICollection<Product>? Products { get; private set; } = new List<Product>();
        public string Name { get; set; } = string.Empty;
    }
}
