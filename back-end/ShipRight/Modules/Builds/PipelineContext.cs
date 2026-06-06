using ShipRight.Shared.Events;

namespace ShipRight.Modules.Builds;

public class PipelineContext
{
    private readonly BuildEventBus _bus;
    private DateTime _stepStartedAt;
    public BuildRecord Record { get; }

    public PipelineContext(BuildRecord record, BuildEventBus bus)
    {
        Record = record;
        _bus = bus;
    }

    public async Task EmitLogAsync(string line, string source = "shipright")
    {
        Record.LogOutput += line + "\n";
        await _bus.EmitAsync(Record.Id, "LogLine", new
        {
            buildId = Record.Id, source, line, timestamp = DateTime.UtcNow
        });
    }

    public async Task StepStartedAsync(int stepNumber, string stepName)
    {
        _stepStartedAt = DateTime.UtcNow;
        Record.CurrentStepNumber = stepNumber;
        Record.CurrentStepName = stepName;
        await _bus.EmitAsync(Record.Id, "StepStarted", new
        {
            buildId = Record.Id, stepNumber, stepName
        });
        await EmitLogAsync($"[Step {stepNumber}] {stepName} started");
    }

    public async Task StepCompletedAsync(int stepNumber, string stepName, bool success = true)
    {
        Record.StepDurations[stepName] = (int)(DateTime.UtcNow - _stepStartedAt).TotalSeconds;
        if (success) Record.SucceededSteps.Add(stepName);
        await _bus.EmitAsync(Record.Id, "StepCompleted", new
        {
            buildId = Record.Id, stepNumber, stepName, success
        });
    }

    public async Task PauseAsync(string reason, string prompt, string[] options, object? fields = null)
    {
        Record.Status = BuildStatus.Paused;
        await _bus.EmitAsync(Record.Id, "PauseRequested", new
        {
            buildId = Record.Id, reason, prompt, options, fields
        });
    }

    public async Task BuildCompletedAsync()
    {
        await _bus.EmitAsync(Record.Id, "BuildCompleted", new
        {
            buildId = Record.Id,
            status = Record.Status.ToString(),
            gitTag = Record.GitTag
        });
        _bus.Complete(Record.Id);
    }

    public async Task PushCompletedAsync()
    {
        await _bus.EmitAsync(Record.Id, "PushCompleted", new
        {
            buildId = Record.Id, status = Record.Status.ToString()
        });
        _bus.Complete(Record.Id);
    }

    public async Task DeployCompletedAsync()
    {
        await _bus.EmitAsync(Record.Id, "DeployCompleted", new
        {
            buildId = Record.Id, status = Record.Status.ToString()
        });
        _bus.Complete(Record.Id);
    }
}
