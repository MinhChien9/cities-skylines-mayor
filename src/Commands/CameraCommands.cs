using System;
using System.Reflection;
using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Commands
{
    /// <summary>
    /// Camera framing and world→screen projection.
    ///
    /// THREADING: every method here runs on the **Unity main thread** through the
    /// bridge's named main-thread queue (see MainThreadQueue). The camera and its
    /// transform are Unity objects owned by the main thread, and projection needs
    /// the camera's view matrix — running any of this on the simulation thread or an
    /// HTTP worker races the renderer exactly the way the simulation dispatcher's
    /// comment warns. These commands must never touch city state.
    ///
    /// WHICH CAMERA (a live-game finding, 2026-11-11)
    /// ----------------------------------------------
    /// `ICities.ICamera` (IManagers.camera → CameraWrapper) drives the game's
    /// **CinematicCameraController** — the photo-mode camera. Framing through it moves
    /// a camera nobody renders; the player's view does not change. Verified live: a
    /// SetCameraTarget call through the wrapper changed nothing on screen.
    ///
    /// The camera the player (and the cursor) actually uses is **CameraController** —
    /// the free camera. Its state is fields, not methods:
    ///   m_targetPosition / m_currentPosition   the point the camera looks at
    ///   m_targetAngle    / m_currentAngle      Vector2 (x = horizontal° yaw, -180..180;
    ///                                          y = vertical° pitch, 0..90 = level..top-down)
    ///   m_targetSize     / m_currentSize       camera distance from the target (zoom)
    ///   m_velocity / m_angleVelocity / m_zoomVelocity   inertia — zeroed for instant moves
    /// The game lerps current → target every frame, so writing the target fields makes
    /// the camera glide; snapping both makes it jump. Both are supported here
    /// ("instant": true by default, so the office's aim is deterministic).
    /// </summary>
    public static class CameraCommands
    {
        // ------------------------------------------------------------------ set-camera

        /// <summary>
        /// POST /commands/set-camera.
        ///
        /// Body (all fields optional):
        ///   x, z              - world position to frame (y defaults to sea level)
        ///   y                 - world height of the target point
        ///   distance          - camera distance to the target (default: keep current zoom)
        ///   verticalAngle     - vertical viewing angle in degrees (default: keep current)
        ///   horizontalAngle   - horizontal viewing angle in degrees (default: keep current)
        ///   instant           - true (default) to snap the camera, false to glide
        ///   edgeScroll        - true/false to toggle the edge-scrolling setting
        ///   hideUi            - true/false to enter/leave the game's free-camera mode, which
        ///                       hides the whole UI (including this bridge's console),
        ///                       notification icons, district names and borders: clean B-roll
        ///
        /// With no coordinates, no edgeScroll and no hideUi the command is a pure
        /// camera-state read. The response always carries the live readback: camera
        /// position, angle, distance, the edge-scrolling and UI state and the screen size.
        /// </summary>
        public static CommandResult SetCamera(string body)
        {
            CameraController controller = FindCameraController();
            if (controller == null)
            {
                return CommandResult.Fail("CameraController not found. Is a city loaded?");
            }

            float x = JsonUtil.GetNumber(body, "x", float.NaN);
            float z = JsonUtil.GetNumber(body, "z", float.NaN);
            bool hasTarget = !float.IsNaN(x) && !float.IsNaN(z);
            if (float.IsNaN(x) != float.IsNaN(z))
            {
                return CommandResult.Fail("x and z must be given together.");
            }

            // ---- optional: toggle edge scrolling (the ergonomics fix) ------------
            string edgeScrollError = null;
            if (HasField(body, "edgeScroll"))
            {
                bool edgeScroll = JsonUtil.GetBool(body, "edgeScroll", false);
                edgeScrollError = SetEdgeScrolling(controller, edgeScroll);
            }

            // ---- optional: hide the UI (the game's own free-camera mode) -------------
            string hideUiError = null;
            if (HasField(body, "hideUi"))
            {
                hideUiError = SetFreeCamera(controller, JsonUtil.GetBool(body, "hideUi", false));
            }

            // ---- optional: frame a target ----------------------------------------
            string requested = "{}";
            if (hasTarget)
            {
                float currentSize = GetFloatField(controller, "m_currentSize", 0f);
                Vector2 currentAngle = GetVector2Field(controller, "m_currentAngle", Vector2.zero);

                // Unspecified parameters keep the current framing, so re-aiming at
                // another point does not spin, tilt or zoom the view unless asked for.
                float y = JsonUtil.GetNumber(body, "y", 0f);
                float distance = JsonUtil.GetNumber(body, "distance", currentSize);
                // CameraController.m_*Angle is (x = yaw, y = pitch): the game wraps x to
                // -180..180 when the player rotates and clamps y to 0..90 (top-down).
                float verticalAngle = Mathf.Clamp(JsonUtil.GetNumber(body, "verticalAngle", currentAngle.y), 0f, 90f);
                float horizontalAngle = JsonUtil.GetNumber(body, "horizontalAngle", currentAngle.x);
                bool instant = JsonUtil.GetBool(body, "instant", true);

                distance = Mathf.Clamp(distance, 100f, 4000f);

                SetTarget(controller, new Vector3(x, y, z), verticalAngle, horizontalAngle, distance, instant);

                requested = "{\"x\":" + JsonUtil.Number(x) +
                    ",\"y\":" + JsonUtil.Number(y) +
                    ",\"z\":" + JsonUtil.Number(z) +
                    ",\"distance\":" + JsonUtil.Number(distance) +
                    ",\"verticalAngle\":" + JsonUtil.Number(verticalAngle) +
                    ",\"horizontalAngle\":" + JsonUtil.Number(horizontalAngle) +
                    ",\"instant\":" + JsonUtil.Bool(instant) + "}";
            }

            if (edgeScrollError != null)
            {
                return CommandResult.Fail(edgeScrollError);
            }
            if (hideUiError != null)
            {
                return CommandResult.Fail(hideUiError);
            }

            return CommandResult.FromJson(
                "{\"ok\":true,\"action\":" + (hasTarget ? "\"target\"" : "\"state\"") +
                ",\"requested\":" + requested +
                "," + BuildCameraReadback(controller) +
                (hasTarget
                    ? ",\"note\":\""
                        + (HasField(body, "instant") && !JsonUtil.GetBool(body, "instant", true)
                            ? "The camera glides to the target over a few frames; re-read or re-project once it settles."
                            : "The camera snapped to the target; the projection is valid immediately.")
                        + "\""
                    : "") +
                "}");
        }

        // --------------------------------------------------------------- project-point

        /// <summary>
        /// POST /commands/project-point.
        ///
        /// Body: x, z (required), y (optional, default 0).
        ///
        /// Projects a world position to pixels. The response carries three coordinate
        /// spaces: Unity screen coordinates (origin bottom-left, the raw projection),
        /// client-window coordinates (origin top-left of the game window), and the
        /// absolute desktop cursor position a CUA channel can click at — plus
        /// onScreen / inFront flags. Re-project after every camera move.
        /// </summary>
        public static CommandResult ProjectPoint(string body)
        {
            Camera unityCamera = GetUnityCamera();
            if (unityCamera == null)
            {
                return CommandResult.Fail("No Unity camera found. Is a city loaded?");
            }

            float x = JsonUtil.GetNumber(body, "x", float.NaN);
            float z = JsonUtil.GetNumber(body, "z", float.NaN);
            if (float.IsNaN(x) || float.IsNaN(z))
            {
                return CommandResult.Fail("x and z are required.");
            }
            float y = JsonUtil.GetNumber(body, "y", 0f);

            Vector3 world = new Vector3(x, y, z);
            Vector3 screen = unityCamera.WorldToScreenPoint(world);

            bool inFront = screen.z > 0f;
            bool onScreen = inFront &&
                screen.x >= 0f && screen.x <= Screen.width &&
                screen.y >= 0f && screen.y <= Screen.height;

            // Client-window coordinates: Unity's origin is bottom-left, a cursor's is
            // top-left, so flip y. When the Unity backbuffer and the Win32 client rect
            // disagree (DPI scaling), scale the point into client pixels first.
            int winX, winY, winWidth, winHeight;
            bool haveWindow = WindowGeometry.TryGetClientRect(
                out winX, out winY, out winWidth, out winHeight);

            string clientX = "null";
            string clientY = "null";
            string cursorX = "null";
            string cursorY = "null";

            if (haveWindow && winWidth > 0 && winHeight > 0)
            {
                float scaleX = winWidth / (float)Screen.width;
                float scaleY = winHeight / (float)Screen.height;
                float pointX = screen.x * scaleX;
                float pointY = screen.y * scaleY;

                float flippedY = winHeight - pointY;
                clientX = JsonUtil.Number(pointX);
                clientY = JsonUtil.Number(flippedY);
                cursorX = JsonUtil.Number(winX + pointX);
                cursorY = JsonUtil.Number(winY + flippedY);
            }

            return CommandResult.FromJson(
                "{\"ok\":true" +
                ",\"point\":{\"x\":" + JsonUtil.Number(x) +
                ",\"y\":" + JsonUtil.Number(y) +
                ",\"z\":" + JsonUtil.Number(z) + "}" +
                ",\"unity\":{\"x\":" + JsonUtil.Number(screen.x) +
                ",\"y\":" + JsonUtil.Number(screen.y) +
                ",\"z\":" + JsonUtil.Number(screen.z) +
                ",\"inFront\":" + JsonUtil.Bool(inFront) + "}" +
                ",\"screen\":{\"width\":" + Screen.width +
                ",\"height\":" + Screen.height +
                ",\"onScreen\":" + JsonUtil.Bool(onScreen) + "}" +
                ",\"window\":{\"x\":" + winX +
                ",\"y\":" + winY +
                ",\"width\":" + winWidth +
                ",\"height\":" + winHeight +
                ",\"known\":" + JsonUtil.Bool(haveWindow) + "}" +
                ",\"client\":{\"x\":" + clientX + ",\"y\":" + clientY + "}" +
                ",\"cursor\":{\"x\":" + cursorX + ",\"y\":" + cursorY + "}" +
                ",\"note\":\"cursor is absolute desktop pixels, origin top-left, ready for a CUA click; re-project after every camera move.\"}");
        }

        // -------------------------------------------------------------------- internals

        private static CameraController FindCameraController()
        {
            return UnityEngine.Object.FindObjectOfType<CameraController>();
        }

        private static void SetTarget(
            CameraController controller, Vector3 target,
            float verticalAngle, float horizontalAngle, float distance, bool instant)
        {
            SetVector3Field(controller, "m_targetPosition", target);
            SetVector2Field(controller, "m_targetAngle", new Vector2(horizontalAngle, verticalAngle));
            SetFloatField(controller, "m_targetSize", distance);

            if (instant)
            {
                // Snap the current state to the target and kill the inertia so the
                // framing is settled the same frame — deterministic for projection.
                SetVector3Field(controller, "m_currentPosition", target);
                SetVector2Field(controller, "m_currentAngle", new Vector2(horizontalAngle, verticalAngle));
                SetFloatField(controller, "m_currentSize", distance);
                SetVector3Field(controller, "m_velocity", Vector3.zero);
                SetVector2Field(controller, "m_angleVelocity", Vector2.zero);
                SetFloatField(controller, "m_zoomVelocity", 0f);
            }
        }

        private static string BuildCameraReadback(CameraController controller)
        {
            Vector3 position = GetVector3Field(controller, "m_currentPosition", Vector3.zero);
            Vector3 target = GetVector3Field(controller, "m_targetPosition", Vector3.zero);
            Vector2 angle = GetVector2Field(controller, "m_currentAngle", Vector2.zero);
            float size = GetFloatField(controller, "m_currentSize", 0f);

            return "\"camera\":{\"position\":{\"x\":" + JsonUtil.Number(position.x) +
                ",\"y\":" + JsonUtil.Number(position.y) +
                ",\"z\":" + JsonUtil.Number(position.z) + "}" +
                ",\"target\":{\"x\":" + JsonUtil.Number(target.x) +
                ",\"y\":" + JsonUtil.Number(target.y) +
                ",\"z\":" + JsonUtil.Number(target.z) + "}" +
                ",\"angle\":{\"vertical\":" + JsonUtil.Number(angle.y) +
                ",\"horizontal\":" + JsonUtil.Number(angle.x) + "}" +
                ",\"distance\":" + JsonUtil.Number(size) +
                ",\"edgeScrolling\":" + ReadEdgeScrolling(controller) +
                ",\"uiHidden\":" + ReadFreeCamera(controller) + "}" +
                ",\"screen\":{\"width\":" + Screen.width +
                ",\"height\":" + Screen.height + "}";
        }

        /// <summary>
        /// The game's render camera. CameraController owns it; its m_camera field is
        /// private, so reflect it. Unity's Camera.main is the fallback.
        /// </summary>
        private static Camera GetUnityCamera()
        {
            CameraController controller = FindCameraController();
            if (controller != null)
            {
                Camera camera = GetField(controller, "m_camera") as Camera;
                if (camera != null)
                {
                    return camera;
                }
            }

            return Camera.main;
        }

        private static bool HasField(string body, string name)
        {
            return body != null && body.IndexOf("\"" + name + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------------ reflection
        // CameraController's camera state is private fields; one small reflector each,
        // all failing soft to zero so a future game patch degrades to a wrong-but-valid
        // answer instead of an exception.

        private static object GetField(CameraController controller, string name)
        {
            FieldInfo field = typeof(CameraController).GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field != null ? field.GetValue(controller) : null;
        }

        private static float GetFloatField(CameraController controller, string name, float fallback)
        {
            object value = GetField(controller, name);
            return value is float ? (float)value : fallback;
        }

        private static Vector3 GetVector3Field(CameraController controller, string name, Vector3 fallback)
        {
            object value = GetField(controller, name);
            return value is Vector3 ? (Vector3)value : fallback;
        }

        private static Vector2 GetVector2Field(CameraController controller, string name, Vector2 fallback)
        {
            object value = GetField(controller, name);
            return value is Vector2 ? (Vector2)value : fallback;
        }

        private static void SetFloatField(CameraController controller, string name, float value)
        {
            FieldInfo field = typeof(CameraController).GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field != null)
            {
                field.SetValue(controller, value);
            }
        }

        private static void SetVector2Field(CameraController controller, string name, Vector2 value)
        {
            FieldInfo field = typeof(CameraController).GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field != null)
            {
                field.SetValue(controller, value);
            }
        }

        private static void SetVector3Field(CameraController controller, string name, Vector3 value)
        {
            FieldInfo field = typeof(CameraController).GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field != null)
            {
                field.SetValue(controller, value);
            }
        }

        /// <summary>
        /// Toggles the game's edge-scrolling setting through the same SavedBool the
        /// game's own CameraController reads every frame — the camera stops drifting
        /// immediately, no restart needed. Returns null on success, an error otherwise.
        /// </summary>
        private static string SetEdgeScrolling(CameraController controller, bool value)
        {
            FieldInfo field = typeof(CameraController).GetField(
                "m_edgeScrolling", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
            {
                return "CameraController.m_edgeScrolling not found (game version changed?).";
            }

            object saved = field.GetValue(controller);
            PropertyInfo valueProperty = field.FieldType.GetProperty(
                "value", BindingFlags.Instance | BindingFlags.Public);
            if (saved == null || valueProperty == null)
            {
                return "Could not reach the edgeScrolling setting.";
            }

            try
            {
                valueProperty.SetValue(saved, value, null);
            }
            catch (Exception ex)
            {
                return "Failed to set edge scrolling: " + ex.Message;
            }

            return null;
        }

        /// <summary>
        /// Enters or leaves the game's free-camera mode by writing the same public field
        /// the game's "hide UI" key toggles. CameraController.UpdateFreeCamera notices the
        /// change on its next frame and does the rest itself: UIView.Show(false),
        /// notification icons, district names, area borders and road names off - and back
        /// on when cleared. Returns null on success, an error otherwise.
        /// </summary>
        private static string SetFreeCamera(CameraController controller, bool hidden)
        {
            FieldInfo field = typeof(CameraController).GetField(
                "m_freeCamera", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null || field.FieldType != typeof(bool))
            {
                return "CameraController.m_freeCamera not found (game version changed?).";
            }
            field.SetValue(controller, hidden);
            return null;
        }

        /// <summary>Whether free-camera mode (UI hidden) is on, or null if unknown.</summary>
        private static string ReadFreeCamera(CameraController controller)
        {
            object value = GetField(controller, "m_freeCamera");
            return value is bool ? JsonUtil.Bool((bool)value) : "null";
        }

        /// <summary>The current edge-scrolling state, or null if it cannot be read.</summary>
        private static string ReadEdgeScrolling(CameraController controller)
        {
            FieldInfo field = typeof(CameraController).GetField(
                "m_edgeScrolling", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
            {
                return "null";
            }

            object saved = field.GetValue(controller);
            PropertyInfo valueProperty = field.FieldType.GetProperty(
                "value", BindingFlags.Instance | BindingFlags.Public);
            if (saved == null || valueProperty == null)
            {
                return "null";
            }

            try
            {
                object value = valueProperty.GetValue(saved, null);
                return value is bool && (bool)value ? "true" : "false";
            }
            catch (Exception)
            {
                return "null";
            }
        }
    }
}
