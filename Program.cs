using System.Buffers;
using System.Diagnostics;
using System.Drawing.Text;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;
using System.Runtime.InteropServices;

// https://artprodcus3.artifacts.visualstudio.com/A6fcc92e5-73a7-4f88-8d13-d9045b45fb27/cbb18261-c48f-4abb-8651-8cdcb5474649/_apis/artifact/cGlwZWxpbmVhcnRpZmFjdDovL2RuY2VuZy1wdWJsaWMvcHJvamVjdElkL2NiYjE4MjYxLWM0OGYtNGFiYi04NjUxLThjZGNiNTQ3NDY0OS9idWlsZElkLzEyMzMxNzEvYXJ0aWZhY3ROYW1lL0J1aWxkX1dpbmRvd3NfRGVidWcrQXR0ZW1wdCsxK0xvZ3M1/content?format=file&subPath=%2FBuild.binlog
//var binlogFilePath = @"C:\Users\jaredpar\Downloads\Build (4).binlog";

// https://artprodcus3.artifacts.visualstudio.com/A6fcc92e5-73a7-4f88-8d13-d9045b45fb27/cbb18261-c48f-4abb-8651-8cdcb5474649/_apis/artifact/cGlwZWxpbmVhcnRpZmFjdDovL2RuY2VuZy1wdWJsaWMvcHJvamVjdElkL2NiYjE4MjYxLWM0OGYtNGFiYi04NjUxLThjZGNiNTQ3NDY0OS9idWlsZElkLzEyMzQ1MDYvYXJ0aWZhY3ROYW1lL0J1aWxkX1dpbmRvd3NfRGVidWcrQXR0ZW1wdCsxK0xvZ3M1/content?format=file&subPath=%2FBuild.binlog
var binlogFilePath = @"C:\Users\jaredpar\Downloads\Build (5).binlog";
var printViolations = false;
var printTree = false;
var printStalls = true;
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
            binlogFilePath = args[index];
            break;
    }

    index++;
}

Console.WriteLine($"Using binlog {binlogFilePath}");
var instanceMap = new Dictionary<int, ProjectInstance>();
var contextMap = new Dictionary<int, ProjectContext>();
var msbuildTasks = new List<MSBuildTask>();
var violations = new List<string>();
BuildMaps();

if (printTree)
{
    PrintTree();
}

if (printStalls)
{
    PrintStalls();
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
    var targetNameMap = new Dictionary<(int, int), string>();

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
                    var context = new ProjectContext(buildContext.ProjectContextId, instance, TargetNamesToArray(e.TargetNames), parentContextId, parentTaskId, e.Timestamp);
                    contextMap[buildContext.ProjectContextId] = context;
                }
                else
                {
                    Assert(false, $"Cannot get project instance id: eval id {buildContext.EvaluationId} instance id {buildContext.ProjectInstanceId}");
                }
                break;
            }
            case ProjectFinishedEventArgs e:
            {
                if (TryGetProjectContext(buildContext, e.ProjectFile) is { } context)
                {
                    Assert(context.Finished is null, $"Got two finished events for {context.ProjectFileName} context id {buildContext.ProjectContextId}");
                    context.Finished = e.Timestamp;

                    // If targets were requested and all of them were cached then this was a cached execution
                    // of a project.
                    context.IsCachedExecution = context.TargetNames.All(x => context.TargetsExecuted.TryGetValue(x, out var e) && e == TargetOutcome.Cached);
                }
                break;
            }
            case TargetStartedEventArgs e:
            {
                targetNameMap[(buildContext.ProjectContextId, buildContext.TargetId)] = e.TargetName;
                break;
            }
            case TargetFinishedEventArgs e:
            {
                if (TryGetProjectContext(buildContext, e.ProjectFile) is { } context)
                {
                    // The ContainsKey is to guard against a case where we get the skip event before finished. The skip
                    // event is more authorative.
                    if (!context.TargetsExecuted.ContainsKey(e.TargetName))
                    {
                        context.TargetsExecuted[e.TargetName] = TargetOutcome.Executed;
                    }
                }

                targetNameMap.Remove((buildContext.ProjectContextId, buildContext.TargetId));
                break;
            }
            case TargetSkippedEventArgs { SkipReason: TargetSkipReason.PreviouslyBuiltUnsuccessfully or TargetSkipReason.PreviouslyBuiltSuccessfully or TargetSkipReason.OutputsUpToDate } e:
            {
                if (TryGetProjectContext(buildContext, e.ProjectFile) is { } context)
                {
                    context.TargetsExecuted[e.TargetName] = TargetOutcome.Cached;
                }
                break;
            }
            case TaskStartedEventArgs { TaskName: "MSBuild" } e:
            {
                Assert(buildContext.TaskId != BuildEventContext.InvalidTaskId, "Invalid task id for msbuild");
                Assert(buildContext.ProjectContextId != BuildEventContext.InvalidProjectContextId, "Invalid context id for msbuild");
                var task = msbuildTaskMap[(buildContext.ProjectContextId, buildContext.TaskId)] = new MSBuildTask(buildContext.ProjectContextId, buildContext.TaskId, e.Timestamp);
                if (targetNameMap.TryGetValue((buildContext.ProjectContextId, buildContext.TargetId), out var name))
                {
                    task.ContainingTargetName = name;
                }

                msbuildTaskMap[(buildContext.ProjectContextId, buildContext.TaskId)] = task;
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

            if (context.Finished is { } finished)
            {
                context.ProjectInstance.Executions.Add(context.Started, context);
            }
        }
    }

    msbuildTasks.AddRange(msbuildTaskMap.Values);

    ProjectContext? TryGetProjectContext(BuildEventContext c, string? projectFilePath)
    {
        var name = projectFilePath is not null
            ? Path.GetFileName(projectFilePath)
            : "<unknown>";
        if (c.ProjectContextId == BuildEventContext.InvalidProjectContextId)
        {
            Assert(false, $"Invalid context id for {name}");
            return null;
        }

        if (!contextMap.TryGetValue(c.ProjectContextId, out var context))
        {
            Assert(false, $"Build event with context {c.ProjectContextId} for {name} before project started ");
            return null;
        }

        return context;
    }
}

