extern alias unitycore;

using BepInEx.Configuration;
using KeyCode = unitycore::UnityEngine.KeyCode;

namespace AzureArchive.VideoTools.Runtime;

internal sealed record CharacterTransformProofOptions(
    bool Enabled,
    KeyCode ApplyKey,
    KeyCode LeftKey,
    KeyCode ResetKey,
    KeyCode JumpKey,
    KeyCode CrouchKey,
    int InitialPublicSlot,
    bool RequireAltForSlotSelection,
    string SceneIdentity,
    string ApplyDirective,
    string LeftDirective,
    string ResetDirective)
{
    public static CharacterTransformProofOptions Bind(ConfigFile config)
    {
        ConfigEntry<bool> enabled = config.Bind(
            "CharacterTransformProof",
            "Enabled",
            true,
            "Enable the explicit main-thread character transform API proof.");
        ConfigEntry<KeyCode> applyKey = config.Bind(
            "CharacterTransformProof",
            "ApplyKey",
            KeyCode.RightArrow,
            "Move the selected character right using ApplyDirective.");
        ConfigEntry<KeyCode> leftKey = config.Bind(
            "CharacterTransformProof",
            "LeftKey",
            KeyCode.LeftArrow,
            "Move the selected character left using LeftDirective.");
        ConfigEntry<KeyCode> resetKey = config.Bind(
            "CharacterTransformProof",
            "ResetKey",
            KeyCode.F4,
            "Restore the baseline captured by the first successful apply proof.");
        ConfigEntry<KeyCode> jumpKey = config.Bind(
            "CharacterTransformProof",
            "JumpKey",
            KeyCode.UpArrow,
            "Enqueue the official Jump action for the selected character.");
        ConfigEntry<KeyCode> crouchKey = config.Bind(
            "CharacterTransformProof",
            "CrouchKey",
            KeyCode.DownArrow,
            "Enqueue the official downward Greeting action for the selected character.");
        ConfigEntry<int> initialPublicSlot = config.Bind(
            "CharacterTransformProof",
            "InitialPublicSlot",
            3,
            "Initial physical character slot selected for manual controls (1..5).");
        ConfigEntry<bool> requireAltForSlotSelection = config.Bind(
            "CharacterTransformProof",
            "RequireAltForSlotSelection",
            true,
            "Require Alt+1..5 instead of bare number keys when selecting a manual slot.");
        ConfigEntry<string> sceneIdentity = config.Bind(
            "CharacterTransformProof",
            "SceneIdentity",
            "explicit-runtime-proof",
            "Managed proof identity shared by apply and reset calls.");
        ConfigEntry<string> applyDirective = config.Bind(
            "CharacterTransformProof",
            "ApplyDirective",
            "#char;3;move;dx=500;duration=2000;easing=easeInOut",
            "Strict #char right-move directive executed by ApplyKey.");
        ConfigEntry<string> leftDirective = config.Bind(
            "CharacterTransformProof",
            "LeftDirective",
            "#char;3;move;dx=-500;duration=2000;easing=easeInOut",
            "Strict #char left-move directive executed by LeftKey.");
        ConfigEntry<string> resetDirective = config.Bind(
            "CharacterTransformProof",
            "ResetDirective",
            "#char;3;reset;duration=2000;easing=easeInOut",
            "Strict #char reset directive executed by ResetKey.");

        return new CharacterTransformProofOptions(
            enabled.Value,
            applyKey.Value,
            leftKey.Value,
            resetKey.Value,
            jumpKey.Value,
            crouchKey.Value,
            Math.Clamp(initialPublicSlot.Value, 1, 5),
            requireAltForSlotSelection.Value,
            sceneIdentity.Value.Trim(),
            applyDirective.Value.Trim(),
            leftDirective.Value.Trim(),
            resetDirective.Value.Trim());
    }
}
