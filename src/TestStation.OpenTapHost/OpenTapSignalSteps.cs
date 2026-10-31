using System.Text.Json;
using OpenTap;
using TestStation.Application;

namespace TestStation.OpenTapHost;

public sealed partial class OpenTapBridgeStep
{
    private void ExecuteExpressions(string source)
    {
        foreach (var assignment in SequenceExpressionProgram.Parse(source))
        {
            Context.CheckCancellation();
            Context.AssignVariable(assignment.Target, HostExpression.Value(assignment.Expression, Context.Values()));
        }
    }

    private void RunSignals(bool read)
    {
        if (!_step.Parameters.TryGetValue("signals", out var table))
        {
            // Legacy single-channel steps remain executable without a file migration.
            var channel = ReadString("channel_id");
            if (read)
            {
                var value = Context.ReadSignal(channel);
                Context.StoreChannel(channel, value); PublishMeasurement("读取值", channel, value);
            }
            else
            {
                var value = Resolve("value");
                Context.WriteSignal(channel, value); Context.StoreChannel(channel, value); PublishMeasurement("设置值", channel, value);
            }
            return;
        }
        var rows = SequenceSignalOperation.Read(table);
        if (rows.Length == 0) throw new InvalidDataException("信号表至少需要一行");
        foreach (var row in rows) row.Validate(read);
        var failed = new List<string>();
        foreach (var row in rows)
        {
            Context.CheckCancellation();
            if (Context.ExternalChannelTypes.TryGetValue(row.ChannelId, out var declaredType) && declaredType != row.DataType)
                throw new InvalidDataException($"设备通道 {row.ChannelId} 类型为 {declaredType}，序列声明为 {row.DataType}");
            if (!Context.ExternalChannels.Contains(row.ChannelId) && !Context.SimulationValues.ContainsKey(row.ChannelId))
                throw new InvalidDataException($"模拟通道未配置: {row.ChannelId}");
            if (!read)
            {
                var value = SequenceValueOperations.ReadInput(row.Value, row.DataType, row.Mode, Context.Values());
                Context.WriteSignal(row.ChannelId, value); Context.StoreChannel(row.ChannelId, value);
                PublishMeasurement("设置值", row.ChannelId, value);
                continue;
            }
            var actual = SequenceValueOperations.ConvertTo(Context.ReadSignal(row.ChannelId), row.DataType);
            Context.StoreChannel(row.ChannelId, actual);
            if (!string.IsNullOrWhiteSpace(row.Value)) Context.AssignVariable(row.Value.Trim(), actual);
            PublishMeasurement("读取值", row.ChannelId, actual);
            if (row.Comparator == "NONE") continue;
            var values = Context.Values();
            var range = row.Comparator is "IN_RANGE" or "OUTSIDE_RANGE";
            var minimum = range ? row.RuleInput(row.Minimum, values) : null;
            var maximum = range ? row.RuleInput(row.Maximum, values) : null;
            var expected = range ? null : row.RuleInput(row.Expected, values);
            var tolerance = row.Comparator is "ABS_TOL" or "REL_TOL"
                ? row.Mode == "expression" ? SequenceValueOperations.Number(HostExpression.Value(row.Tolerance, values)) : SequenceValueOperations.Number(row.Tolerance) : 0;
            var passed = SequenceValueOperations.Compare(actual, expected, row.Comparator, row.ArrayMode, minimum, maximum, tolerance);
            PublishAssertion(row.ChannelId, actual, row.Comparator + "/" + row.ArrayMode,
                range ? $"{DisplayValue(minimum)} .. {DisplayValue(maximum)}" : expected, passed);
            if (!passed) failed.Add(row.ChannelId);
        }
        if (failed.Count > 0) HandleFailure($"读取判定失败: {string.Join("、", failed)}", Verdict.Fail);
    }
}
