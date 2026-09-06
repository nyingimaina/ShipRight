namespace ShipRight.Shared.CommandExecution;

public interface IExecutionTargetProvider
{
    ExecutionTarget GetTarget();
}

/// <summary>
/// Resolves the execution target from configuration. When no remote build host
/// is configured, commands always run on the local machine.
/// Env vars: SHIPRIGHT__BUILD__SSH_HOST, SHIPRIGHT__BUILD__SSH_USER,
/// SHIPRIGHT__BUILD__SSH_KEY_PATH.
/// </summary>
public sealed class ExecutionTargetProvider : IExecutionTargetProvider
{
    private readonly Func<string, string?> _getEnv;

    public ExecutionTargetProvider(Func<string, string?>? getEnv = null)
    {
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
    }

    public ExecutionTarget GetTarget()
    {
        var host = _getEnv("SHIPRIGHT__BUILD__SSH_HOST");
        if (string.IsNullOrWhiteSpace(host))
            return ExecutionTarget.Local;

        var user = _getEnv("SHIPRIGHT__BUILD__SSH_USER");
        var key = _getEnv("SHIPRIGHT__BUILD__SSH_KEY_PATH");
        return new ExecutionTarget("ssh", host, string.IsNullOrWhiteSpace(user) ? "root" : user, key ?? string.Empty);
    }
}