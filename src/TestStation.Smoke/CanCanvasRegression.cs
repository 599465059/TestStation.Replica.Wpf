using System.IO;
using System.Text.Json;
using TestStation.Application.Can;
using TestStation.Infrastructure.Protocols;
using TestStation.App.ViewModels;
using TestStation.Domain.Can;

namespace TestStation.Smoke;

/// <summary>
/// Model-level regression coverage for the CAN free canvas.  Keeping this
/// separate from the WPF UI smoke makes canvas persistence and edit rules
/// deterministic and fast to run after each CAN change.
/// </summary>
internal static class CanCanvasRegression
{
    public static void VerifyModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "test-station-can-canvas", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var simulation = new SimulatedCanService();
        using var viewModel = new CanTestViewModel(simulation, new DbcService());
        viewModel.OpenProject(root);

        Require(viewModel.IsDesignMode, "A new CAN canvas must start in design mode");
        Require(viewModel.Widgets.Count == 4, "A new CAN canvas must contain the four default CAN widgets");
        Require(viewModel.Widgets.Select(item => item.Kind).OrderBy(item => item).SequenceEqual(
            ["dbc_send", "manual_send", "message_tree", "signal_monitor"]),
            "Default CAN widget kinds are incomplete");

        var widget = viewModel.Widgets.Single(item => item.Kind == "signal_monitor");
        viewModel.SelectWidget(widget);
        Require(widget.IsSelected && viewModel.Widgets.Count(item => item.IsSelected) == 1,
            "Selecting a CAN widget did not clear other selections");

        var beforeResize = (widget.Left, widget.Top, widget.Width, widget.Height);
        viewModel.ResizeWidget(widget, "E", 80, 0);
        Require(widget.Width == beforeResize.Width + 80 && widget.Left == beforeResize.Left,
            "East resize did not preserve the left edge");
        viewModel.ResizeWidget(widget, "S", 0, 60);
        Require(widget.Height == beforeResize.Height + 60 && widget.Top == beforeResize.Top,
            "South resize did not preserve the top edge");
        viewModel.ResizeWidget(widget, "W", 40, 0);
        Require(widget.Left == beforeResize.Left + 40 && widget.Width == beforeResize.Width + 40,
            "West resize did not preserve the right edge");
        viewModel.ResizeWidget(widget, "N", 0, 25);
        Require(widget.Top == beforeResize.Top + 25 && widget.Height == beforeResize.Height + 35,
            "North resize did not preserve the bottom edge");

        viewModel.ResizeWidget(widget, "E", -10000, 0);
        viewModel.ResizeWidget(widget, "S", 0, -10000);
        Require(widget.Width == 220 && widget.Height == 150,
            "CAN widget minimum width and height were not enforced");
        Require(widget.Left >= 0 && widget.Top >= 0, "CAN widget coordinates became negative");

        var countBeforeRun = viewModel.Widgets.Count;
        viewModel.ToggleDesignModeCommand.Execute(null);
        Require(!viewModel.IsDesignMode && !viewModel.Widgets.Any(item => item.IsSelected),
            "Switching to run mode did not clear selection");
        viewModel.DeleteWidgetCommand.Execute(widget);
        Require(viewModel.Widgets.Count == countBeforeRun, "Run mode allowed deleting a CAN widget");
        var runBounds = (widget.Left, widget.Top, widget.Width, widget.Height);
        viewModel.ResizeWidget(widget, "E", 50, 50);
        Require((widget.Left, widget.Top, widget.Width, widget.Height) == runBounds,
            "Run mode allowed resizing a CAN widget");

        viewModel.ToggleDesignModeCommand.Execute(null);
        viewModel.SelectWidget(widget);
        var temporary = viewModel.Widgets.Last();
        viewModel.DeleteWidgetCommand.Execute(temporary);
        Require(viewModel.Widgets.Count == countBeforeRun - 1 && !viewModel.Widgets.Contains(temporary),
            "Design mode did not delete the selected CAN widget");
        viewModel.AddWidget("manual_send");
        Require(viewModel.Widgets.Count == countBeforeRun, "Adding a CAN widget after deletion failed");

        var save = viewModel.SaveProjectChanges();
        Require(save.Success, $"CAN workspace save failed: {save.Error}");
        var workspacePath = Path.Combine(root, "layouts", "can", "workspace.json");
        Require(File.Exists(workspacePath), "CAN workspace was not saved to layouts/can/workspace.json");
        using (var document = JsonDocument.Parse(File.ReadAllText(workspacePath)))
        {
            Require(document.RootElement.GetProperty("Version").GetInt32() == 1,
                "CAN workspace version is incorrect");
            Require(document.RootElement.GetProperty("Widgets").GetArrayLength() == countBeforeRun,
                "CAN workspace saved an unexpected widget count");
        }

        using var reopened = new CanTestViewModel(new SimulatedCanService(), new DbcService());
        reopened.OpenProject(root);
        Require(reopened.Widgets.Count == countBeforeRun, "CAN widget count did not survive reload");
        var restored = reopened.Widgets.Single(item => item.Kind == "signal_monitor");
        Require((restored.Left, restored.Top, restored.Width, restored.Height) ==
            (widget.Left, widget.Top, widget.Width, widget.Height),
            "CAN widget bounds did not survive save and reload");

        Console.WriteLine("PASS: CAN canvas defaults, selection, design/run lock, resize minimums, delete/add, save and reload");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
