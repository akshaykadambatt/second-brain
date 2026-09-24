namespace SecondBrain.Core;

// Visual motion only: recognition owns the selected word. Keep a bounded, monotonic
// glide toward its line rather than turning transcript bursts into pixel jumps.
public sealed class ReaderMotion
{
    private double velocity;
    public double Position { get; private set; }
    public double Target { get; private set; }
    public bool Moving => Target - Position > .01;

    public void Reset(double offset)
    {
        Position = Target = Math.Max(0, offset);
        velocity = 0;
    }

    public void Follow(double offset)
    {
        // Manual selection and layout changes use Reset. Voice must never pull back.
        Target = Math.Max(Target, offset);
    }
    public double Step(double elapsedSeconds, double lineHeight)
    {
        if (!Moving || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return Position;
        // Discard stall time. Small integration steps keep the response consistent
        // at different refresh rates without trying to catch up a blocked UI thread.
        var remainingTime = Math.Min(elapsedSeconds, 1d / 30);
        var maxSpeed = Math.Max(1, lineHeight) * 2.2;
        var frameLimit = Position + maxSpeed * remainingTime;
        const double settleTime = .38;
        const double omega = 2 / settleTime;
        while (remainingTime > 0)
        {
            var dt = Math.Min(remainingTime, 1d / 240);
            remainingTime -= dt;
            var change = Math.Max(Position - Target, -maxSpeed * settleTime);
            var localTarget = Position - change;
            var temporary = (velocity + omega * change) * dt;
            var decay = Math.Exp(-omega * dt);
            velocity = Math.Clamp((velocity - omega * temporary) * decay, 0, maxSpeed);
            var next = localTarget + (change + temporary) * decay;
            Position = Math.Clamp(next, Position, Math.Min(Target, Position + maxSpeed * dt));
        }
        // Finish the imperceptible tail exactly, without spending more than this frame's motion budget.
        if (Target - Position <= .05 && Target <= frameLimit) { Position = Target; velocity = 0; }
        return Position;
    }
}
