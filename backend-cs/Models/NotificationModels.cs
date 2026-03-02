namespace DriveChill.Models;

public sealed class PushSubscriptionRecord
{
    public string  Id          { get; set; } = "";
    public string  Endpoint    { get; set; } = "";
    public string  P256dh      { get; set; } = "";
    public string  AuthKey     { get; set; } = "";
    public string? UserAgent   { get; set; }
    public string  CreatedAt   { get; set; } = "";
    public string? LastUsedAt  { get; set; }
}

public sealed class EmailNotificationSettingsRecord
{
    public bool   Enabled        { get; set; }
    public string SmtpHost       { get; set; } = "";
    public int    SmtpPort       { get; set; } = 587;
    public string SmtpUsername   { get; set; } = "";
    public string SmtpPassword   { get; set; } = "";
    public string SenderAddress  { get; set; } = "";
    public string RecipientList  { get; set; } = "[]";
    public bool   UseTls         { get; set; } = true;
    public bool   UseSsl         { get; set; }
    public string UpdatedAt      { get; set; } = "";
}
