namespace LordHelm.Providers;

public sealed record VendorHealth(
    string VendorId,
    int InFlight,
    int WindowLimit,
    TimeSpan Window,
    bool IsHealthy,
    string? LastError = null);

/// <summary>
/// Live rolling-window usage + health per vendor. Surfaces on the dashboard
/// summary ribbon and on the /providers page so operators can see the cost /
/// rate-limit state of every provider at a glance (Cummings supervisory-control
/// principle: real-time economic feedback is a prerequisite for rational tier
/// switching).
/// </summary>
public interface IProviderHealth
{
    IReadOnlyList<VendorHealth> GetAll();
    VendorHealth? Get(string vendorId);
}
