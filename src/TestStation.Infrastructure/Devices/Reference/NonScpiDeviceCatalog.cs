using TestStation.Domain.Devices;

namespace TestStation.Infrastructure.Devices.Reference;

/// <summary>
/// Catalog of pending non-SCPI modules audited from the reference
/// project. The catalog deliberately keeps protocol and transport identity in
/// the family definition; it is not a second SCPI profile list.
/// </summary>
public static class NonScpiDeviceCatalog
{
    private static readonly string[] Modbus = ["serial", "tcp"];
    private static readonly string[] ModbusTcp = ["tcp"];
    private static readonly string[] ModbusSerial = ["serial"];

    public static IReadOnlyList<InstrumentFamily> Families { get; } = Build();

    private static IReadOnlyList<InstrumentFamily> Build()
    {
        var list = new List<InstrumentFamily>();
        void Add(string id, string name, string category, string protocol, IReadOnlyList<string> transports,
            string status = "尚未完成该型号的通道、指令或寄存器映射，当前不可连接", params string[] modelNames)
        {
            var names = modelNames.Length == 0 ? [name] : modelNames;
            var models = names.SelectMany(model => protocol == "scpi-modbus"
                    ? new[] { (Name: model + " (Modbus TCP)", Protocol: "modbus"), (Name: model + " (SCPI)", Protocol: "scpi") }
                    : new[] { (Name: model, Protocol: protocol) })
                .Select(item => new InstrumentModel(
                    id + "." + item.Name.Replace(' ', '-').Replace('/', '-').Replace('(', '-').Replace(')', '-'), item.Name, name, item.Protocol,
                    transports, [], "参考项目目录；驱动待迁移", status.Contains("不可连接") ? status : status + "；当前不可连接", RuntimeAvailable: false))
                .ToArray();
            list.Add(new InstrumentFamily(id, name, category, models));
        }

        Add("reference.modbus.C3000", "C3000", "电源", "modbus", Modbus, modelNames: ["C3000"]);
        Add("reference.modbus.D2000", "D2000", "电源", "modbus", Modbus, modelNames: ["D2000"]);
        Add("reference.modbus.DCEL1000", "DCEL1000", "电源", "modbus", ModbusSerial, modelNames: ["DCEL1000"]);
        Add("reference.modbus.GCT33030", "GCT33030", "交流源", "modbus", ModbusSerial, modelNames: ["GCT33030"]);
        Add("reference.modbus.GFC33030", "GFC33030", "交流源", "modbus", ModbusSerial, modelNames: ["GFC33030"]);
        Add("reference.modbus.SDC50", "SDC50", "电源", "modbus", ModbusSerial, modelNames: ["SDC50"]);
        Add("reference.modbus.G6000", "G6000", "交流源载", "modbus", ModbusTcp, modelNames: ["G6000"]);
        Add("reference.modbus.ZKAEL0093", "ZKAEL0093", "电子负载", "modbus", ModbusSerial, modelNames: ["ZKAEL0093"]);
        Add("reference.modbus.ZKAEL0093V2", "ZKAEL0093V2", "电子负载", "modbus", ModbusSerial, modelNames: ["ZKAEL0093V2"]);
        Add("reference.modbus.AN87600", "AN87600", "功率分析仪", "modbus", ModbusTcp, modelNames: ["AN87600"]);
        Add("reference.modbus.PA5000", "PA5000", "功率分析仪", "modbus", ModbusTcp, modelNames: ["PA5000"]);
        Add("reference.modbus.PW8001", "PW8001", "功率分析仪", "modbus", ModbusTcp, modelNames: ["PW8001"]);
        Add("reference.modbus.CARI", "CARI", "温箱", "modbus", ModbusTcp, modelNames: ["CARI"]);
        Add("reference.modbus.EMIC", "EMIC", "温箱", "modbus", ModbusTcp, modelNames: ["EMIC"]);
        Add("reference.modbus.TOMILO", "TOMILO", "温箱", "modbus", ModbusTcp, modelNames: ["TOMILO"]);
        Add("reference.modbus.TOMILOL", "TOMILOL", "温箱", "modbus", ModbusTcp, modelNames: ["TOMILOL"]);
        Add("reference.modbus.ZD2000D", "ZD2000D", "温湿度设备", "modbus", ModbusTcp, modelNames: ["ZD2000D"]);
        Add("reference.modbus.LG", "LG", "水冷机", "modbus", Modbus, modelNames: ["LG"]);
        Add("reference.modbus.LG1CH", "LG1CH", "水冷机", "modbus", ModbusSerial, modelNames: ["LG1CH"]);
        Add("reference.modbus.LG6CHAION", "LG6CHAION", "水冷机", "modbus", ModbusTcp, modelNames: ["LG6CHAION"]);
        Add("reference.modbus.TOMILO-XP", "TOMILO-XP", "水冷机", "modbus", ModbusTcp, modelNames: ["TOMILO-XP"]);
        Add("reference.modbus.SARSF112", "SARSF112", "旋变模拟器", "modbus", ModbusSerial, modelNames: ["SARSF112"]);
        Add("reference.modbus.SARSF212", "SARSF212", "旋变模拟器", "modbus", ModbusSerial, modelNames: ["SARSF212"]);
        Add("reference.modbus.SARSF312", "SARSF312", "旋变模拟器", "modbus", ModbusSerial, modelNames: ["SARSF312"]);

        Add("reference.itech.fixed.IT8211", "ITECH IT8211", "电子负载", "itech-fixed", ModbusSerial, modelNames: ["IT8211"]);
        Add("reference.itech.fixed.IT8500", "ITECH IT8500", "电子负载", "itech-fixed", ModbusSerial, modelNames: ["IT8500"]);
        Add("reference.binary.BP7330", "BP7330", "电源", "bp7330", ModbusTcp, modelNames: ["BP7330"]);
        Add("reference.udp.GWS", "GWS", "温箱", "gws-udp", ["udp"], modelNames: ["GWS"]);
        Add("reference.udp.N39240", "N39240", "电源", "n39240-udp-scpi", ["udp"], modelNames: ["N39240"]);
        Add("reference.serial.HZP", "HZP", "传感器", "hzp", ModbusSerial, modelNames: ["HZP"]);
        Add("reference.tcp.TongzhouWeipu", "TongzhouWeipu", "水冷机", "tongzhou-weipu", ModbusTcp, modelNames: ["TongzhouWeipu"]);
        Add("reference.tcp.TongzhouWeipuMCx", "TongzhouWeipuMCx", "水冷机", "tongzhou-weipu-mcx", ModbusTcp, modelNames: ["TongzhouWeipuMCx"]);
        Add("reference.scpi-modbus.ANRGL", "ANRGL", "交流源载", "scpi-modbus", ModbusTcp, modelNames: ["ANRGL"]);
        Add("reference.scpi-modbus.PRE20", "PRE20", "交流源载", "scpi-modbus", ModbusTcp, modelNames: ["PRE20"]);
        Add("reference.scpi-modbus.PROA18", "PROA18", "交流源载", "scpi-modbus", ModbusTcp, modelNames: ["PROA18"]);
        Add("reference.scpi.ANEVH", "ANEVH", "交流源载", "scpi", ["tcp"], modelNames: ["ANEVH"]);
        Add("reference.scpi.AN23600E", "AN23600E", "电子负载", "scpi", ["tcp"], modelNames: ["AN23600E"]);
        Add("reference.scpi.N68000", "N68000", "电子负载", "scpi", ["tcp"], modelNames: ["N68000"]);
        Add("reference.scpi.PW3335", "PW3335", "功率分析仪", "scpi", ["tcp"], modelNames: ["PW3335"]);
        Add("reference.scpi.PW3337", "PW3337", "功率分析仪", "scpi", ["tcp"], modelNames: ["PW3337"]);
        Add("reference.scpi.WT1800", "WT1800", "功率分析仪", "scpi", ["tcp"], modelNames: ["WT1800"]);
        Add("reference.scpi.DG1022Z", "DG1022Z", "信号发生器", "scpi", ["tcp"], modelNames: ["DG1022Z"]);

        // C 类 modules are registered as SDK-backed families. They have a
        // real device contract and an explicit unavailable state until the
        // vendor runtime is installed; they never fall back to memory values.
        Add("reference.sdk.GACRangeExtender", "GACRangeExtender", "继电器", "modbus-sdk", ModbusTcp, "需迁移共享继电器运行服务", "GACRangeExtender");
        Add("reference.sdk.GenericModbusTcp", "GenericModbusTcp", "继电器", "modbus-sdk", ModbusTcp, "需配置实际寄存器映射", "GenericModbusTcp");
        Add("reference.sdk.NI", "NI", "继电器/DAQ", "ni-daqmx", ["ni"], "需安装 NI-DAQmx/NI-SWITCH 运行库", "NI");
        Add("reference.sdk.ZKEVOBDC3", "ZKEVOBDC3", "继电器/导引", "relay-sdk", ModbusSerial, "需迁移共享继电器运行服务", "ZKEVOBDC3");
        Add("reference.sdk.ZLG-CAN", "ZLG CAN", "CAN 接口卡", "zlg-can", ["can"], "需安装 ZLG CAN SDK", "ZLG-CAN");
        return list;
    }

}
