using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources;

public static class PipelineExecutor
{
    public static List<string> ValidateSteps(List<PipelineStep> steps)
    {
        var errors = new List<string>();
        var seenBuild = false;
        var seenPush = false;
        var seenDeploy = false;
        var buildIdx = -1;
        var pushIdx = -1;
        var deployIdx = -1;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            switch (step.Type)
            {
                case PipelineStepType.Build:
                    if (seenBuild)
                    {
                        errors.Add("Build step appears multiple times. Only one Build step is allowed.");
                        continue;
                    }
                    seenBuild = true;
                    buildIdx = i;
                    break;

                case PipelineStepType.Push:
                    if (seenPush)
                    {
                        errors.Add("Push step appears multiple times. Only one Push step is allowed.");
                        continue;
                    }
                    seenPush = true;
                    pushIdx = i;
                    break;

                case PipelineStepType.Deploy:
                    if (seenDeploy)
                    {
                        errors.Add("Deploy step appears multiple times. Only one Deploy step is allowed.");
                        continue;
                    }
                    seenDeploy = true;
                    deployIdx = i;
                    break;

                case PipelineStepType.Script:
                    if (step.ScriptResourceId is null)
                    {
                        errors.Add($"Script step '{step.Label ?? "unnamed"}' is missing ScriptResourceId.");
                    }
                    break;
            }
        }

        if (seenPush && seenBuild && pushIdx < buildIdx)
            errors.Add("Push step must come after Build step.");

        if (seenDeploy && seenPush && deployIdx < pushIdx)
            errors.Add("Deploy step must come after Push step.");

        if (seenDeploy && seenBuild && !seenPush && deployIdx < buildIdx)
            errors.Add("Deploy step must come after Build step.");

        return errors;
    }

    public static PipelineStepGroups GroupStepsByPosition(List<PipelineStep> steps)
    {
        var groups = new PipelineStepGroups();
        var buildIdx = steps.FindIndex(s => s.Type == PipelineStepType.Build);
        var pushIdx = steps.FindIndex(s => s.Type == PipelineStepType.Push);
        var deployIdx = steps.FindIndex(s => s.Type == PipelineStepType.Deploy);

        foreach (var step in steps)
        {
            if (step.Type != PipelineStepType.Script) continue;

            var stepIdx = steps.IndexOf(step);

            if (buildIdx >= 0 && stepIdx < buildIdx)
            {
                groups.PreBuild.Add(step);
            }
            else if (pushIdx >= 0 && stepIdx < pushIdx)
            {
                groups.PrePush.Add(step);
            }
            else if (deployIdx >= 0 && stepIdx < deployIdx)
            {
                groups.PreDeploy.Add(step);
            }
            else if (buildIdx < 0 && pushIdx < 0 && deployIdx < 0)
            {
                groups.PreBuild.Add(step);
            }
            else
            {
                groups.PostDeploy.Add(step);
            }
        }

        return groups;
    }
}

public class PipelineStepGroups
{
    public List<PipelineStep> PreBuild { get; } = [];
    public List<PipelineStep> PrePush { get; } = [];
    public List<PipelineStep> PreDeploy { get; } = [];
    public List<PipelineStep> PostDeploy { get; } = [];
}
