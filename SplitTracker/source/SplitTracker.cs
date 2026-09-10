using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Sotn;

public sealed class SplitTrackerMod : IMod
{
    private const string Prefix = "mods.SplitTracker.";
    private const uint FinalDraculaAddress = 0x8003CA98;

    private bool modEnabled = true;
    private bool showOverlay = true;

    private float totalElapsedSeconds = 0f;
    private string currentFormattedTime = "00:00:00.00";
    private bool isTimerRunning = false;
    private bool sessionInitialized = false;
    private bool hadActiveRun = false;
    private bool scrollToBottom = false;

    private readonly List<SplitRecord> recordedSplits = new();
    private readonly HashSet<string> defeatedSplitses = new();

    private readonly Dictionary<string, float> bestSplits = new();
    private float bestTotalTime = float.MaxValue;
    private bool hasBestTime = false;

    private IMemory? mem;

    private IMemory? Mem => mem ??= RecompOne.Runtime.Runtime.Mem;

    private string TimesDirectory => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "mods", "SplitTracker", "times");

    private SplitTrackerPanel? timerPanel;

    private sealed class SplitRecord
    {
        public string SplitsName { get; set; } = "";
        public string SplitTime { get; set; } = "";
        public float SplitSeconds { get; set; }
        public float? DifferenceSeconds { get; set; }
    }

    private sealed class SplitTrackerPanel : IFloatingPanel
    {
        private readonly SplitTrackerMod owner;

        public SplitTrackerPanel(SplitTrackerMod owner)
        {
            this.owner = owner;
        }

        public string Name => "SplitTrackerOverlay";
        public string TitleKey => "Splits Timer";

        public bool IsOpen
        {
            get => owner.modEnabled && owner.showOverlay;
            set
            {
                owner.showOverlay = value;
                owner.Save();
            }
        }

        public void Draw()
        {
            owner.DrawTimerWindow();
        }
    }

    public void OnLoad()
    {
        Load();

        timerPanel = new SplitTrackerPanel(this);
        PanelManager.Register(timerPanel);
        Event.AddListener<VSyncEvent>(OnVSync);
    }

    public void OnUnload()
    {
        Event.RemoveListener<VSyncEvent>(OnVSync);
        timerPanel = null;
    }

    public void DrawSettings()
    {
        if (ImGui.Checkbox("Enable Mod", ref modEnabled))
        {
            if (!modEnabled)
                showOverlay = false;
            Save();
        }

        if (!modEnabled)
            return;

        ImGui.Separator();

        if (ImGui.Checkbox("Show Split Tracker Window", ref showOverlay))
            Save();

        ImGui.Separator();
        ImGui.TextDisabled("Times folder:");
        ImGui.TextDisabled(TimesDirectory);
    }

    private void OnVSync(VSyncEvent e)
    {
        if (!modEnabled || !Game.Available)
            return;

        if (hadActiveRun &&
            (Game.State == GameState.MainMenu ||
             Game.State == GameState.Title ||
             Game.State == GameState.Boot))
        {
            ResetRun();
            return;
        }

        if (!sessionInitialized && Game.InGame)
            InitializeSession();

        if (isTimerRunning)
        {
            totalElapsedSeconds += 1f / 60f;
            currentFormattedTime = FormatTime(totalElapsedSeconds);
        }

        if (Game.InGame && isTimerRunning)
            CheckSplitsProgress();
    }

    private void InitializeSession()
    {
        sessionInitialized = isTimerRunning = hadActiveRun = true;

        LoadBestTime();

        if (Progress.IsDefeated(TimeAttackEvent.DraculaDefeat))
            defeatedSplitses.Add(TimeAttackEvent.DraculaDefeat.ToString());

        foreach (var boss in Progress.Bosses)
        {
            if (!Progress.IsDefeated(boss))
                continue;

            defeatedSplitses.Add(boss.ToString());
        }
    }

    private void DrawTimerWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(310f, 310f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(15f, 55f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(1f);

        bool open = showOverlay;

        if (ImGui.Begin("Split Tracker###SplitsTimerWindow", ref open))
        {
            ImGui.Text("TIME");

            ImGui.SetWindowFontScale(1.5f);
            ImGui.Text(currentFormattedTime);
            ImGui.SetWindowFontScale(1.0f);

            ImGui.SameLine();
            string buttonText = "Reset Timer";
            float buttonWidth = ImGui.CalcTextSize(buttonText).X + ImGui.GetStyle().FramePadding.X * 2.0f;
            float rightX = ImGui.GetContentRegionMax().X - buttonWidth;

            if (ImGui.GetCursorPosX() < rightX)
                ImGui.SetCursorPosX(rightX);

            if (ImGui.Button(buttonText))
                ResetRun();

            if (hasBestTime)
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), $"Target PB: {FormatTime(bestTotalTime)}");

            ImGui.Separator();
            ImGui.Text("SPLITS");

            ImGui.BeginChild("SplitsScroll", new Vector2(0f, 150f));

            if (recordedSplits.Count == 0)
            {
                ImGui.TextDisabled("No split yet");
            }
            else if (ImGui.BeginTable(
                "SplitsSplitsTable",
                3,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("Diff", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableHeadersRow();

                foreach (SplitRecord split in recordedSplits)
                {
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(split.SplitsName.Replace("Defeat", ""));

                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text(split.SplitTime);

                    ImGui.TableSetColumnIndex(2);
                    if (split.DifferenceSeconds.HasValue)
                    {
                        float diff = split.DifferenceSeconds.Value;
                        string diffText = diff >= 0 ? $"+{diff:F1}" : $"{diff:F1}";
                        Vector4 color = diff < 0
                            ? new Vector4(0.2f, 1.0f, 0.2f, 1.0f)
                            : new Vector4(1.0f, 0.2f, 0.2f, 1.0f);
                        ImGui.TextColored(color, diffText);
                    }
                    else
                        ImGui.TextDisabled("--");
                }

                ImGui.EndTable();
            }

            if (scrollToBottom)
            {
                ImGui.SetScrollHereY(1.0f); // Scroll to the just added split entry
                scrollToBottom = false;
            }

            ImGui.EndChild();
        }

        ImGui.End();

        if (open != showOverlay)
        {
            showOverlay = open;
            Save();
        }
    }

    private static string FormatTime(float seconds)
    {
        TimeSpan time = TimeSpan.FromSeconds(Math.Max(0f, seconds));
        return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds / 10:D2}";
    }

    private void CheckSplitsProgress()
    {
        CheckBoss(TimeAttackEvent.DraculaDefeat);

        foreach (TimeAttackEvent boss in Progress.Bosses)
        {
            CheckBoss(boss);
        }

        // Register split only if FinalDracula isn't already triggered, mem is not null and adress is not 0
        if (!defeatedSplitses.Contains("FinalDracula") &&
            Mem is {} memory &&
            memory.ReadU32(FinalDraculaAddress) != 0)
        {
            RegisterSplit("FinalDracula");
        }
    }

    private void CheckBoss(TimeAttackEvent boss)
    {
        string name = boss.ToString();

        if (defeatedSplitses.Contains(name))
            return;

        if (!Progress.IsDefeated(boss))
            return;

        RegisterSplit(name);
    }

    private void RegisterSplit(string splitName)
    {
        if (!defeatedSplitses.Add(splitName))
            return;

        float? diff = null;
        if (hasBestTime && bestSplits.TryGetValue(splitName, out float bestSplitSeconds))
        {
            diff = totalElapsedSeconds - bestSplitSeconds;
        }

        recordedSplits.Add(new SplitRecord
        {
            SplitsName = splitName,
            SplitTime = currentFormattedTime,
            SplitSeconds = totalElapsedSeconds,
            DifferenceSeconds = diff
        });

        scrollToBottom = true;

        if (splitName == "FinalDracula")
        {
            SaveRunToFile();
            isTimerRunning = false;
        }
    }

    private void ResetRun()
    {
        totalElapsedSeconds = 0f;
        currentFormattedTime = "00:00:00.00";
        recordedSplits.Clear();
        defeatedSplitses.Clear();
        sessionInitialized = isTimerRunning = hadActiveRun = false;
        mem = null;
    }

    private void SaveRunToFile()
    {
        try
        {
            if (!Directory.Exists(TimesDirectory))
                Directory.CreateDirectory(TimesDirectory);

            string safeTime = currentFormattedTime.Replace(":", "-");
            string path = Path.Combine(TimesDirectory, $"{safeTime}.txt");

            using StreamWriter writer = new StreamWriter(path);
            writer.WriteLine($"Total={totalElapsedSeconds}");
            foreach (var split in recordedSplits)
                writer.WriteLine($"{split.SplitsName}={split.SplitSeconds}");
        }
        catch (Exception err) { Console.WriteLine($"[SplitsTimer] Failed to save file: {err.Message}"); }
    }
    
    private void LoadBestTime()
    {
        bestSplits.Clear();
        hasBestTime = false;
        bestTotalTime = float.MaxValue;

        if (!Directory.Exists(TimesDirectory))
            return;

        string? bestFile = null;

        foreach (string file in Directory.GetFiles(TimesDirectory, "*.txt"))
        {
            try
            {
                string? firstLine = File.ReadLines(file).FirstOrDefault(l => l.StartsWith("Total="));
                if (firstLine != null && float.TryParse(firstLine.Substring(6), out float total))
                {
                    if (total < bestTotalTime)
                    {
                        bestTotalTime = total;
                        bestFile = file;
                    }
                }
            }
            catch (Exception err) { Console.WriteLine($"[SplitsTimer] Failed to read file: {err.Message}"); }
        }

        if (bestFile == null)
            return;

        try
        {
            foreach (string line in File.ReadLines(bestFile))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq);
                string val = line.Substring(eq + 1);

                if (key != "Total" && float.TryParse(val, out float splitTime))
                    bestSplits[key] = splitTime;
            }

            hasBestTime = true;
        }
        catch (Exception err) { Console.WriteLine($"[SplitsTimer] Failed to load file: {err.Message}"); }
    }

    private void Save()
    {
        var view = Runtime.View;
        view.SetBool(Prefix + "enabled", modEnabled);
        view.SetBool(Prefix + "overlay", showOverlay);
        Runtime.SaveView();
    }

    private void Load()
    {
        var view = Runtime.View;
        modEnabled = view.GetBool(Prefix + "enabled", true);
        showOverlay = view.GetBool(Prefix + "overlay", true);
    }
}