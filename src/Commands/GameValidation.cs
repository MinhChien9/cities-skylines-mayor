using System;
using System.Text;
using ColossalFramework;

namespace SkylinesAgentBridge.Commands
{
    /// <summary>
    /// Shared plumbing for the game-validated build commands.
    ///
    /// WHY THIS EXISTS
    /// ---------------
    /// The upstream build commands called <c>NetManager.CreateSegment</c> and
    /// <c>BuildingManager.CreateBuilding</c> directly. Those are the *storage* layer: they
    /// skip every rule the player's tools enforce — node snapping and intersection
    /// splitting, collision with existing roads and buildings, slope limits, unlocked map
    /// tiles, money. That is why coordinate building produced overlapping, disconnected,
    /// free roads and buildings standing in rivers.
    ///
    /// The build commands now go through the same static entry points the game's own tools
    /// use (<c>NetTool.CreateNode</c>, <c>BuildingTool.CheckSpace</c>,
    /// <c>BuildingAI.CheckBuildPosition</c>), first in <c>test</c> mode. A placement the
    /// player could not make is refused with the game's own <see cref="ToolBase.ToolErrors"/>,
    /// which this class turns into readable names.
    ///
    /// All of these run on the simulation thread — the same thread the tools' own
    /// <c>SimulationStep</c> runs them on.
    /// </summary>
    internal static class GameValidation
    {
        /// <summary>
        /// True when the game charges for construction (game mode, not the editors). Same
        /// test the tools use.
        /// </summary>
        public static bool NeedMoney()
        {
            ToolController properties = Singleton<ToolManager>.instance.m_properties;
            return properties != null && (properties.m_mode & ItemClass.Availability.Game) != 0;
        }

        /// <summary>True when the city treasury can pay <paramref name="cost"/> right now.</summary>
        public static bool CanAfford(int cost)
        {
            if (cost <= 0)
            {
                return true;
            }
            return Singleton<EconomyManager>.instance.PeekResource(EconomyManager.Resource.Construction, cost) == cost;
        }

        /// <summary>Renders a ToolErrors bit set as a JSON array of flag names.</summary>
        public static string ErrorsJson(ToolBase.ToolErrors errors)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('[');
            bool first = true;
            ulong bits = (ulong)errors;
            if (bits != 0)
            {
                foreach (ToolBase.ToolErrors flag in Enum.GetValues(typeof(ToolBase.ToolErrors)))
                {
                    ulong value = (ulong)flag;
                    if (value == 0 || (value & (value - 1)) != 0)
                    {
                        continue; // skip None and any combined aliases
                    }
                    if ((bits & value) == 0)
                    {
                        continue;
                    }
                    bits &= ~value; // an enum may carry two names for one bit: report it once
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    sb.Append('"').Append(flag.ToString()).Append('"');
                    first = false;
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>
        /// A one-line hint for the most common errors, so the agent knows what to change
        /// rather than retrying the same coordinates.
        /// </summary>
        public static string Hint(ToolBase.ToolErrors errors)
        {
            if ((errors & ToolBase.ToolErrors.NotEnoughMoney) != 0)
            {
                return "Not enough money. Wait for income or cut costs, then retry.";
            }
            if ((errors & ToolBase.ToolErrors.OutOfArea) != 0)
            {
                return "Outside the unlocked map tiles. Build inside the owned area.";
            }
            if ((errors & ToolBase.ToolErrors.ObjectCollision) != 0)
            {
                return "Collides with an existing road or building. Move it, or snap to the existing road instead of crossing it at a shallow angle.";
            }
            if ((errors & ToolBase.ToolErrors.SlopeTooSteep) != 0)
            {
                return "Terrain is too steep here. Pick flatter ground (check /state/map heights).";
            }
            if ((errors & ToolBase.ToolErrors.TooShort) != 0)
            {
                return "Too short. Lengthen it, or snap to the existing node instead.";
            }
            if ((errors & ToolBase.ToolErrors.InvalidShape) != 0)
            {
                return "Invalid shape: the curve or junction angle is too sharp.";
            }
            if ((errors & ToolBase.ToolErrors.CannotBuildOnWater) != 0)
            {
                return "Cannot build on water.";
            }
            if ((errors & ToolBase.ToolErrors.ShoreNotFound) != 0)
            {
                return "Needs a shoreline: place it at the water's edge.";
            }
            if ((errors & ToolBase.ToolErrors.GridNotFound) != 0)
            {
                return "Needs free road frontage: aim within ~8 m of a road edge where no other building stands (grown houses occupy zoned frontage - pick another street, or bulldoze).";
            }
            if ((errors & ToolBase.ToolErrors.CannotConnect) != 0)
            {
                return "Cannot connect to the road there.";
            }
            if ((errors & ToolBase.ToolErrors.TooManyObjects) != 0)
            {
                return "The game's object limit is reached.";
            }
            return "";
        }

        /// <summary>Common result fields: ok, errors, hint.</summary>
        public static string ErrorFields(ToolBase.ToolErrors errors)
        {
            return "\"errors\":" + ErrorsJson(errors) +
                ",\"hint\":\"" + Support.JsonUtil.Escape(Hint(errors)) + "\"";
        }
    }
}
