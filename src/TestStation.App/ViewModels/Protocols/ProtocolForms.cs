using System.Globalization;
using System.Text.Json;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TestStation.Application.Protocols;
using TestStation.Domain.Protocols;

namespace TestStation.App.ViewModels.Protocols;

public partial class ProtocolFieldForm : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _role = "data";
    [ObservableProperty] private string _dataType = "uint8";
    [ObservableProperty] private string _byteOffset = "0";
    [ObservableProperty] private string _length = "1";
    [ObservableProperty] private string _byteOrder = "big";
    [ObservableProperty] private string _scale = "1";
    [ObservableProperty] private string _valueOffset = "0";
    [ObservableProperty] private string _constant = "";
    [ObservableProperty] private string _channelId = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _enabled = true;
    public ProtocolFieldForm() { }
    public ProtocolFieldForm(ProtocolField item)
    {
        Id = item.Id; Name = item.Name; Role = item.Role; DataType = item.DataType; ByteOffset = item.ByteOffset.ToString(CultureInfo.InvariantCulture);
        Length = item.Length.ToString(CultureInfo.InvariantCulture); ByteOrder = item.ByteOrder; Scale = item.Scale.ToString(CultureInfo.InvariantCulture);
        ValueOffset = item.ValueOffset.ToString(CultureInfo.InvariantCulture); Constant = item.ConstantValue?.GetRawText() ?? "";
        ChannelId = item.ChannelId; Description = item.Description; Enabled = item.Enabled;
    }
    public ProtocolField Build(ProtocolField source)
    {
        var item = ProtocolCopy.Clone(source);
        item.Id = Id.Trim(); item.Name = Name.Trim(); item.Role = Role; item.DataType = DataType;
        item.ByteOffset = FormNumbers.Integer(ByteOffset, "起始字节"); item.Length = FormNumbers.Integer(Length, "字段长度"); item.ByteOrder = ByteOrder;
        item.Scale = FormNumbers.Number(Scale, "比例"); item.ValueOffset = FormNumbers.Number(ValueOffset, "偏移");
        if (string.IsNullOrWhiteSpace(Constant)) item.ConstantValue = null;
        else
        {
            try { item.ConstantValue = JsonDocument.Parse(Constant).RootElement.Clone(); }
            catch (JsonException) { item.ConstantValue = JsonSerializer.SerializeToElement(Constant); }
        }
        item.ChannelId = ChannelId; item.Description = Description; item.Enabled = Enabled;
        return item;
    }
}

public partial class ProtocolFrameForm : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _kind = "read_request";
    [ObservableProperty] private string _fixedLength = "0";
    [ObservableProperty] private string _lengthFieldId = "";
    [ObservableProperty] private string _checksumAlgorithm = "none";
    [ObservableProperty] private string _checksumStart = "0";
    [ObservableProperty] private string _description = "";
    public ProtocolFrameForm() { }
    public ProtocolFrameForm(ProtocolFrame frame)
    {
        Id = frame.Id; Name = frame.Name; Kind = frame.Kind; FixedLength = frame.FixedLength.ToString(CultureInfo.InvariantCulture);
        LengthFieldId = frame.LengthFieldId; ChecksumAlgorithm = frame.ChecksumAlgorithm; ChecksumStart = frame.ChecksumStart.ToString(CultureInfo.InvariantCulture); Description = frame.Description;
    }
    public ProtocolFrame Build(ProtocolFrame source)
    {
        var frame = ProtocolCopy.Clone(source);
        frame.Id = Id.Trim(); frame.Name = Name.Trim(); frame.Kind = Kind; frame.FixedLength = FormNumbers.Integer(FixedLength, "固定帧长度");
        frame.LengthFieldId = LengthFieldId.Trim(); frame.ChecksumAlgorithm = ChecksumAlgorithm; frame.ChecksumStart = FormNumbers.Integer(ChecksumStart, "校验起点"); frame.Description = Description;
        return frame;
    }
}

internal static class FormNumbers
{
    public static int Integer(string value, string label) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new ArgumentException($"{label} 应为整数");
    public static double Number(string value, string label) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new ArgumentException($"{label} 应为数字");
}

public sealed record ProtocolFieldRole(string Role, string Name, string Icon, string BrushKey);
public sealed record ProtocolTreeEntry(string Name, string? FrameId);
public partial class ProtocolTreeRoot : ObservableObject
{
    [ObservableProperty] private string _name = "";
    public ObservableCollection<ProtocolTreeEntry> Children { get; }
    public ProtocolTreeRoot(string name, IEnumerable<ProtocolTreeEntry> children) { Name = name; Children = new(children); }
}
public partial class ProtocolFramePresentation : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Kind), nameof(CalculatedLength))]
    private ProtocolFrame _frame;
    public ProtocolFramePresentation(ProtocolFrame frame) => _frame = frame;
    public string Name => Frame.Name;
    public string Kind => Frame.Kind;
    public int CalculatedLength => Frame.CalculatedLength;
}
