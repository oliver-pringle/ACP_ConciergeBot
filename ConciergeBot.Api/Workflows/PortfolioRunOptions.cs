namespace ConciergeBot.Api.Workflows;

public sealed class PortfolioRunOptions
{
    public string RevokeBotBaseUrl { get; set; } = "http://revokebot-api:5000";
    public string OracleBotBaseUrl { get; set; } = "http://oraclebot-api:5000";
    public string SecurityBotBaseUrl { get; set; } = "http://securitybot-api:5000";
    public int TimeoutSeconds { get; set; } = 15;
}
