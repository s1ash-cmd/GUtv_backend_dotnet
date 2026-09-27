namespace GUtv_backend_dotnet.Models;

public class Announcement
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    [GraphQLIgnore] public int AuthorId { get; set; }
    [GraphQLIgnore] public Guid RequestId { get; set; }
}

// Durable delivery queue. Chat IDs and delivery errors are never exposed in the feed.
public class AnnouncementDelivery
{
    public int AnnouncementId { get; set; }
    public Announcement Announcement { get; set; } = null!;
    public long ChatId { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    public bool Failed { get; set; }
    public string? LastError { get; set; }
}
