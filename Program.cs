using System.Buffers;
using System.Diagnostics;
using System.Drawing.Text;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;

// https://artprodcus3.artifacts.visualstudio.com/A6fcc92e5-73a7-4f88-8d13-d9045b45fb27/cbb18261-c48f-4abb-8651-8cdcb5474649/_apis/artifact/cGlwZWxpbmVhcnRpZmFjdDovL2RuY2VuZy1wdWJsaWMvcHJvamVjdElkL2NiYjE4MjYxLWM0OGYtNGFiYi04NjUxLThjZGNiNTQ3NDY0OS9idWlsZElkLzEyMzMxNzEvYXJ0aWZhY3ROYW1lL0J1aWxkX1dpbmRvd3NfRGVidWcrQXR0ZW1wdCsxK0xvZ3M1/content?format=file&subPath=%2FBuild.binlog
var binlogFilePath = @"C:\Users\jaredpar\Downloads\Build (1).binlog";
using var stream = File.OpenRead(binlogFilePath);
var records = BinaryLog.ReadRecords(stream);
var instanceMap = new Dictionary<int, ProjectInstance>();
var contextMap = new Dictionary<int, ProjectContext>();

foreach (var record in records)
{
    if (record.Args is ProjectStartedEventArgs { BuildEventContext: {} buildContext } e)
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
                //Assert(buildContext.EvaluationId != BuildEventContext.InvalidEvaluationId, $"The initial evaluation for {Path.GetFileName(e.ProjectFile)} is invalid");
                instance = new ProjectInstance(e.ProjectFile ?? "<unknown>", instanceId, buildContext.NodeId, buildContext.EvaluationId, key);
            }

            int? parentContextId = null;
            if (e.ParentProjectBuildEventContext is { ProjectContextId: not BuildEventContext.InvalidProjectContextId })
            {
                parentContextId = e.ParentProjectBuildEventContext.ProjectContextId;
            }

            Assert(!contextMap.ContainsKey(buildContext.ProjectContextId), "Project context already exists");
            contextMap[buildContext.ProjectContextId] = new ProjectContext(instance, e.TargetNames, parentContextId);
        }
        else
        {
            Assert(false, $"Cannot get project instance id: eval id {buildContext.EvaluationId} instance id {buildContext.ProjectInstanceId}");
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
    }
}

foreach (var context in contextMap.Values.Where(c => c.Parent is null))
{
    PrintContext(context, 0);
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

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        Console.WriteLine("WARNING: " + message);
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

internal sealed class ProjectContext(ProjectInstance projectInstance, string? targetNames, int? parentContextId)
{
    internal ProjectInstance ProjectInstance { get; } = projectInstance;
    internal string? TargetNames { get; } = targetNames;
    internal int? ParentContextId { get; } = parentContextId;
    internal ProjectContext? Parent { get; set; }
    internal string ProjectFileName => ProjectInstance.ProjectFileName;
    public override string ToString() => $"{ProjectInstance.ProjectFileName} Targets: {TargetNames}";
}

