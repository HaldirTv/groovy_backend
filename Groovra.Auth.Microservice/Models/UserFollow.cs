using System;

namespace Groovra.Auth.Microservice.Models
{
    public class UserFollow
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid FollowerId { get; set; }
        public User Follower { get; set; } = null!;
        public Guid FollowedId { get; set; }
        public User Followed { get; set; } = null!;
        public DateTime FollowedAt { get; set; } = DateTime.UtcNow;
    }
}
