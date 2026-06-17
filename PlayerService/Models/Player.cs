namespace PlayerService.Models;

public class Player
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public int Balance { get; set; }
    public bool IsAdmin { get; set; } = false;
    public bool IsBlocked { get; set; } = false;
    
    public List<PlayerItem> Inventory { get; set; } = new();
    public List<ApiKeyInfo> ApiKeys { get; set; } = new();
}

public class PlayerItem
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class ApiKeyInfo
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int CallsCount { get; set; }
    public bool IsActive { get; set; } = true;
    
    [System.Text.Json.Serialization.JsonIgnore]
    public Player? Player { get; set; }
}