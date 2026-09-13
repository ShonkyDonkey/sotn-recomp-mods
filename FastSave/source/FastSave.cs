using System;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Modding;
using RecompOne.Runtime.Memory;
using Sotn;

public sealed class FastSaveMod : IMod
{

    // Bool controlling if start menu can be open or not
    private const uint CanStartBePressedAddr = 0x8003C8B8u;
    // Random adress that siwtches from 0 to not 0 when save animation begins,
    // not sure what is it but its always true during save animation in the save room
    // which lets us know when save anim has started
    private const uint AreWeSavingAddr = 0x8007F508u;

    private static IMemory M => RecompOne.Runtime.Runtime.Mem!;

    private static bool CanStartBePressed
    {
        get => M.ReadU8(CanStartBePressedAddr) != 0;
        set => M.WriteU8(CanStartBePressedAddr, (byte)(value ? 1 : 0));
    }

    private static bool AreWeSaving => M.ReadU8(AreWeSavingAddr) != 0;

    private bool fastSaveStarted;

    public void OnLoad() => Event.AddListener<VSyncEvent>(OnVSync);

    public void OnUnload() => Event.RemoveListener<VSyncEvent>(OnVSync);

    private void OnVSync(VSyncEvent e)
    {

        // LONG LIVE MACHINE LEARNING
        // Derp baboon if you are reading this you should know you are reatarded and living in delusions
        // Unless you are using BSD which is null, your fucking OS is full of AI CODE
        // Do everyone favor and GTFAFC you hypocrite
        // I use plany of my time to deveop those mods, and very little of AI assist.
        // Casting stones is for hypocrites like you
        
        if (!Game.Available || !Game.InGame || Game.IsLoading)
        {
            fastSaveStarted = false;
            return;
        }

        bool inSaveRoom = Game.CanSave;
        bool canStartBePressed = CanStartBePressed;
        bool areWeSaving = AreWeSaving;

        // Start the fast-save override only after the save operation has
        // actually reached the required state.
        if (!fastSaveStarted && inSaveRoom && !Player.HasControl &&
            !canStartBePressed && areWeSaving)
        {
            fastSaveStarted = true;

            // Since start menu cant be open if player leaves room during save animation
            // we force Re-enable Start menu once the save operation has finished.
            CanStartBePressed = true;
        }

        // Once started, keep DemoTimer at zero whenever the game takes
        // control away from the player for the rest of the save-room visit.
        if (fastSaveStarted && inSaveRoom && !Player.HasControl) Player.DemoTimer = 0;
            

        // Leaving the save room resets the fast-save state.
        if (!inSaveRoom) fastSaveStarted = false;
    }
}