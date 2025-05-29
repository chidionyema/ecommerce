 
  using System;
  using System.Collections.Generic;
  namespace ecommerce.Dto
{
      public class GuestInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Zip { get; set; } = string.Empty;
    }

      public class UserInfo
    {
        public string UserId { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsGuest { get; set; } = true;
    }
 }
