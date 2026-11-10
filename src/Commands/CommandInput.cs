using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Commands
{
    /// <summary>
    /// Readers for the JSON body of a command request.
    ///
    /// Every command receives its arguments as a JSON string. These helpers keep the
    /// "how do I pull a coordinate out of that string" question in one place instead of
    /// repeating the same three JsonUtil calls in every command.
    /// </summary>
    internal static class CommandInput
    {
        /// <summary>
        /// Reads a <c>{"x":..,"y":..,"z":..}</c> object out of the request body.
        /// Missing axes default to zero, which is the world origin.
        /// </summary>
        public static Vector3 ReadPoint(string body, string name)
        {
            float x = JsonUtil.GetPointNumber(body, name, "x", 0f);
            float y = JsonUtil.GetPointNumber(body, name, "y", 0f);
            float z = JsonUtil.GetPointNumber(body, name, "z", 0f);
            return new Vector3(x, y, z);
        }
    }
}
