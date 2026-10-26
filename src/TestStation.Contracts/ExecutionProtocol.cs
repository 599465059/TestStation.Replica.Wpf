namespace TestStation.Contracts;

/// <summary>Versioned JSON-lines protocol shared by WPF and the OpenTAP Host.</summary>
public static class ExecutionProtocol
{
    public const int Version = 1;
    public const string Run = "run";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Stop = "stop";
    public const string Completed = "run_completed";
    public const string Finished = "run_finished";
    public const string Error = "error";
}
