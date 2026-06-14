using Aegis.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Media;

namespace Aegis.Audio;

/// <summary>
/// Gerenciador de áudio com cache.
/// SoundEffect  → sons curtos (SFX): pulo, tiro, coleta
/// Song         → música de fundo via MediaPlayer
/// </summary>
public static class AudioManager
{
    private static readonly Dictionary<string, SoundEffect> _sfx = new();
    private static readonly Dictionary<string, Song> _songs = new();
    private static SoundEffectInstance? _musicSfxInstance;

    private static float _sfxVolume = 1f;
    private static float _musicVolume = 1f;
    private static bool _musicPaused;
    private static readonly Dictionary<string, float> _groupVolumes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sfx"] = 1f,
        ["music"] = 1f
    };

    public static string AudioRoot { get; set; } = "res/audio";

    public static void PlaySound(string file, float volume = 1f, float pitch = 0f, float pan = 0f)
    {
        var sfx = LoadSfx(file);
        volume = Math.Clamp(volume * _sfxVolume * GetGroupVolume("sfx"), 0f, 1f);
        pitch = Math.Clamp(pitch, -1f, 1f);
        pan = Math.Clamp(pan, -1f, 1f);
        sfx.Play(volume, pitch, pan);
    }

    public static void SetSfxVolume(float v)
        => _sfxVolume = Math.Clamp(float.IsFinite(v) ? v : 1f, 0f, 1f);

    public static void PlayMusic(string file, bool loop = true)
    {
        StopMusic();
        if (Path.GetExtension(file).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            var sfx = LoadSfx(file);
            _musicSfxInstance = sfx.CreateInstance();
            _musicSfxInstance.IsLooped = loop;
            _musicSfxInstance.Volume = _musicVolume * GetGroupVolume("music");
            _musicSfxInstance.Play();
            _musicPaused = false;
            return;
        }

        var song = LoadSong(file);
        MediaPlayer.IsRepeating = loop;
        MediaPlayer.Volume = _musicVolume * GetGroupVolume("music");
        MediaPlayer.Play(song);
        _musicPaused = false;
    }

    public static void StopMusic()
    {
        MediaPlayer.Stop();
        _musicSfxInstance?.Stop();
        _musicSfxInstance?.Dispose();
        _musicSfxInstance = null;
        _musicPaused = false;
    }

    public static void PauseMusic()
    {
        MediaPlayer.Pause();
        _musicSfxInstance?.Pause();
        _musicPaused = true;
    }

    public static void ResumeMusic()
    {
        if (!_musicPaused) return;
        if (_musicSfxInstance is not null) _musicSfxInstance.Resume();
        else MediaPlayer.Resume();
        _musicPaused = false;
    }

    public static void SetMusicVolume(float v)
    {
        _musicVolume = Math.Clamp(float.IsFinite(v) ? v : 1f, 0f, 1f);
        MediaPlayer.Volume = _musicVolume * GetGroupVolume("music");
        if (_musicSfxInstance is not null)
            _musicSfxInstance.Volume = _musicVolume * GetGroupVolume("music");
    }

    public static bool IsMusicPlaying => MediaPlayer.State == MediaState.Playing
        || _musicSfxInstance?.State == SoundState.Playing;


    public static void SetGroupVolume(string group, float volume)
    {
        if (string.IsNullOrWhiteSpace(group)) group = "sfx";
        _groupVolumes[group] = Math.Clamp(float.IsFinite(volume) ? volume : 1f, 0f, 1f);
        MediaPlayer.Volume = _musicVolume * GetGroupVolume("music");
        if (_musicSfxInstance is not null)
            _musicSfxInstance.Volume = _musicVolume * GetGroupVolume("music");
    }

    public static float GetGroupVolume(string group)
        => _groupVolumes.TryGetValue(group, out var v) ? v : 1f;

    public static void PlaySoundAt(string file, float x, float y, float cameraX, float cameraY, float viewW, float viewH, float maxDist = 600f, float volume = 1f)
    {
        maxDist = MathF.Max(1f, maxDist);
        var listener = new Vector2(cameraX + viewW * 0.5f, cameraY + viewH * 0.5f);
        var pos = new Vector2(x, y);
        var delta = pos - listener;
        var dist = delta.Length();
        var attenuation = Math.Clamp(1f - dist / maxDist, 0f, 1f);
        var pan = Math.Clamp(delta.X / maxDist, -1f, 1f);
        if (attenuation <= 0.001f) return;
        PlaySound(file, volume * attenuation, 0f, pan);
    }

    public static void CrossfadeTo(string file, float seconds = 1.5f)
    {
        // Implementação segura inicial: troca imediata preservando a API.
        // Crossfade real precisa de duas instâncias de música; MediaPlayer toca só uma Song por vez.
        PlayMusic(file, true);
    }

    public static void PlayMusicLooped(string intro, string loop)
    {
        // Base simples: toca intro sem loop. O loop separado será evoluído com controle de MediaState/tempo.
        PlayMusic(intro, false);
    }

    private static SoundEffect LoadSfx(string file)
    {
        var key = NormalizeKey(file);
        if (_sfx.TryGetValue(key, out var hit)) return hit;

        var path = ResolveAudioPath(key);
        if (!File.Exists(path))
            throw new FileNotFoundException($"[Aegis|Audio] SFX não encontrado: '{path}'");

        using var stream = File.OpenRead(path);
        var sfx = SoundEffect.FromStream(stream);
        _sfx[key] = sfx;
        return sfx;
    }

    private static Song LoadSong(string file)
    {
        var key = NormalizeKey(file);
        if (_songs.TryGetValue(key, out var hit)) return hit;

        var path = ResolveAudioPath(key);
        if (!File.Exists(path))
            throw new FileNotFoundException($"[Aegis|Audio] Music não encontrada: '{path}'");

        var song = Song.FromUri(key, new Uri(Path.GetFullPath(path)));
        _songs[key] = song;
        return song;
    }

    private static string NormalizeKey(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
            throw new ArgumentException("Arquivo de áudio vazio.", nameof(file));
        if (Path.IsPathRooted(file))
            throw new InvalidOperationException($"[Aegis|Audio] Use caminho relativo dentro de {AudioRoot}: '{file}'");
        var normalized = file.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            ? normalized[6..]
            : normalized;
    }

    private static string ResolveAudioPath(string key)
    {
        var root = Path.GetFullPath(AudioRoot);
        var full = Path.GetFullPath(Path.Combine(root, key));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"[Aegis|Audio] Caminho fora da pasta de áudio: '{key}'");
        return full;
    }

    public static void Unload()
    {
        _musicSfxInstance?.Dispose();
        _musicSfxInstance = null;
        foreach (var s in _sfx.Values) s.Dispose();
        _sfx.Clear();
        _songs.Clear();
    }
}
