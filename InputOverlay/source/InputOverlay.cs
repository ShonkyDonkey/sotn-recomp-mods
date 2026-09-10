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

    private bool showInputOverlay = true;
    private int overlayWidth = 300;

    private string ImagesDirectory => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "mods", "InputOverlay", "imgs");

    private InputDisplayPanel? inputPanel;

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

    private sealed class InputDisplayPanel : IFloatingPanel
    {
        private readonly InputOverlayMod owner;

        public InputDisplayPanel(InputOverlayMod owner)
        {
            this.owner = owner;
        }

        public string Name => "InputDisplayOverlay";
        public string TitleKey => "Input Display";

        public bool IsOpen
        {
            get => owner.showInputOverlay;
            set
            {
                owner.showInputOverlay = value;
                owner.Save();
            }
        }

        public void Draw()
        {
            if (!owner.showInputOverlay)
                return;

            owner.DrawInputWindow();
        }
    }

    public void OnLoad()
    {
        Load();
        LoadJoystickConfig();

        inputPanel = new InputDisplayPanel(this);
        SafeRegisterPanel(inputPanel);
    }

    public void OnUnload()
    {
        if (inputPanel != null)
        {
            SafeUnregisterPanel(inputPanel);
            inputPanel = null;
        }

        showInputOverlay = false;

        textureCache.Clear();
        backgroundTextureCache.Clear();
        nativeBackgroundWidthCache.Clear();
        nativeImageSizeCache.Clear();
    }

    private static void SafeRegisterPanel(IFloatingPanel panel)
    {
        PanelManager.Register(panel);
        /*Task.Run(async () =>
        {
            // Allow initial render loop startup to pass before registering
            await Task.Delay(150);

            bool registered = false;
            for (int attempt = 0; attempt < 10 && !registered; attempt++)
            {
                try
                {
                    PanelManager.Register(panel);
                    registered = true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[InputOverlay] Deferred registration attempt {attempt + 1} failed: {ex.Message}");
                    await Task.Delay(100);
                }
            }
        });*/
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

    public void DrawSettings()
    {
        if (ImGui.Checkbox("Show Input Overlay Window", ref showInputOverlay))
            Save();

        ImGui.Separator();

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

    private void DrawInputWindow()
    {
        if (!showInputOverlay)
            return;

        try
        {
            const string folder = "joystick";
            const string backgroundName = "background.png";

            TextureInfo? background = GetResizedBackgroundTexture(
                Path.Combine(folder, backgroundName),
                overlayWidth);

            Vector2 windowSize;

            if (background != null && background.Width > 0 && background.Height > 0)
            {
                windowSize = new Vector2(background.Width, background.Height);
            }
            else
            {
                windowSize = new Vector2(overlayWidth, 200f);
            }

            ImGui.SetNextWindowDockID(0, ImGuiCond.Always);
            ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);
            ImGui.SetNextWindowPos(new Vector2(15f, 380f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowBgAlpha(0f);

            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

            bool open = showInputOverlay;

            if (ImGui.Begin(
                "Input Display###InputOverlayWindow",
                ref open,
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

                if (background != null && background.Id != 0)
                {
                    drawList.AddImage(
                        (nint)background.Id,
                        cursorStart,
                        cursorStart + drawSize);
                }

                DrawMappedPsxInputs(drawList, cursorStart, folder, scale, windowSize);
            }

            ImGui.End();
            ImGui.PopStyleVar(2);

            if (open != showInputOverlay)
            {
                showInputOverlay = open;
                Save();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[InputOverlay] Draw error suppressed: {ex.Message}");
        }
    }

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

            if (targetWidth >= image.Width)
            {
                uint texture = HostWindow.UploadTexture(image.Data, image.Width, image.Height);
                if (texture == 0) return null;

                var nativeInfo = new TextureInfo(texture, image.Width, image.Height);
                backgroundTextureCache[cacheKey] = nativeInfo;
                return nativeInfo;
            }

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

    private void Save()
    {
        var view = Runtime.View;
        view.SetBool(Prefix + "inputOverlay", showInputOverlay);
        view.SetInt(Prefix + "overlayWidth", overlayWidth);
        Runtime.SaveView();
    }

    private void Load()
    {
        var view = Runtime.View;
        showInputOverlay = view.GetBool(Prefix + "inputOverlay", true);

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