using System.Text.Json;

namespace TestStation.Application;

public sealed class RunResultRepository(string projectRoot)
{
    private const string Format = "test-station-sequence-run";
    private readonly string _root = Path.Combine(projectRoot, "reports");
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public void Save(SequenceRunResult result)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, result.Id + ".tsrun.json");
        var payload = new { format = Format, version = 1, result };
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(payload, Options));
        File.Move(temporary, path, true);
        SaveHtml(result);
    }

    public string ReportPath(string runId) => Path.Combine(_root, runId + ".html");

    public IReadOnlyList<RunReportSummary> LoadSummaries()
    {
        var summaries = new List<RunReportSummary>();
        foreach (var item in LoadAll())
        {
            try
            {
                var result = item.GetProperty("result");
                var id = result.GetProperty("id").GetString() ?? "";
                summaries.Add(new RunReportSummary(id, result.GetProperty("sequence_name").GetString() ?? "",
                    result.GetProperty("status").GetString() ?? "", result.GetProperty("started_at").GetDateTimeOffset(),
                    result.GetProperty("finished_at").GetDateTimeOffset(), result.GetProperty("steps").GetArrayLength(), ReportPath(id)));
            }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        return summaries.OrderByDescending(report => report.StartedAt).ToArray();
    }

    public SequenceRunResult? LoadResult(string runId)
    {
        var path = Path.Combine(_root, runId + ".tsrun.json");
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("format", out var format) || format.GetString() != Format) return null;
        return document.RootElement.TryGetProperty("result", out var result)
            ? JsonSerializer.Deserialize<SequenceRunResult>(result.GetRawText(), Options)
            : null;
    }

    private void SaveHtml(SequenceRunResult result)
    {
        var temporary = ReportPath(result.Id) + ".tmp";
        var rows = string.Join(Environment.NewLine, result.Steps.Select(step =>
            $"<tr><td>{Html(step.SequenceId)}</td><td>{Html(step.Section)}</td><td>{Html(step.Name)}</td><td>{Html(step.Status)}</td><td>{step.DurationMs} ms</td><td>{Html(step.Message)}</td><td>{Html(step.Path)}</td></tr>"));
        var passed = result.Steps.Count(step => step.Status.Equals("passed", StringComparison.OrdinalIgnoreCase));
        var failed = result.Steps.Count(step => step.Status.Equals("failed", StringComparison.OrdinalIgnoreCase));
        var statusClass = result.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) ? "ok" : "bad";
        var html = $"<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{Html(result.SequenceName)} - 测试报告</title>" +
            "<style>:root{font-family:'Microsoft YaHei UI','Microsoft YaHei',sans-serif;color:#25313b;background:#f4f6f8}body{margin:0;padding:32px}.page{max-width:1180px;margin:auto;background:#fff;padding:30px 34px;border:1px solid #dce2e7;border-radius:12px;box-shadow:0 5px 18px #1b273315}h1{margin:0 0 6px;font-size:26px}.muted{color:#71808d;font-size:13px}.badge{display:inline-block;padding:5px 12px;border-radius:99px;font-weight:600;font-size:13px}.ok{background:#e5f5eb;color:#13753d}.bad{background:#fde9e8;color:#b42318}.cards{display:flex;gap:12px;margin:24px 0}.card{flex:1;padding:14px 16px;background:#f6f8fa;border:1px solid #e2e7eb;border-radius:8px}.value{font-size:22px;font-weight:700;margin-top:5px}table{border-collapse:collapse;width:100%;margin-top:22px;font-size:13px}th,td{border-bottom:1px solid #e5e9ed;padding:11px 10px;text-align:left}th{color:#60707d;background:#f6f8fa;font-weight:600}tr:last-child td{border-bottom:0}.passed{color:#13753d}.failed{color:#b42318}.skipped{color:#8a5b00}</style></head><body><main class=\"page\">" +
            $"<h1>{Html(result.SequenceName)}</h1><div class=\"muted\">测试运行报告 · {Html(result.Id)}</div><p>执行结果：<span class=\"badge {statusClass}\">{Html(result.Status)}</span></p>" +
            $"<div class=\"cards\"><div class=\"card\"><div class=\"muted\">步骤总数</div><div class=\"value\">{result.Steps.Count}</div></div><div class=\"card\"><div class=\"muted\">通过</div><div class=\"value passed\">{passed}</div></div><div class=\"card\"><div class=\"muted\">失败</div><div class=\"value failed\">{failed}</div></div><div class=\"card\"><div class=\"muted\">开始时间</div><div class=\"value\" style=\"font-size:14px\">{Html(result.StartedAt)}</div></div></div>" +
            $"<table><thead><tr><th>序列</th><th>区域</th><th>步骤</th><th>状态</th><th>耗时</th><th>消息</th><th>调用路径</th></tr></thead><tbody>{rows}</tbody></table></main></body></html>";
        File.WriteAllText(temporary, html);
        File.Move(temporary, ReportPath(result.Id), true);
    }

    private static string Html(string value) => System.Net.WebUtility.HtmlEncode(value ?? "");

    public IReadOnlyList<JsonElement> LoadAll()
    {
        if (!Directory.Exists(_root)) return [];
        var results = new List<JsonElement>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.tsrun.json").OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("format", out var format) || format.GetString() != Format) continue;
                results.Add(document.RootElement.Clone());
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        return results;
    }
}

public sealed record RunReportSummary(string Id, string SequenceName, string Status,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, int StepCount, string HtmlPath);
