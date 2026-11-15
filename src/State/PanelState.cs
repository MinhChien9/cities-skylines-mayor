using System;
using System.Collections.Generic;
using System.Text;
using ColossalFramework.UI;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    /// <summary>
    /// The game's own pop-up panels that interrupt play: the milestone "Congratulations"
    /// panel, scenario messages (won, lost, rewards, fines), the park-level panel, the
    /// bail-out offer and the pause menu. All but the scenario messages force-pause the
    /// simulation (SimulationManager.ForcedSimulationPaused) until they close.
    ///
    /// UI belongs to the Unity main thread. <see cref="Refresh"/> runs there every few
    /// frames and keeps a JSON snapshot, so simulation-thread reads (/state/summary) can
    /// report open panels without touching UI. <see cref="Close"/> runs there too (through
    /// the main-thread queue) and closes a panel the way the game's Escape key does
    /// (GameKeyShortcuts.Escape / EscapeFromModal and each panel's own key handler).
    /// </summary>
    public static class PanelState
    {
        public const string Milestone = "UnlockingPanel";
        public const string Story = "StoryMessagePanel";
        public const string ParkLevel = "ParkAreaUnlockingPanel";
        public const string Bailout = "BailoutPanel";
        public const string PauseMenu = "PauseMenu";

        private const int RefreshEveryFrames = 15;

        /// <summary>A scenario message panel shows buffered messages one after another.</summary>
        private const int MaxStoryMessages = 20;

        private static int frame;
        private static volatile string snapshotJson = "[]";
        private static volatile string clockHolder = "";

        /// <summary>The open panels as a JSON array, as of the last refresh (at most ~15 frames old).</summary>
        public static string SnapshotJson
        {
            get { return snapshotJson; }
        }

        /// <summary>Name of an open panel that force-pauses the clock, or "" when none is open.</summary>
        public static string ClockHolder
        {
            get { return clockHolder; }
        }

        /// <summary>Called every frame on the main thread while a city is loaded.</summary>
        public static void Tick()
        {
            frame++;
            if (frame % RefreshEveryFrames == 0)
            {
                Refresh();
            }
        }

        /// <summary>Re-reads the open panels. Main thread only.</summary>
        public static void Refresh()
        {
            try
            {
                string holder;
                snapshotJson = BuildOpenPanelsJson(out holder);
                clockHolder = holder;
            }
            catch (Exception)
            {
                // The UI is being torn down or rebuilt (level unload); keep the last snapshot.
            }
        }

        /// <summary>
        /// POST /commands/close-panel, on the main thread. Closes the named panel, or every
        /// open known panel when <paramref name="body"/> names none.
        /// </summary>
        public static CommandResult Close(string body)
        {
            string wanted = JsonUtil.GetString(body, "panel", "").Trim();
            if (wanted.Length > 0 && !IsKnown(wanted))
            {
                return CommandResult.Fail("panel must be one of: " + Milestone + ", " + Story + ", " + ParkLevel + ", " + Bailout + ", " + PauseMenu + " (or omitted to close every open one)");
            }

            List<string> closed = new List<string>();
            List<string> errors = new List<string>();
            // Modal panels first, top of the stack first: the pause menu can sit above the others.
            TryClose(wanted, PauseMenu, closed, errors);
            TryClose(wanted, Bailout, closed, errors);
            TryClose(wanted, Milestone, closed, errors);
            TryClose(wanted, ParkLevel, closed, errors);
            TryClose(wanted, Story, closed, errors);

            string holder;
            string open = BuildOpenPanelsJson(out holder);
            snapshotJson = open;
            clockHolder = holder;

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":").Append(JsonUtil.Bool(errors.Count == 0));
            json.Append(",\"closed\":[").Append(string.Join(",", closed.ToArray())).Append("]");
            json.Append(",\"openPanels\":").Append(open);
            if (errors.Count > 0)
            {
                json.Append(",\"errors\":[");
                for (int i = 0; i < errors.Count; i++)
                {
                    json.Append(i == 0 ? "" : ",").Append("\"").Append(JsonUtil.Escape(errors[i])).Append("\"");
                }
                json.Append("]");
            }
            if (closed.Count == 0 && errors.Count == 0)
            {
                json.Append(",\"hint\":\"No such panel is open.\"");
            }
            json.Append(",\"notes\":\"The milestone panel sets the speed to 1 when it closes (the game's own behaviour); set the speed again afterwards. Closing the bail-out offer declines it.\"}");
            return CommandResult.FromJson(json.ToString());
        }

        private static bool IsKnown(string name)
        {
            return name == Milestone || name == Story || name == ParkLevel || name == Bailout || name == PauseMenu;
        }

        private static void TryClose(string wanted, string name, List<string> closed, List<string> errors)
        {
            if (wanted.Length > 0 && wanted != name)
            {
                return;
            }
            try
            {
                CloseOne(name, closed);
            }
            catch (Exception ex)
            {
                errors.Add(name + ": " + ex.Message);
            }
        }

        private static void CloseOne(string name, List<string> closed)
        {
            if (name == Story)
            {
                StoryMessagePanel story = LibraryPanel<StoryMessagePanel>(Story);
                // Closing shows the next buffered message, if any; read each before closing it.
                for (int i = 0; i < MaxStoryMessages && story != null && story.component.isVisible; i++)
                {
                    closed.Add(DescribeStory(story));
                    story.OnCloseButton();
                }
                return;
            }

            UIComponent panel = FindPanel(name);
            if (panel == null || !panel.isVisible)
            {
                return;
            }
            string described = Describe(name, panel);
            if (name == Milestone)
            {
                // Escape: GameKeyShortcuts presses the panel's Close button, which hides it.
                UnlockingPanel unlocking = panel.GetComponent<UnlockingPanel>();
                if (unlocking == null)
                {
                    throw new InvalidOperationException("the panel has no UnlockingPanel component");
                }
                unlocking.OnClosed();
            }
            else if (name == ParkLevel)
            {
                ParkAreaUnlockingPanel park = panel.GetComponent<ParkAreaUnlockingPanel>();
                if (park == null)
                {
                    throw new InvalidOperationException("the panel has no ParkAreaUnlockingPanel component");
                }
                park.Hide();
            }
            else if (name == Bailout)
            {
                // Escape on the bail-out offer rejects it.
                BailoutPanel bailout = panel.GetComponent<BailoutPanel>();
                if (bailout == null)
                {
                    throw new InvalidOperationException("the panel has no BailoutPanel component");
                }
                bailout.OnReject();
            }
            else if (name == PauseMenu)
            {
                if (UIView.GetModalComponent() != panel)
                {
                    throw new InvalidOperationException("another modal panel is above the pause menu");
                }
                UIView.library.Hide(PauseMenu, -1);
            }
            closed.Add(described);
        }

        /// <summary>The open panels as a JSON array; <paramref name="holder"/> is the first that holds the clock.</summary>
        private static string BuildOpenPanelsJson(out string holder)
        {
            holder = "";
            UIComponent modal = UIView.HasModalInput() ? UIView.GetModalComponent() : null;
            List<string> items = new List<string>();
            List<UIComponent> known = new List<UIComponent>();

            foreach (string name in new string[] { Milestone, ParkLevel, Bailout, PauseMenu })
            {
                UIComponent panel = FindPanel(name);
                if (panel == null)
                {
                    continue;
                }
                known.Add(panel);
                if (!panel.isVisible)
                {
                    continue;
                }
                bool holds = HoldsClock(name, panel);
                if (holds && holder.Length == 0)
                {
                    holder = name;
                }
                items.Add(Describe(name, panel, holds, panel == modal));
            }

            StoryMessagePanel story = LibraryPanel<StoryMessagePanel>(Story);
            if (story != null)
            {
                known.Add(story.component);
                if (story.component.isVisible)
                {
                    items.Add(DescribeStory(story));
                }
            }

            if (modal != null && !known.Contains(modal) && modal.isVisible)
            {
                items.Add("{\"name\":\"" + JsonUtil.Escape(modal.name) + "\",\"kind\":\"other\",\"modal\":true,\"holdsClock\":null,\"closable\":false}");
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        /// <summary>
        /// The milestone panel opened from its toolbar button is an ordinary window; the
        /// "Congratulations" version (UnlockingPanel.ShowModal) shows the Congrats sprite,
        /// is modal and force-pauses the clock.
        /// </summary>
        private static bool HoldsClock(string name, UIComponent panel)
        {
            return name != Milestone || ChildVisible(panel, "Congrats");
        }

        private static string Describe(string name, UIComponent panel)
        {
            UIComponent modal = UIView.HasModalInput() ? UIView.GetModalComponent() : null;
            return Describe(name, panel, HoldsClock(name, panel), panel == modal);
        }

        private static string Describe(string name, UIComponent panel, bool holdsClock, bool modal)
        {
            string kind;
            string title;
            if (name == Milestone)
            {
                kind = "milestone";
                UIComponent overview = panel.Find("MilestoneOverview");
                title = overview != null ? LabelText(overview, "MilestoneTitle") : "";
                title = (holdsClock ? "Milestone reached: " : "Milestones: ") + title;
            }
            else if (name == ParkLevel)
            {
                kind = "parkLevel";
                title = "Park level reached";
            }
            else if (name == Bailout)
            {
                kind = "bailout";
                title = LabelText(panel, "InfoLabel");
                if (title.Length == 0)
                {
                    title = "Bail-out offer";
                }
            }
            else
            {
                kind = "pauseMenu";
                title = "Pause menu";
            }
            return "{\"name\":\"" + name + "\",\"kind\":\"" + kind + "\",\"title\":\"" + JsonUtil.Escape(title) +
                "\",\"modal\":" + JsonUtil.Bool(modal) + ",\"holdsClock\":" + JsonUtil.Bool(holdsClock) + ",\"closable\":true}";
        }

        private static string DescribeStory(StoryMessagePanel story)
        {
            UIComponent panel = story.component;
            string kind = "message";
            if (ChildVisible(panel, "PanelLose"))
            {
                kind = "scenarioLost";
            }
            else if (ChildVisible(panel, "PanelWin"))
            {
                kind = "scenarioWon";
            }
            else if (ChildVisible(panel, "PanelReward"))
            {
                kind = "reward";
            }
            else if (ChildVisible(panel, "PanelFine"))
            {
                kind = "fine";
            }
            string text = "";
            if (kind == "scenarioLost" || kind == "scenarioWon")
            {
                UIComponent reason = panel.Find("PanelReason");
                text = reason != null && reason.isVisible ? LabelText(reason, "MessageText") : "";
            }
            else
            {
                UIComponent message = panel.Find("PanelMessage");
                text = message != null && message.isVisible ? LabelText(message, "Label") : "";
            }
            return "{\"name\":\"" + Story + "\",\"kind\":\"" + kind + "\",\"title\":\"" + JsonUtil.Escape(LabelText(panel, "Caption")) +
                "\",\"text\":\"" + JsonUtil.Escape(text) + "\",\"modal\":false,\"holdsClock\":false,\"closable\":true}";
        }

        private static bool ChildVisible(UIComponent panel, string child)
        {
            UIComponent c = panel.Find(child);
            return c != null && c.isVisible;
        }

        private static string LabelText(UIComponent parent, string label)
        {
            UILabel l = parent.Find<UILabel>(label);
            return l != null && l.text != null ? l.text : "";
        }

        private static UIComponent FindPanel(string name)
        {
            UIComponent library = LibraryComponent(name);
            if (library != null)
            {
                return library;
            }
            // GameKeyShortcuts finds the milestone panel by name in the view.
            return name == Milestone ? UIView.Find<UIPanel>(Milestone) : null;
        }

        private static UIComponent LibraryComponent(string name)
        {
            UIDynamicPanels library = UIView.library;
            if (library == null || !library.isInitialized)
            {
                return null;
            }
            return library.Get(name);
        }

        private static T LibraryPanel<T>(string name) where T : UnityEngine.MonoBehaviour
        {
            UIComponent component = LibraryComponent(name);
            return component != null ? component.GetComponent<T>() : null;
        }
    }
}
