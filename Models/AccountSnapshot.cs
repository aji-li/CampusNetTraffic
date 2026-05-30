namespace CampusNetTraffic.Models;

public sealed record AccountSnapshot(
    double UsedTrafficMb,
    double AvailableTrafficMb,
    decimal Balance,
    string Status,
    string Plan,
    string BillingMode,
    string BillingPeriod,
    DateTimeOffset CapturedAt);
