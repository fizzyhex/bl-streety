using BoneLib.Notifications;

using MelonLoader;

using UnityEngine;

namespace Streety.Services;

/// <summary>
/// ctrl+v with <c>mapskey:&lt;key&gt;</c> on the clipboard swaps in a new maps key, then wipes the clipboard so the
/// key doesn't hang around in it. never logs the key.
/// </summary>
public static class MapsKeyPaste
{
    public static void Update()
    {
        if (!Input.GetKeyDown(KeyCode.V))
            return;

        if (!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl))
            return;

        var text = GUIUtility.systemCopyBuffer?.Trim();

        if (text == null || !text.StartsWith(Prefs.MapsKeyPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        var key = Prefs.StripMapsKeyPrefix(text);
        GUIUtility.systemCopyBuffer = string.Empty;

        if (key.Length == 0)
        {
            Notify("that maps key was empty", NotificationType.Warning);
            return;
        }

        Prefs.MapsKey.Value = key;
        MelonPreferences.Save();

        StreetyMod.Log.Msg("maps key received from the clipboard");
        Notify("key received, your keyboard has been flushed.", NotificationType.Success);

        // a running session holds the old key; start it over with the new one
        StreetyMod.RestartSession();
    }

    private static void Notify(string message, NotificationType type)
    {
        Notifier.Send(new Notification
        {
            Title = "streety",
            Message = message,
            Type = type,
            PopupLength = 3f,
            ShowTitleOnPopup = true,
        });
    }
}