void PrintStalls()
{
    var nodeMap = BuildNodeExecutionMap();
    var builder = new StringBuilder();

    foreach (var task in msbuildTasks.Where(x => x.ProjectContexts.All(x => x.IsCachedExecution)))
    {
        // Ignore default targets for now
        if (task.Targets.Length == 0)
        {
            continue;
        }

        if (task.Finished is not { } taskFinished)
        {
            continue;
        }
        if (!IsTaskWaiting(task, taskFinished))
        {
            continue;
        }

        var taskDuration = taskFinished - task.Started;
        if (taskDuration.TotalSeconds < 1)
        {
            continue;
        }

        var taskContext = contextMap[task.ProjectContextId];
        builder.Length = 0;

        builder.AppendLine($"MSBuild Task inside {taskContext.ProjectFileName} (task id {task.TaskId}) (node id {taskContext.ProjectInstance.NodeId})");
        builder.AppendLine($"\tContaining Target: {task.ContainingTargetName}");
        builder.AppendLine($"\tExecuted Targets: {TargetNamesToString(task.Targets)}");
        builder.AppendLine($"\tStall time: {(taskFinished - task.Started):mm\\:ss}");

        var anyRealStall = false;
        var stack = new Stack<ProjectContext>();
        foreach (var currentContext in task.ProjectContexts)
        {
            var instance = currentContext.ProjectInstance;
            Assert(task.Started < currentContext.Started, "MSBuild child task started before the msbuild task");
            var timeToChildContext = currentContext.Started - task.Started;
            if (timeToChildContext.TotalSeconds < 1)
            {
                continue;
            }

            stack.Clear();
            var nodeList = nodeMap[instance.NodeId];
            var index = nodeList.IndexOfKey(currentContext.Started) - 1;
            while (index > 0)
            {
                var temp = nodeList.GetValueAtIndex(index);
                index--;

                // Ignore project executions that occur on the same node as this is not a stall. The work is just
                // happening on this node
                if (temp.ProjectInstance.NodeId == taskContext.ProjectInstance.NodeId)
                {
                    continue;
                }

                if (temp.Finished is not { } tempFinished)
                {
                    continue;
                }

                if (tempFinished < task.Started)
                {
                    break;
                }

                stack.Push(temp);
            }

            if (IsFalseCacheExecution(task.Targets, currentContext.ProjectInstance, stack))
            {
                continue;
            }

            anyRealStall = true;
            builder.AppendLine($"\tProject: {currentContext.ProjectFileName} (node {instance.NodeId}) (context {currentContext.ParentContextId}) {timeToChildContext:mm\\:ss}");

            foreach (var previousContext in stack)
            {
                Debug.Assert(previousContext.Finished.HasValue);
                var start = task.Started > previousContext.Started ? task.Started : previousContext.Started;
                var previousDuration = previousContext.Finished.Value - start;
                builder.AppendLine($"\t\tExecuted: {previousContext.ProjectFileName} (context {previousContext.ProjectContextId})");
                builder.AppendLine($"\t\tTargets: {TargetNamesToString(previousContext.TargetNames)}");
                var durationStr = previousContext.IsCachedExecution ? "Cached Execution" : $"{previousDuration:mm\\:ss}";
                builder.AppendLine($"\t\tDuration: {durationStr}");
            }
        }

        if (anyRealStall)
        {
            Console.Write(builder.ToString());
        }

        // A false cache execution happens when the _final_ request is a cached execution of a target but 
        // between when the request started and the final request happened the target was acutally executed.
        // Hence yes this was a cache read but the cache wasn't actually available at the time the request
        // happened. We can detect this by seeing if any of the targets executed during the duration of
        // the requesting msbuild task
        static bool IsFalseCacheExecution(string[] targetNames, ProjectInstance project, IEnumerable<ProjectContext> contexts)
        {
            foreach (var context in contexts.Where(x => x.ProjectInstance.ProjectId ==project.ProjectId))
            {
                foreach (var targetName in targetNames)
                {
                    if (context.TargetsExecuted.TryGetValue(targetName, out var e) && e == TargetOutcome.Executed)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Determine if this task is just waiting. That is the node does nothing durith the time it's waiting for 
        // cached target requests to complete
        bool IsTaskWaiting(MSBuildTask task, DateTime taskFinished)
        {
            var context = contextMap[task.ProjectContextId];
            var nodeTimeline = nodeMap[context.ProjectInstance.NodeId];

            var index = nodeTimeline.IndexOfKey(context.Started) + 1;
            while (index < nodeTimeline.Count)
            {
                var current = nodeTimeline.GetValueAtIndex(index);
                if (current.Started > task.Started && current.Started < taskFinished)
                {
                    return false;
                }

                if (current.Started > taskFinished)
                {
                    return true;
                }

                index++;
            }

            return false;
        }
    }
}

int? GetProjectInstanceId(BuildEventContext context) => (context.EvaluationId, context.ProjectInstanceId) switch
{
    (BuildEventContext.InvalidEvaluationId, BuildEventContext.InvalidProjectInstanceId) => null,
    (BuildEventContext.InvalidEvaluationId, var id) => id,
    (var id, BuildEventContext.InvalidProjectInstanceId) => id,
    (var evalId, _) => evalId,
};

// Build a map of node id to sorted list of project executions based on time on that node 
Dictionary<int, SortedList<DateTime, ProjectContext>> BuildNodeExecutionMap()
{
    var nodeMap = new Dictionary<int, SortedList<DateTime, ProjectContext>>();
    foreach (var context in contextMap.Values)
    {
        if (context.Finished is not { } finished)
        {
            continue;
        }

        var nodeId = context.ProjectInstance.NodeId;
        if (!nodeMap.TryGetValue(nodeId, out var list))
        {
            list = new();
            nodeMap[nodeId] = list;
        }
        list.Add(context.Started, context);
    }

    return nodeMap;
}

void PrintTree()
{
    foreach (var context in contextMap.Values.Where(c => c.Parent is null))
    {
        PrintContext(context, 0);
    }

    void PrintContext(ProjectContext context, int indentLength)
    {
        var indent = new string(' ', indentLength);
        Console.WriteLine($"{indent}Project: {context.ProjectFileName} Target Names: {TargetNamesToString(context.TargetNames)}");
        foreach (var c in contextMap.Values.Where(c => c.Parent == context))
        {
            PrintContext(c, indentLength + 2);
        }
    }
}

void Assert(bool condition, string message)
{
    if (!condition)
    {
        violations.Add(message);
    }
}

// This is a convenient hash function to create a key from a set of properties. It's 
// used to quickly compare if two sets of properties are the same. Helpful in
// telling if project instances are equivalent.
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

static string TargetNamesToString(string[] targetNames) =>
    string.Join(';', targetNames);

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
    internal string? ContainingTargetName { get; set; }
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
    internal SortedList<DateTime, ProjectContext> Executions { get; } = new();
    public override string ToString() => $"{ProjectFileName} {Key}";
};

internal sealed class ProjectContext(int projectContextId, ProjectInstance projectInstance, string[] targetNames, int? parentContextId, int? parentTaskId, DateTime started)
{
    internal int ProjectContextId { get; } = projectContextId;
    internal ProjectInstance ProjectInstance { get; } = projectInstance;
    internal string[] TargetNames { get; } = targetNames;
    internal int? ParentContextId { get; } = parentContextId;
    internal int? ParentTaskId { get; } = parentTaskId;
    internal ProjectContext? Parent { get; set; }
    internal DateTime Started { get; } = started;
    internal DateTime? Finished { get; set; }

    /// <summary>
    /// This is true when the project context was a pure cache hit. Nothing actually happened. The execution
    /// just queried the results of previously executed targets.
    /// </summary>
    internal bool IsCachedExecution { get; set; }
    internal string ProjectFileName => ProjectInstance.ProjectFileName;
    internal Dictionary<string, TargetOutcome> TargetsExecuted { get; } = new();
    public override string ToString() => $"{ProjectInstance.ProjectFileName} Targets: {TargetNames}";
}

internal enum TargetOutcome
{
    Cached,
    Executed
}


