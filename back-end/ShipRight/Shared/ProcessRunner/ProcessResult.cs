namespace ShipRight.Shared.ProcessRunner;

public record ProcessResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration)
{
    public bool Success => ExitCode == 0;
}
