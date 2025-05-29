namespace ecommerce.Db
{
    public enum OrderStatus
    {
        Pending = 0,
        Processing = 1,
        Completed = 2,
        Cancelled = 3,
        Refunded = 4,
        Failed = 5,
        RequiresReview = 6
    }
}

