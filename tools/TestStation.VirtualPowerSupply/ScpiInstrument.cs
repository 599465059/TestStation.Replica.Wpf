using System.Globalization;
using System.Text;

namespace TestStation.VirtualPowerSupply;

/// <summary>A standalone instrument state machine. No Test Station driver or application dependencies.</summary>
internal sealed class ScpiInstrument(double maxVoltage, double maxCurrent, double loadOhms)
{
    private readonly object _sync = new();
    private readonly Queue<(int Code, string Text)> _errors = new();
    private double _voltage;
    private double _current = Math.Min(1, maxCurrent);
    private bool _output;
    private int _esr;
    private int _ese;
    private int _sre;
    private bool _remote;
    private const string Identity = "TestStation,Virtual-SCPI-PSU,SIM0001,1.0";

    private double ActualVoltage => !_output ? 0 : Math.Min(_voltage, _current * loadOhms);
    private double ActualCurrent => ActualVoltage / loadOhms;

    public string State
    {
        get
        {
            lock (_sync) return FormattableString.Invariant($"OUT={(_output ? 1 : 0)} SET={_voltage:G6}V/{_current:G6}A MEAS={ActualVoltage:G6}V/{ActualCurrent:G6}A {(_output && _voltage / loadOhms > _current ? "CC" : "CV")} {(_remote ? "REMOTE" : "LOCAL")}");
        }
    }

    /// <summary>Each line is a SCPI program message; query results share one semicolon-separated response.</summary>
    public string? Execute(string line)
    {
        lock (_sync)
        {
            var replies = new List<string>();
            var parent = "";
            foreach (var unit in Split(line))
            {
                var text = unit.Trim();
                if (text.Length == 0) { Error(-102, "Syntax error"); continue; }
                var space = text.IndexOfAny([' ', '\t']);
                var header = space < 0 ? text : text[..space];
                var parameter = space < 0 ? "" : text[(space + 1)..].Trim();
                var absolute = header.StartsWith(':');
                header = header.TrimStart(':').ToUpperInvariant();
                var common = header.StartsWith('*');
                if (!common && !absolute && parent.Length > 0) header = parent + ":" + header;
                if (!common) { var last = header.LastIndexOf(':'); parent = last < 0 ? "" : header[..last]; }
                var response = Dispatch(Canonical(header), parameter);
                if (response is not null) replies.Add(response);
            }
            return replies.Count == 0 ? null : string.Join(';', replies);
        }
    }

    private string? Dispatch(string header, string parameter)
    {
        var query = header.EndsWith('?');
        var command = query ? header[..^1] : header;
        // Only the documented MAX/MIN setpoint queries accept query parameters.
        if (query && parameter.Length > 0 && command is not ("SOUR:VOLT" or "VOLT" or "SOUR:CURR" or "CURR"))
        { Error(-108, "Parameter not allowed"); return null; }
        if (query)
        {
            switch (command)
            {
                case "*IDN": return Identity;
                case "*OPC": return "1";
                case "*ESR": var esr = _esr; _esr = 0; return esr.ToString(CultureInfo.InvariantCulture);
                case "*ESE": return _ese.ToString(CultureInfo.InvariantCulture);
                case "*SRE": return _sre.ToString(CultureInfo.InvariantCulture);
                case "*STB":
                    var stb = (_errors.Count > 0 ? 4 : 0) | ((_esr & _ese) != 0 ? 32 : 0);
                    if ((stb & _sre & ~64) != 0) stb |= 64;
                    return stb.ToString(CultureInfo.InvariantCulture);
                case "SYST:ERR": case "SYST:ERR:NEXT":
                    var error = _errors.Count > 0 ? _errors.Dequeue() : (0, "No error");
                    return $"{error.Item1},\"{error.Item2}\"";
                case "SYST:ERR:COUNT": return _errors.Count.ToString(CultureInfo.InvariantCulture);
                case "SOUR:VOLT": case "VOLT": return SetpointQuery(parameter, _voltage, maxVoltage);
                case "SOUR:CURR": case "CURR": return SetpointQuery(parameter, _current, maxCurrent);
                case "OUTP": case "OUTP:STAT": return _output ? "1" : "0";
                case "MEAS:VOLT": case "MEAS:VOLT:DC": return Number(ActualVoltage);
                case "MEAS:CURR": case "MEAS:CURR:DC": return Number(ActualCurrent);
                case "MEAS:POW": return Number(ActualVoltage * ActualCurrent);
                default: Error(-113, "Undefined header"); return null;
            }
        }
        switch (command)
        {
            case "*RST":
                if (!NoParameter(parameter)) return null;
                _voltage = 0; _current = Math.Min(1, maxCurrent); _output = false;
                // *RST resets output configuration; *CLS is responsible for status/error clearing.
                break;
            case "*CLS": if (NoParameter(parameter)) { _errors.Clear(); _esr = 0; } break;
            case "*OPC": if (NoParameter(parameter)) _esr |= 1; break;
            case "*WAI": NoParameter(parameter); break; // All operations in this subset finish synchronously.
            case "*ESE": SetRegister(parameter, ref _ese); break;
            case "*SRE": SetRegister(parameter, ref _sre); _sre &= ~64; break;
            case "SOUR:VOLT": case "VOLT": SetNumber(parameter, maxVoltage, "V", ref _voltage); break;
            case "SOUR:CURR": case "CURR": SetNumber(parameter, maxCurrent, "A", ref _current); break;
            case "OUTP": case "OUTP:STAT":
                switch (parameter.ToUpperInvariant())
                {
                    case "ON": case "1": _output = true; break;
                    case "OFF": case "0": _output = false; break;
                    default: Error(parameter.Length == 0 ? -109 : -224, parameter.Length == 0 ? "Missing parameter" : "Illegal parameter value"); break;
                }
                break;
            case "SYST:REM": if (NoParameter(parameter)) _remote = true; break;
            case "SYST:LOC": if (NoParameter(parameter)) _remote = false; break;
            default: Error(-113, "Undefined header"); break;
        }
        return null;
    }

