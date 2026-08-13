namespace FlowStock.Updater;

public sealed class UpdaterProcessOutcome
{
    public const int RecoveryFailureExitCode = 3;

    private readonly bool _recovery;

    public UpdaterProcessOutcome(bool recovery)
    {
        _recovery = recovery;
        ExitCode = recovery ? RecoveryFailureExitCode : 0;
    }

    public int ExitCode { get; private set; }

    public void MarkSuccess()
    {
        if (_recovery)
        {
            ExitCode = 0;
        }
    }

    public void MarkFailure()
    {
        if (_recovery)
        {
            ExitCode = RecoveryFailureExitCode;
        }
    }
}
