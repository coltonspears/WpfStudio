namespace WpfStudio.Inspection.Tests;

/// <summary>
/// Tests that change this process's environment variables. An application another test launched meanwhile would inherit
/// them, so these run on their own after the other classes, which launch their own applications with their own folders and pipes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}
