namespace BookmarkManager.Api.Data;

public static class AdminAccountConstants
{
    public const int SingletonId = 1;
}

public class AdminAccount
{
    public int Id { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
