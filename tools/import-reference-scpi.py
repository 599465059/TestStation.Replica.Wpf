"""Export pure reference SCPI definitions; never imports UI, polling or device lifetime.

Usage: python tools/import-reference-scpi.py F:/my-test-system-master
This is an audit/export utility only. It does not register devices in the app.
"""
import hashlib
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
source = Path(sys.argv[1])
work = root / 'artifacts/reference-scpi-export'
work.mkdir(parents=True, exist_ok=True)
selected = []
for protocol in sorted(source.glob('*Modules.ITECH.*/*ScpiProtocol.cs')):
    if '_Copy' in str(protocol.parent):
        continue
    catalog = list(protocol.parent.glob('*ModelCatalog.cs'))
    if len(catalog) == 1 and 'using Device.Core.Scpi;' in protocol.read_text(encoding='utf-8-sig'):
        selected += [protocol, catalog[0]]
selected += [source / 'Device.Core/Scpi' / f'{name}.cs' for name in (
    'GenericScpiCommandSet', 'GenericScpiParameterDefinition', 'GenericScpiCommandDefinition',
    'GenericScpiParameterType', 'GenericScpiCommandType')]
# Only copy pure definition files into the disposable exporter project.
for old in work.glob('Source*.cs'):
    old.unlink()
for i, path in enumerate(selected):
    (work / f'Source{i}.cs').write_text(path.read_text(encoding='utf-8-sig'), encoding='utf-8')
(work / 'Export.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
<ImplicitUsings>enable</ImplicitUsings><Nullable>disable</Nullable><NoWarn>8632</NoWarn></PropertyGroup></Project>''', encoding='utf-8')
(work / 'Program.cs').write_text('''using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Device.Core.Scpi;
var result = new List<object>();
foreach (var type in Assembly.GetExecutingAssembly().GetTypes().Where(t => t.Name.EndsWith("ScpiProtocol")))
{
    var create = type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
    if (create == null || create.GetParameters().Length != 1) continue;
    var catalog = Assembly.GetExecutingAssembly().GetTypes().Single(t => t.Namespace == type.Namespace && t.Name.EndsWith("ModelCatalog"));
    var all = (System.Collections.IEnumerable)(catalog.GetProperty("All", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
        ?? catalog.GetField("All", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
        ?? throw new Exception("Missing catalog: " + catalog.FullName));
    var models = new List<object>();
    foreach (var specification in all)
    {
        var commands = (GenericScpiCommandSet)create.Invoke(null, new[]{ specification });
        models.Add(new { Specification = specification, Commands = commands });
    }
    result.Add(new { SourceModule = type.Namespace, Models = models });
}
File.WriteAllText(args[0], JsonSerializer.Serialize(result, new JsonSerializerOptions {
    Converters = { new JsonStringEnumConverter() } }));
Console.WriteLine($"Exported {result.Count} SCPI modules");
''', encoding='utf-8')
output = root / 'artifacts/reference-device-audit/scpi-definitions.json'
output.parent.mkdir(parents=True, exist_ok=True)
subprocess.run(['dotnet', 'run', '--project', str(work / 'Export.csproj'), '--', str(output)], check=True, cwd=root)
manifest = [{'path': str(p.relative_to(source)).replace('\\', '/'), 'sha256': hashlib.sha256(p.read_bytes()).hexdigest()} for p in selected]
output.with_name('scpi-sources.json').write_text(json.dumps({'schemaVersion': 1, 'files': manifest}, ensure_ascii=False, indent=2), encoding='utf-8')
