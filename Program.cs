using System.Buffers;
using System.Diagnostics;
using System.Drawing.Text;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;

var printViolations = false;
var printTree = false;
var printStalls = false;
var index = 0;
while (index < args.Length)
{
    switch (args[index])
    {
        case "v":
            printViolations = true;
            break;
        case "t":
            printTree = true;
            break;
        case "s":
            printStalls = true;
            break;
        default:
            Console.WriteLine($"Bad option: {args[index]}");
            return 1;
    }

    index++;
}

// https://artprodcus3.artifacts.visualstudio.com/A6fcc92e5-73a7-4f88-8d13-d9045b45fb27/cbb18261-c48f-4abb-8651-8cdcb5474649/_apis/artifact/cGlwZWxpbmVhcnRpZmFjdDovL2RuY2VuZy1wdWJsaWMvcHJvamVjdElkL2NiYjE4MjYxLWM0OGYtNGFiYi04NjUxLThjZGNiNTQ3NDY0OS9idWlsZElkLzEyMzMxNzEvYXJ0aWZhY3ROYW1lL0J1aWxkX1dpbmRvd3NfRGVidWcrQXR0ZW1wdCsxK0xvZ3M1/content?format=file&subPath=%2FBuild.binlog
var binlogFilePath = @"C:\Users\jaredpar\Downloads\Build (1).binlog";

var instanceMap = new Dictionary<int, ProjectInstance>();
var contextMap = new Dictionary<int, ProjectContext>();
var msbuildTasks = new List<MSBuildTask>();
var violations = new List<string>();
BuildMaps();

if (printTree)
{
    foreach (var context in contextMap.Values.Where(c => c.Parent is null))
    {
        PrintContext(context, 0);
    }
}

if (printViolations)
{
    foreach (var violation in violations)
    {
        Console.WriteLine($"WARNING: {violation}");
    }
}

return 0;

void BuildMaps()
{
    using var stream = File.OpenRead(binlogFilePath);
    var records = BinaryLog.ReadRecords(stream);
    var msbuildTaskMap = new Dictionary<(int, int), MSBuildTask>();
    foreach (var record in records)
    {
        if (record.Args is not { BuildEventContext: { } buildContext })
        {
            continue;
        }

        switch (record.Args)
        {
            case ProjectStartedEventArgs e:
            {
                var key = GetKey(e.GlobalProperties);
                if (GetProjectInstanceId(buildContext) is { } instanceId)
                {
                    if (instanceMap.TryGetValue(instanceId, out var instance))
                    {
                        Assert(instance.NodeId == buildContext.NodeId, $"The node id for {instance.ProjectFileName} changed from {instance.NodeId} to {buildContext.NodeId}");
                        Assert(buildContext.EvaluationId == BuildEventContext.InvalidEvaluationId || buildContext.EvaluationId == instance.EvaluationId, $"The eval id changed for {instance.ProjectFileName}");
                    }
                    else
                    {
                        Assert(!string.IsNullOrEmpty(e.ProjectFile), $"The project file is null for eval id {buildContext.EvaluationId}");

                        // This assert trips for pretty much every project
                        Assert(buildContext.EvaluationId != BuildEventContext.InvalidEvaluationId, $"The initial evaluation for {Path.GetFileName(e.ProjectFile)} is invalid");
                        instance = new ProjectInstance(e.ProjectFile ?? "<unknown>", instanceId, buildContext.NodeId, buildContext.EvaluationId, key);
                    }

                    int? parentContextId = null;
                    int? parentTaskId = null;
                    if (e.ParentProjectBuildEventContext is { ProjectContextId: not BuildEventContext.InvalidProjectContextId })
                    {
                        parentContextId = e.ParentProjectBuildEventContext.ProjectContextId;
                        parentTaskId = e.ParentProjectBuildEventContext.TaskId;
                    }

                    Assert(!contextMap.ContainsKey(buildContext.ProjectContextId), "Project context already exists");
                    contextMap[buildContext.ProjectContextId] = new ProjectContext(instance, TargetNamesToArray(e.TargetNames), parentContextId, parentTaskId, e.Timestamp);
                }
                else
                {
                    Assert(false, $"Cannot get project instance id: eval id {buildContext.EvaluationId} instance id {buildContext.ProjectInstanceId}");
                }
                break;
            }
            case ProjectFinishedEventArgs e:
            {
                if (buildContext.ProjectContextId is BuildEventContext.InvalidProjectContextId)
                {
                    Assert(false, $"Invalid context id for finished in {Path.GetFileName(e.ProjectFile)}");
                    continue;
                }

                if (!contextMap.TryGetValue(buildContext.ProjectContextId, out var context))
                {
                    Assert(false, $"Finished without a start for {Path.GetFileName(e.ProjectFile)}");
                    continue;
                }

                Assert(context.Finished is null, $"Got two finished events for {context.ProjectFileName} context id {buildContext.ProjectContextId}");
                context.Finished = e.Timestamp;
                break;
            }
            case TaskStartedEventArgs { TaskName: "MSBuild" } e:
            {
                Assert(buildContext.TaskId != BuildEventContext.InvalidTaskId, "Invalid task id for msbuild");
                Assert(buildContext.ProjectContextId != BuildEventContext.InvalidProjectContextId, "Invalid context id for msbuild");
                msbuildTaskMap[(buildContext.ProjectContextId, buildContext.TaskId)] = new MSBuildTask(buildContext.ProjectContextId, buildContext.TaskId, e.Timestamp);
                break;
            }
            case TaskParameterEventArgs { Kind: TaskParameterMessageKind.TaskInput, ItemType: "Targets" } e:
            {
                if (msbuildTaskMap.TryGetValue((buildContext.ProjectContextId, buildContext.TaskId), out var task))
                {
                    task.Targets = e.Items
                        .OfType<ITaskItem>()
                        .Select(x => x.ItemSpec)
                        .ToArray();
                }
                break;
            }
            case TaskFinishedEventArgs { TaskName: "MSBuild" } e:
            {
                if (msbuildTaskMap.TryGetValue((buildContext.ProjectContextId, buildContext.TaskId), out var task))
                {
                    task.Finished = e.Timestamp;
                }
                break;
            }
        }
    }

    foreach (var context in contextMap.Values)
    {
        if (context.ParentContextId is { } parentContextId)
        {
            if (!contextMap.TryGetValue(parentContextId, out var parentContext))
            {
                Assert(false, "Cannot find parent context");
                continue;
            }
            context.Parent = parentContext;

            if (context.ParentTaskId is {  } parentTaskId && msbuildTaskMap.TryGetValue((parentContextId, parentTaskId), out var task))
            {
                task.ProjectContexts.Add(context);
            }
        }
    }

    msbuildTasks.AddRange(msbuildTaskMap.Values);
}

