using ColossalFramework;
using SkylinesAgentBridge.State;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Commands
{
    public static class SimulationCommands
    {
        public static CommandResult SetSimulationSpeed(string body)
        {
            bool paused = JsonUtil.GetBool(body, "paused", false);
            int speed = (int)JsonUtil.GetNumber(body, "speed", 1f);

            if (speed < 1)
            {
                speed = 1;
            }
            if (speed > 3)
            {
                speed = 3;
            }

            SimulationManager simulation = Singleton<SimulationManager>.instance;
            simulation.ForcedSimulationPaused = false;
            simulation.SelectedSimulationSpeed = speed;
            simulation.SimulationPaused = paused;

            string json = "{\"ok\":true,\"paused\":" + JsonUtil.Bool(simulation.SimulationPaused) +
                ",\"selectedSpeed\":" + simulation.SelectedSimulationSpeed +
                ",\"finalSpeed\":" + simulation.FinalSimulationSpeed +
                ",\"openPanels\":" + PanelState.SnapshotJson;
            string holder = PanelState.ClockHolder;
            if (holder.Length > 0)
            {
                // The clock runs again, but the panel is still on screen and pauses the game
                // once more (or resets the speed to 1) when it is closed.
                json += ",\"hint\":\"" + JsonUtil.Escape("The game's " + holder + " is still open; close it with city_close_panel, then set the speed again.") + "\"";
            }
            return CommandResult.FromJson(json + "}");
        }
    }
}
