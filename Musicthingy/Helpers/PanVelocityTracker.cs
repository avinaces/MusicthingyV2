using System;

namespace Musicthingy.Helpers;

public class PanVelocityTracker
{
    private double _lastPanX;
    private double _lastPanY;
    private DateTime _lastPanTime;

    public double VelocityX { get; private set; }
    public double VelocityY { get; private set; }

    public void Start()
    {
        VelocityX = 0;
        VelocityY = 0;
        _lastPanX = 0;
        _lastPanY = 0;
        _lastPanTime = DateTime.UtcNow;
    }

    public void Update(double totalX, double totalY)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastPanTime).TotalMilliseconds;
        if (dt > 0)
        {
            double cvX = (totalX - _lastPanX) / dt;
            double cvY = (totalY - _lastPanY) / dt;

            // Exponential moving average for smooth velocity
            VelocityX = (VelocityX == 0) ? cvX : (VelocityX * 0.2 + cvX * 0.8);
            VelocityY = (VelocityY == 0) ? cvY : (VelocityY * 0.2 + cvY * 0.8);
        }
        _lastPanX = totalX;
        _lastPanY = totalY;
        _lastPanTime = now;
    }
}