namespace Netfox.Core.Time;

/// <summary>One NTP-style ping/pong measurement. Port of network-clock-sample.gd.</summary>
public sealed class ClockSample
{
    public double PingSent { get; set; }
    public double PingReceived { get; set; }
    public double PongSent { get; set; }
    public double PongReceived { get; set; }

    public double Rtt => PongReceived - PingSent;

    /// <summary>See RFC 5905 section 8: theta = ((t2 - t1) + (t3 - t4)) / 2.</summary>
    public double Offset => ((PingReceived - PingSent) + (PongSent - PongReceived)) / 2.0;

    public override string ToString()
        => $"(theta={Offset * 1000.0:F2}ms; delta={Rtt * 1000.0:F2}ms; t1={PingSent:F4}s; t2={PingReceived:F4}s; t3={PongSent:F4}s; t4={PongReceived:F4}s)";
}
