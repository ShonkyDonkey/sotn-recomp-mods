using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Modding;
using StbImageSharp;

public sealed class InputOverlayMod : IMod
{
    private const string Prefix = "mods.InputOverlay.";

    private int overlayWidth = 300;
    private bool isPanelRegistered = false;

    private string ImagesDirectory => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "mods", "InputOverlay", "imgs");

    private InputDisplayPanel? inputPanel;

    // Caches to prevent reloading and resizing textures from disk every frame
    private readonly Dictionary<string, TextureInfo> textureCache = new();
    private readonly Dictionary<string, TextureInfo> backgroundTextureCache = new();
    private readonly Dictionary<string, int> nativeBackgroundWidthCache = new();
    private readonly Dictionary<string, (int Width, int Height)> nativeImageSizeCache = new();

    private Dictionary<string, InputConfig> joystickConfig = new(StringComparer.OrdinalIgnoreCase);

    private sealed class TextureInfo
    {
        public uint Id { get; }
        public int Width { get; }
        public int Height { get; }

        public TextureInfo(uint id, int width, int height)
        {
            Id = id;
            Width = width;
            Height = height;
        }
    }

    public class InputConfig
    {
        public JsonElement X { get; set; }
        public JsonElement Y { get; set; }
        public string ImageName { get; set; } = "";
    }

    // Connects our drawing logic to the engine's panel manager
    private sealed class InputDisplayPanel : IFloatingPanel
    {
        private readonly InputOverlayMod owner;

        public InputDisplayPanel(InputOverlayMod owner)
        {
            this.owner = owner;
        }

        public string Name => "InputDisplayOverlay";
        public string TitleKey => "Input Display";

        // Required by the IFloatingPanel interface. 
        // Hardcoded to true so the engine always attempts to draw it.
        public bool IsOpen { get; set; } = true;

        public void Draw()
        {
            if (!IsOpen)
                return;

            owner.DrawInputWindow();
        }
    }

    public void OnLoad()
    {
        Load();
        LoadJoystickConfig();

        inputPanel = new InputDisplayPanel(this);
        
        // Defer panel registration to the main thread's first frame to ensure the host UI is ready
        Event.AddListener<VSyncEvent>(RegisterPanelOnMainThread);
    }

    public void OnUnload()
    {
        // Safety catch in case the mod is unloaded before the first frame fires
        Event.RemoveListener<VSyncEvent>(RegisterPanelOnMainThread);

        if (inputPanel != null)
        {
            SafeUnregisterPanel(inputPanel);
            inputPanel = null;
        }

        textureCache.Clear();
        backgroundTextureCache.Clear();
        nativeBackgroundWidthCache.Clear();
        nativeImageSizeCache.Clear();
    }

    private void RegisterPanelOnMainThread(VSyncEvent e)
    {
        if (isPanelRegistered || inputPanel == null) return;
        
        PanelManager.Register(inputPanel);
        isPanelRegistered = true;
        
        // Clean up the listener immediately after registration so it only runs once
        Event.RemoveListener<VSyncEvent>(RegisterPanelOnMainThread);
    }

    private static void SafeUnregisterPanel(IFloatingPanel panel)
    {
        try
        {
            Type managerType = typeof(PanelManager);
            MethodInfo? unregister = managerType.GetMethod("Unregister", BindingFlags.Public | BindingFlags.Static);
            unregister?.Invoke(null, new object[] { panel });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[InputOverlay] Failed to unregister panel: {ex.Message}");
        }
    }

    // Renders the mod's configuration options in the mod settings menu
    public void DrawSettings()
    {
        int newWidth = overlayWidth;
        if (ImGui.InputInt("Overlay Width", ref newWidth, 10, 50))
        {
            newWidth = Math.Clamp(newWidth, 32, 4096);

            if (newWidth != overlayWidth)
            {
                overlayWidth = newWidth;
                Save();
            }
        }

        ImGui.TextDisabled("Height is calculated automatically from background aspect ratio.");
        ImGui.Separator();
        ImGui.TextDisabled("Images folder:");
        ImGui.TextDisabled(ImagesDirectory + Path.DirectorySeparatorChar + "joystick");
    }

    // Core drawing loop for the actual on-screen overlay
    private void DrawInputWindow()
    {
        try
        {
            const string folder = "joystick";
            const string backgroundName = "background.png";

            TextureInfo? background = GetResizedBackgroundTexture(
                Path.Combine(folder, backgroundName),
                overlayWidth);

            // Determine the window size based on the background image ratio
            Vector2 windowSize;
            if (background != null && background.Width > 0 && background.Height > 0)
            {
                windowSize = new Vector2(background.Width, background.Height);
            }
            else
            {
                windowSize = new Vector2(overlayWidth, 200f);
            }

            // Setup a borderless, transparent, non-interactive ImGui window
            ImGui.SetNextWindowDockID(0, ImGuiCond.Always);
            ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);
            ImGui.SetNextWindowPos(new Vector2(15f, 380f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowBgAlpha(0f);

            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

            if (ImGui.Begin(
                "Input Display###InputOverlayWindow",
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoDecoration |
                ImGuiWindowFlags.NoDocking))
            {
                Vector2 cursorStart = ImGui.GetCursorScreenPos();
                var drawList = ImGui.GetWindowDrawList();

                Vector2 drawSize = background != null
                    ? new Vector2(background.Width, background.Height)
                    : Vector2.Zero;

                float scale = 1f;

                if (background != null && background.Width > 0)
                {
                    float nativeWidth = GetNativeBackgroundWidth(folder, backgroundName);
                    scale = overlayWidth / nativeWidth;
                }

                // Draw the controller background first
                if (background != null && background.Id != 0)
                {
                    drawList.AddImage(
                        (nint)background.Id,
                        cursorStart,
                        cursorStart + drawSize);
                }

                // Overlay active button presses on top of the background
                DrawMappedPsxInputs(drawList, cursorStart, folder, scale, windowSize);
            }

            ImGui.End();
            ImGui.PopStyleVar(2);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[InputOverlay] Draw error suppressed: {ex.Message}");
        }
    }

    // Maps standard controller inputs to visual representations
    private void DrawMappedPsxInputs(ImDrawListPtr drawList, Vector2 origin, string folder, float scale, Vector2 bgSize)
    {
        DrawPsxIfPressed(drawList, origin, folder, "GamepadDpadUp", Controller.Up, scale, bgSize);
        DrawPsxIfPressed(drawList, origin, folder, "GamepadDpadDown", Controller.Down, scale, bgSize);
        DrawPsxIfPressed(drawList, origin, folder, "GamepadDpadLeft", Controller.Left, scale, bgSize);
        DrawPsxIfPressed(drawList, origin, folder, "GamepadDpadRight", Controller.Right, scale, bgSize);

        DrawPsxIfPressed(drawList, origin, folder, "GamepadFaceUp", Controller.Triangle, scale, bgSize);
        DrawPsxIfPressed(drawList, origin, folder, "GamepadFaceDown", Controller.Cross, scale, bgSize);
        DrawPsxIfPressed(drawList, origin, folder, "GamepadFaceLeft", Controller.Square, scale, bgSize);
        DrawPsxIfPressed(drawList, origin, folder, "GamepadFaceRight", Controller.Circle, scale, bgSize);

        DrawPsxIfPressed(drawList, origin, folder, "GamepadL1", Controller.L1, scale, bgSize);
        DrawPsxIfPressed(drawList, origin, folder, "GamepadR1", Controller.R1, scale, bgSize);
        DrawPsxIfPressed(drawList, origin, folder, "GamepadL2", Controller.L2, scale, bgSize);
        DrawPsxIfPressed(drawList, origin, folder, "GamepadR2", Controller.R2, scale, bgSize);
        DrawPsxIfPressed(drawList, origin, folder, "GamepadStart", Controller.Start, scale, bgSize);
        DrawPsxIfPressed(drawList, origin, folder, "GamepadBack", Controller.Select, scale, bgSize);
    }

    private void DrawPsxIfPressed(ImDrawListPtr drawList, Vector2 origin, string folder, string configName, ushort psxBit, float scale, Vector2 bgSize)
    {
        // Skip drawing if the button is currently released
        if ((Controller.State & psxBit) != 0)
            return;

        if (!joystickConfig.TryGetValue(configName, out InputConfig? entry))
            return;

        DrawConfiguredInput(drawList, origin, folder, entry, scale, bgSize);
    }

    private void DrawConfiguredInput(ImDrawListPtr drawList, Vector2 origin, string folder, InputConfig entry, float scale, Vector2 bgSize)
    {
        if (string.IsNullOrWhiteSpace(entry.ImageName))
            return;

        TextureInfo? pressTexture = GetResizedTexture(
            Path.Combine(folder, entry.ImageName),
            scale,
            scale);
            
        if (pressTexture == null || pressTexture.Id == 0)
            return;

        // Calculate final positions based on whether the config uses raw pixels or percentages
        (float xValue, bool xPixels) = ParsePosition(entry.X);
        (float yValue, bool yPixels) = ParsePosition(entry.Y);

        float x = xPixels
            ? MathF.Round(xValue * scale)
            : MathF.Round(xValue * bgSize.X);

        float y = yPixels
            ? MathF.Round(yValue * scale)
            : MathF.Round(yValue * bgSize.Y);

        Vector2 pos = origin + new Vector2(x, y);
        Vector2 end = pos + new Vector2(pressTexture.Width, pressTexture.Height);

        drawList.AddImage((nint)pressTexture.Id, pos, end);
    }

    // Handles loading and resizing the background image efficiently
    private TextureInfo? GetResizedBackgroundTexture(string relativePath, int targetWidth)
    {
        targetWidth = Math.Clamp(targetWidth, 32, 4096);
        string cacheKey = relativePath + "@width=" + targetWidth;

        if (backgroundTextureCache.TryGetValue(cacheKey, out TextureInfo? cached))
            return cached;

        string fullPath = Path.Combine(ImagesDirectory, relativePath);

        if (!File.Exists(fullPath))
        {
            Console.WriteLine($"[InputOverlay] Background image not found: {fullPath}");
            return null;
        }

        try
        {
            byte[] png = File.ReadAllBytes(fullPath);
            ImageResult image = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);

            if (image == null || image.Width <= 0 || image.Height <= 0)
                return null;

            // If target width is larger than native, just use the native texture and let ImGui scale it
            if (targetWidth >= image.Width)
            {
                uint texture = HostWindow.UploadTexture(image.Data, image.Width, image.Height);
                if (texture == 0) return null;

                var nativeInfo = new TextureInfo(texture, image.Width, image.Height);
                backgroundTextureCache[cacheKey] = nativeInfo;
                return nativeInfo;
            }

            // Downscale using Lanczos resampling for better quality
            float ratio = targetWidth / (float)image.Width;
            int targetHeight = Math.Max(1, (int)MathF.Round(image.Height * ratio));

            byte[] resized = LanczosResizer.ResizeRgbaLanczos3(image.Data, image.Width, image.Height, targetWidth, targetHeight);
            uint resizedTexture = HostWindow.UploadTexture(resized, targetWidth, targetHeight);

            if (resizedTexture == 0) return null;

            var info = new TextureInfo(resizedTexture, targetWidth, targetHeight);
            backgroundTextureCache[cacheKey] = info;
            return info;
        }
        catch (Exception err)
        {
            Console.WriteLine($"[InputOverlay] Failed to resize background '{fullPath}': {err.Message}");
            return null;
        }
    }

    private int GetNativeBackgroundWidth(string folder, string backgroundName)
    {
        string relativePath = Path.Combine(folder, backgroundName);

        if (nativeBackgroundWidthCache.TryGetValue(relativePath, out int cachedWidth))
            return cachedWidth;

        string fullPath = Path.Combine(ImagesDirectory, relativePath);

        try
        {
            if (File.Exists(fullPath))
            {
                byte[] png = File.ReadAllBytes(fullPath);
                ImageResult image = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);

                if (image != null && image.Width > 0)
                {
                    nativeBackgroundWidthCache[relativePath] = image.Width;
                    return image.Width;
                }
            }
        }
        catch { }

        return overlayWidth;
    }

    private void LoadJoystickConfig()
    {
        joystickConfig = LoadConfig(
            Path.Combine(ImagesDirectory, "joystick", "positions.json"));
    }

    private static Dictionary<string, InputConfig> LoadConfig(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                Console.WriteLine($"[InputOverlay] Config not found: {configPath}");
                return new();
            }

            string json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<Dictionary<string, InputConfig>>(json) 
                   ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception err)
        {
            Console.WriteLine($"[InputOverlay] Failed to load config '{configPath}': {err.Message}");
            return new();
        }
    }

    // Handles parsing position values from the config, stripping "px" or "dp" suffixes if present
    private static (float Value, bool Pixels) ParsePosition(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return (value.GetSingle(), false);

        if (value.ValueKind != JsonValueKind.String)
            return (0f, false);

        string text = value.GetString()?.Trim() ?? "";
        bool pixels = text.EndsWith("px", StringComparison.OrdinalIgnoreCase);

        if (pixels || text.EndsWith("dp", StringComparison.OrdinalIgnoreCase))
            text = text[..^2].Trim();

        return (float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float result)
            ? (result, pixels) : (0f, false));
    }

    private TextureInfo? GetResizedTexture(string relativePath, float scaleX, float scaleY)
    {
        string fullPath = Path.Combine(ImagesDirectory, relativePath);

        if (!File.Exists(fullPath))
            return null;

        try
        {
            if (!nativeImageSizeCache.TryGetValue(relativePath, out (int Width, int Height) nativeSize))
            {
                byte[] sourcePng = File.ReadAllBytes(fullPath);
                ImageResult sourceImage = ImageResult.FromMemory(sourcePng, ColorComponents.RedGreenBlueAlpha);

                if (sourceImage == null || sourceImage.Width <= 0 || sourceImage.Height <= 0)
                    return null;

                nativeSize = (sourceImage.Width, sourceImage.Height);
                nativeImageSizeCache[relativePath] = nativeSize;
            }

            int targetWidth = Math.Max(1, (int)MathF.Round(nativeSize.Width * scaleX));
            int targetHeight = Math.Max(1, (int)MathF.Round(nativeSize.Height * scaleY));

            if (targetWidth >= nativeSize.Width && targetHeight >= nativeSize.Height)
                return GetTexture(relativePath);

            string cacheKey = relativePath + "@" + targetWidth + "x" + targetHeight;

            if (backgroundTextureCache.TryGetValue(cacheKey, out TextureInfo? cached))
                return cached;

            byte[] png = File.ReadAllBytes(fullPath);
            ImageResult image = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);

            if (image == null || image.Width <= 0 || image.Height <= 0)
                return null;

            byte[] resized = LanczosResizer.ResizeRgbaLanczos3(
                image.Data, image.Width, image.Height, targetWidth, targetHeight);

            uint texture = HostWindow.UploadTexture(resized, targetWidth, targetHeight);

            if (texture == 0) return null;

            var info = new TextureInfo(texture, targetWidth, targetHeight);
            backgroundTextureCache[cacheKey] = info;
            return info;
        }
        catch (Exception err)
        {
            Console.WriteLine($"[InputOverlay] Failed to resize image '{fullPath}': {err.Message}");
            return null;
        }
    }

    private TextureInfo? GetTexture(string relativePath)
    {
        if (textureCache.TryGetValue(relativePath, out TextureInfo? cached))
            return cached;

        string fullPath = Path.Combine(ImagesDirectory, relativePath);

        if (!File.Exists(fullPath)) return null;

        try
        {
            byte[] png = File.ReadAllBytes(fullPath);
            ImageResult image = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);

            if (image == null || image.Width <= 0 || image.Height <= 0) return null;

            uint texture = HostWindow.UploadTexture(image.Data, image.Width, image.Height);

            if (texture == 0) return null;

            var info = new TextureInfo(texture, image.Width, image.Height);
            textureCache[relativePath] = info;
            return info;
        }
        catch (Exception err)
        {
            Console.WriteLine($"[InputOverlay] Failed to load image '{fullPath}': {err.Message}");
            return null;
        }
    }

    // Persists view state using the engine's built-in save system
    private void Save()
    {
        var view = Runtime.View;
        view.SetInt(Prefix + "overlayWidth", overlayWidth);
        Runtime.SaveView();
    }

    // Loads saved settings or defaults to the native background width
    private void Load()
    {
        var view = Runtime.View;

        int defaultWidth = 300;
        try
        {
            string backgroundPath = Path.Combine(ImagesDirectory, "joystick", "background.png");

            if (File.Exists(backgroundPath))
            {
                byte[] png = File.ReadAllBytes(backgroundPath);
                ImageResult image = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);
                if (image != null && image.Width > 0) defaultWidth = image.Width;
            }
        }
        catch { }

        overlayWidth = view.GetInt(Prefix + "overlayWidth", defaultWidth);
        overlayWidth = Math.Clamp(overlayWidth, 32, 4096);
    }
}