void PrintStalls()
{

}

int? GetProjectInstanceId(BuildEventContext context) => (context.EvaluationId, context.ProjectInstanceId) switch
{
    (BuildEventContext.InvalidEvaluationId, BuildEventContext.InvalidProjectInstanceId) => null,
    (BuildEventContext.InvalidEvaluationId, var id) => id,
    (var id, BuildEventContext.InvalidProjectInstanceId) => id,
    (var evalId, _) => evalId,
};

void PrintContext(ProjectContext context, int indentLength)
{
    var indent = new string(' ', indentLength);
    Console.WriteLine($"{indent}Project: {context.ProjectFileName} Target Names: {context.TargetNames}");
    foreach (var c in contextMap.Values.Where(c => c.Parent == context))
    {
        PrintContext(c, indentLength + 2);
    }
}

void Assert(bool condition, string message)
{
    if (!condition)
    {
        violations.Add(message);
    }
}

static string GetKey(IDictionary<string, string>? properties)
{
    if (properties is null)
    {
        return "";
    }

    var builder = new StringBuilder();
    foreach (var kvp in properties)
    {
        builder.AppendLine($"{kvp.Key}={kvp.Value}");
    }

    // SHA256 hash the string in the builder
    var hash = SHA256.Create();
    var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
    return Convert.ToHexString(bytes);
}

static string[] TargetNamesToArray(string? targetNames)
{
    if (targetNames is null)
    {
        return [];
    }

    return targetNames.Split(';', StringSplitOptions.RemoveEmptyEntries);
}

internal sealed class MSBuildTask(int projectContextId, int taskId, DateTime started)
{
    internal int ProjectContextId { get; } = projectContextId;
    internal int TaskId { get; } = taskId;
    internal string[] Targets { get; set; } = [];
    internal DateTime Started { get; } = started;
    internal DateTime? Finished { get; set; }
    internal List<ProjectContext> ProjectContexts { get; } = new();
}

internal sealed class ProjectInstance(string projectFilePath, int projectInstanceId, int nodeId, int evaluationId, string key)
{
    internal string ProjectFilePath { get; } = projectFilePath;
    internal string ProjectFileName { get; } = Path.GetFileName(projectFilePath);
    internal int ProjectId { get; } = projectInstanceId;
    internal int NodeId { get; } = nodeId;
    internal int EvaluationId { get; } = evaluationId;
    internal string Key { get; } = key;
    public override string ToString() => $"{ProjectFileName} {Key}";
};

internal sealed class ProjectContext(ProjectInstance projectInstance, string[] targetNames, int? parentContextId, int? parentTaskId, DateTime started)
{
    internal ProjectInstance ProjectInstance { get; } = projectInstance;
    internal string[] TargetNames { get; } = targetNames;
    internal int? ParentContextId { get; } = parentContextId;
    internal int? ParentTaskId { get; } = parentTaskId;
    internal ProjectContext? Parent { get; set; }
    internal DateTime Started { get; } = started;
    internal DateTime? Finished { get; set; }
    internal string ProjectFileName => ProjectInstance.ProjectFileName;
    public override string ToString() => $"{ProjectInstance.ProjectFileName} Targets: {TargetNames}";
}

