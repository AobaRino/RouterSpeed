namespace RouterSpeed;

public sealed record SpeedSnapshot(double DirectDown, double DirectUp, double ProxyDown, double ProxyUp,
    string Status, string Detail, bool Connected);
