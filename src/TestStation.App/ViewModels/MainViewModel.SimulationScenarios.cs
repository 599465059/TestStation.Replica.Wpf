using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application;

namespace TestStation.App.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<SimulationScenario> SimulationScenarios { get; } = [];
    [ObservableProperty] private SimulationScenario? _selectedSimulationScenario;
    [ObservableProperty] private string _simulationScenarioName = "";
    [ObservableProperty] private string _activeSimulationScenario = "当前模拟数据";

    partial void OnSelectedSimulationScenarioChanged(SimulationScenario? value)
    { if (value is not null) SimulationScenarioName = value.Name; }

    private void LoadSimulationScenarios()
    {
        SimulationScenarios.Clear(); SelectedSimulationScenario = null;
        if (_workspace is null) return;
        try
        {
            foreach (var scenario in _workspaceService.LoadScenarios(_workspace)) SimulationScenarios.Add(scenario);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        { Status = $"模拟场景读取失败: {error.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanEditSimulation))]
    private void SaveSimulationScenario()
    {
        if (!CanEditSimulation || _workspace is null || HasPendingSimulationEdits && !TryApplySimulation()) return;
        try
        {
            var name = SimulationScenarioName.Trim();
            if (name.Length == 0) throw new InvalidDataException("请填写场景名称");
            var scenarios = _workspaceService.LoadScenarios(_workspace).Where(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            scenarios.Add(new SimulationScenario(name, SimulationValues.ToDictionary(v => v.Channel, v => v.Value.Clone())));
            _workspaceService.SaveScenarios(_workspace, scenarios); LoadSimulationScenarios();
            SelectedSimulationScenario = SimulationScenarios.First(s => s.Name == name);
            ActiveSimulationScenario = name; Status = $"模拟场景已保存: {name}";
        }
        catch (Exception error) { Status = $"保存场景失败: {error.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanEditSimulation))]
    private void LoadSimulationScenario()
    {
        if (!CanEditSimulation || _workspace is null || SelectedSimulationScenario is not { } selected) return;
        // Finish validation of the pending form before replacing the active data set.
        if (HasPendingSimulationEdits && !TryApplySimulation()) return;
        try
        {
            _workspaceService.SaveSimulation(_workspace, selected.Values, preserveDefinitions: true);
            LoadSimulationValues(selected.Values);
            ActiveSimulationScenario = selected.Name; Status = $"已载入模拟场景: {selected.Name}";
        }
        catch (Exception error) { Status = $"载入场景失败: {error.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanEditSimulation))]
    private void DeleteSimulationScenario()
    {
        if (!CanEditSimulation || _workspace is null || SelectedSimulationScenario is not { } selected) return;
        try
        {
            _workspaceService.SaveScenarios(_workspace,
                _workspaceService.LoadScenarios(_workspace).Where(s => !s.Name.Equals(selected.Name, StringComparison.OrdinalIgnoreCase)).ToArray());
            LoadSimulationScenarios(); SimulationScenarioName = "";
            if (ActiveSimulationScenario == selected.Name) ActiveSimulationScenario = "当前模拟数据";
            Status = "场景已删除，当前模拟通道数据保留";
        }
        catch (Exception error) { Status = $"删除场景失败: {error.Message}"; }
    }
}