    private string? SetpointQuery(string parameter, double current, double maximum)
    {
        switch (parameter.ToUpperInvariant())
        {
            case "": return Number(current);
            case "MIN": case "MINIMUM": return "0";
            case "MAX": case "MAXIMUM": return Number(maximum);
            default: Error(-224, "Illegal parameter value"); return null;
        }
    }

    private void SetNumber(string parameter, double maximum, string unit, ref double target)
    {
        if (parameter.Length == 0) { Error(-109, "Missing parameter"); return; }
        var text = parameter.Trim().ToUpperInvariant();
        if (text.EndsWith(unit, StringComparison.Ordinal)) text = text[..^unit.Length].TrimEnd();
        double value;
        if (text is "MIN" or "MINIMUM") value = 0;
        else if (text is "MAX" or "MAXIMUM") value = maximum;
        else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
        { Error(-128, "Numeric data error"); return; }
        if (value < 0 || value > maximum) { Error(-222, "Data out of range"); return; }
        target = value;
    }

    private void SetRegister(string parameter, ref int target)
    {
        if (!int.TryParse(parameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 255)
        { Error(-222, "Data out of range"); return; }
        target = value;
    }

    private bool NoParameter(string parameter)
    { if (parameter.Length == 0) return true; Error(-108, "Parameter not allowed"); return false; }

    private void Error(int code, string text)
    {
        _esr |= code is <= -100 and > -200 ? 32 : 16;
        if (_errors.Count < 16) _errors.Enqueue((code, text));
        else if (_errors.Last().Code != -350)
        {
            var kept = _errors.Take(15).ToArray(); _errors.Clear();
            foreach (var item in kept) _errors.Enqueue(item);
            _errors.Enqueue((-350, "Queue overflow"));
        }
    }

    private static string Number(double value) => value.ToString("G12", CultureInfo.InvariantCulture);

    private static string Canonical(string header)
    {
        var query = header.EndsWith('?');
        var parts = (query ? header[..^1] : header).Split(':').Select(node => node switch
        {
            "SOURCE" => "SOUR", "VOLTAGE" => "VOLT", "CURRENT" => "CURR", "OUTPUT" => "OUTP",
            "MEASURE" => "MEAS", "POWER" => "POW", "SYSTEM" => "SYST", "ERROR" => "ERR",
            "REMOTE" => "REM", "LOCAL" => "LOC", "STATE" => "STAT", "AMPLITUDE" => "AMPL",
            "IMMEDIATE" => "IMM", _ => node
        });
        var path = string.Join(':', parts);
        // Optional source nodes supported by this simulator's explicitly documented subset.
        path = path.Replace(":IMM:AMPL", "", StringComparison.Ordinal);
        if (path.EndsWith(":IMM", StringComparison.Ordinal)) path = path[..^4];
        if (path.EndsWith(":AMPL", StringComparison.Ordinal)) path = path[..^5];
        return path + (query ? "?" : "");
    }

    private static IEnumerable<string> Split(string line)
    {
        var start = 0; var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '\'' or '"') quote = c;
            else if (c == ';') { yield return line[start..i]; start = i + 1; }
        }
        yield return line[start..];
    }
}
