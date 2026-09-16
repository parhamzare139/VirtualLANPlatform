using System.IO;

namespace VirtualLANPlatform.Core.Services;

public enum AppSound { Join, Leave, Message, File, Connected, PttOn, PttOff }

/// <summary>
/// Short UI cues, generated in-house and shipped as resources so they sound the same
/// on every machine (the old approach borrowed whatever Windows had under Media\,
/// which differs by edition and is often missing). Each player is built once and
/// reused; SoundPlayer.Play is asynchronous and returns immediately.
/// </summary>
public static class AppSounds
{
    private static readonly Dictionary<AppSound, System.Media.SoundPlayer> _players = new();
    private static readonly object _lock = new();

    public static void Play(AppSound sound)
    {
        var s = AppSettings.I;
        if (!s.Sounds) return;
        bool allowed = sound switch
        {
            AppSound.Join or AppSound.Leave or AppSound.Connected => s.SoundJoinLeave,
            AppSound.Message                                    => s.SoundMessage,
            AppSound.File                                       => s.SoundFile,
            _                                                   => true
        };
        if (!allowed) return;
        PlayAlways(sound);
    }

    /// <summary>Ignores the settings — for the "test" buttons on the settings page.</summary>
    public static void PlayAlways(AppSound sound)
    {
        try
        {
            System.Media.SoundPlayer? player;
            lock (_lock)
            {
                if (!_players.TryGetValue(sound, out player))
                {
                    player = Load(sound);
                    if (player != null) _players[sound] = player;
                }
            }
            player?.Play();
        }
        catch { }
    }

    private static System.Media.SoundPlayer? Load(AppSound sound)
    {
        string file = sound switch
        {
            AppSound.Join      => "join.wav",
            AppSound.Leave     => "leave.wav",
            AppSound.Message   => "message.wav",
            AppSound.File      => "file.wav",
            AppSound.Connected => "connected.wav",
            AppSound.PttOn     => "ptt_on.wav",
            AppSound.PttOff    => "ptt_off.wav",
            _                  => ""
        };
        if (file.Length == 0) return null;

        var rs = System.Windows.Application.GetResourceStream(
            new Uri($"pack://application:,,,/Assets/Sounds/{file}"));
        if (rs?.Stream == null) return null;

        // Copy out of the resource stream: SoundPlayer wants a seekable stream it owns.
        var ms = new MemoryStream();
        rs.Stream.CopyTo(ms);
        ms.Position = 0;
        var p = new System.Media.SoundPlayer(ms);
        p.Load();
        return p;
    }
}
