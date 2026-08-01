#if TOOLS
using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.EditorTools;

// The editor's 3D viewport ships with defaults meant for a room-sized scene: 5 m/s freelook (15 with
// Shift, which multiplies by 3) and a 4 km far plane. An Unturned map is 4 km ACROSS, so crossing one at
// the default speed takes about four and a half minutes and the far side is clipped away while you do it.
//
// This rescales the handful of settings that matter to the map being previewed, and can put them back.
// These are EDITOR settings, not project settings — they apply to every project this editor opens, which
// is exactly why the original values are backed up and Restore is one button away.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class ViewportTuning
{
    private const string BackupPath = "user://editor_viewport_backup.cfg";
    private const string BackupSection = "viewport";

    // Free-look metres per second. A map is crossed in ~20 s at span/200, and Shift still triples it for
    // the long hauls. Clamped so a tiny arena map does not end up unusable at 2 m/s and a huge one does
    // not overshoot the whole terrain on a tap.
    private const float MinSpeed = 20f;
    private const float MaxSpeed = 400f;

    private static readonly string[] Tuned =
    {
        "editors/3d/freelook/freelook_base_speed",
        "editors/3d/default_z_far",
        "editors/3d/default_z_near",
        "editors/3d/grid_size",
    };

    // Applies settings scaled to a map `spanMetres` across. Returns the human-readable summary of what it
    // set, so the dock can show it.
    public static string Apply(float spanMetres)
    {
        EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
        Backup(settings);

        float span = spanMetres > 0f ? spanMetres : 4096f;
        float speed = Math.Clamp(span / 20f, MinSpeed, MaxSpeed);
        // Far enough to see the opposite edge from above, and a near plane raised off 0.05: with a far
        // plane in the thousands, that ratio is what makes distant terrain and roads z-fight.
        float zFar = Math.Max(span * 1.5f, 4000f);

        settings.SetSetting("editors/3d/freelook/freelook_base_speed", speed);
        settings.SetSetting("editors/3d/default_z_far", zFar);
        settings.SetSetting("editors/3d/default_z_near", 0.25f);
        // The default 200 m grid is a dense mesh of lines at map scale, and reads as visual noise.
        settings.SetSetting("editors/3d/grid_size", 2000);

        return $"freelook {speed:0} m/s ({speed * 3f:0} with Shift), z_far {zFar:0}, z_near 0.25, grid 2000. " +
            "Re-enter freelook (right-drag) for the speed to take effect.";
    }

    // Restores whatever the settings were before the first Apply. Returns false when there is no backup.
    public static bool Restore()
    {
        var config = new ConfigFile();
        if (config.Load(BackupPath) != Error.Ok)
            return false;

        EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
        foreach (string key in Tuned)
            if (config.HasSectionKey(BackupSection, key))
                settings.SetSetting(key, config.GetValue(BackupSection, key));

        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(BackupPath));
        return true;
    }

    public static bool HasBackup() => new ConfigFile().Load(BackupPath) == Error.Ok;

    // Saves the current values once — a second Apply (a different map) must not overwrite the backup with
    // the already-tuned values, or Restore would put the tuned ones back.
    private static void Backup(EditorSettings settings)
    {
        var config = new ConfigFile();
        if (config.Load(BackupPath) == Error.Ok)
            return;

        foreach (string key in Tuned)
            if (settings.HasSetting(key))
                config.SetValue(BackupSection, key, settings.GetSetting(key));
        config.Save(BackupPath);
    }

    // The settings as they stand now, for display.
    public static IEnumerable<string> Current()
    {
        EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
        foreach (string key in Tuned)
            if (settings.HasSetting(key))
                yield return $"{key.Substring(key.LastIndexOf('/') + 1)}={settings.GetSetting(key)}";
    }
}
#endif
