using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Plays one song in sync with the server clock:
    ///   yt-dlp resolves the audio stream URL -> ffmpeg streams + decodes it to float PCM on stdout ->
    ///   a reader thread fills a ring buffer -> OnAudioFilterRead plays it.
    /// The ring buffer holds a few seconds; when it's full the reader stops reading, so ffmpeg (and the network)
    /// only pull audio at roughly playback speed — a steady ~6 KB/s trickle rather than a download burst.
    /// Sync: the audible song time should equal (server time - start). Late? drop buffered audio. Early? hold.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class MusicStream : MonoBehaviour
    {
        const float BufferSeconds = 8f;
        /// <summary>Roughly how long ffmpeg takes to deliver audio after starting (used when joining mid-song).</summary>
        const float StartupGuess = 2.5f;
        const float MaxDrift = 0.6f;
        const float MinStartBuffer = 0.4f;

        class Pipe
        {
            public Process process;
            public volatile bool stop, eof;
            public long framesIn;
            public string lastError = "";
        }

        public string Status { get; private set; } = "";
        /// <summary>The song for this play id couldn't be played here.</summary>
        public event Action<int> Failed;

        int _rate;
        float[] _ring;
        int _read, _count; // in floats (interleaved stereo)
        readonly object _lock = new();
        long _consumedFrames, _pendingDrop;
        volatile bool _playing;
        volatile float _volume;
        /// <summary>Set when playback (re)starts or jumps: the audio thread ramps gain up from 0 so there's no pop.</summary>
        volatile bool _rampFromZero;
        float _gain;      // audio thread only: smoothed gain actually applied
        float _gainCoef;  // per-sample smoothing factor (~15 ms)

        Pipe _pipe;
        int _gen, _playId;
        double _start, _seek;
        float _duration;
        Func<double> _now;

        void Awake()
        {
            _rate = AudioSettings.outputSampleRate;
            _ring = new float[_rate * 2 * (int)BufferSeconds];
            _gainCoef = 1f - Mathf.Exp(-1f / (_rate * 0.015f));
            _volume = GameSettings.MusicGain;
            var src = GetComponent<AudioSource>();
            // silent looping clip just keeps the source alive so OnAudioFilterRead runs; we write the real samples there
            src.clip = AudioClip.Create("MusicCarrier", _rate, 2, _rate, false);
            src.loop = true;
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.priority = 0;
            src.volume = 1f;
            // master volume is folded into GameSettings.MusicGain ourselves, so it applies to the stream reliably and only once
            src.ignoreListenerVolume = true;
            src.bypassEffects = src.bypassListenerEffects = src.bypassReverbZones = true;
            src.Play();
        }

        void OnDestroy() => Stop();

        public void Play(int playId, string videoId, double start, float duration, Func<double> now)
        {
            Stop();
            int gen = ++_gen;
            _playId = playId;
            _start = start;
            _duration = duration;
            _now = now;
            Status = "Loading...";
            Begin(gen, videoId);
        }

        public void Stop()
        {
            _gen++;
            _playId = 0;
            _playing = false;
            Status = "";
            var p = _pipe;
            _pipe = null;
            if (p != null)
            {
                p.stop = true;
                MusicTools.Kill(p.process);
            }
            lock (_lock)
            {
                _read = _count = 0;
                _consumedFrames = _pendingDrop = 0;
                Monitor.PulseAll(_lock);
            }
        }

        async void Begin(int gen, string videoId)
        {
            string url = await MusicTools.ResolveStream(videoId);
            if (gen != _gen) return;
            if (url == null)
            {
                Fail("Couldn't load this song");
                return;
            }

            double expected = _now() - _start;
            // joining mid-song: start ffmpeg a little ahead, it catches up with the clock while it spins up
            _seek = expected > 3.0 ? expected + StartupGuess : 0.0;
            if (_seek >= _duration - 1.0) { Status = ""; return; }

            string args = "-hide_banner -loglevel error -nostdin -reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 " +
                          $"-ss {_seek.ToString("0.###", CultureInfo.InvariantCulture)} -i \"{url}\" -vn -f f32le -ac 2 -ar {_rate} pipe:1";
            Process proc;
            try { proc = MusicTools.StartProcess(MusicTools.FfmpegPath, args); }
            catch (Exception e) { Fail("Couldn't start ffmpeg: " + e.Message); return; }
            if (proc == null) { Fail("Couldn't start ffmpeg"); return; }

            var pipe = new Pipe { process = proc };
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) pipe.lastError = e.Data; };
            proc.BeginErrorReadLine();
            _pipe = pipe;
            new Thread(() => ReadLoop(pipe)) { IsBackground = true, Name = "MusicStream" }.Start();
        }

        void Fail(string msg)
        {
            int id = _playId;
            Stop();
            Status = msg;
            Failed?.Invoke(id);
        }

        // ------------------------------------------------------------------ reader thread

        void ReadLoop(Pipe pipe)
        {
            var stream = pipe.process.StandardOutput.BaseStream;
            var bytes = new byte[16384];
            var floats = new float[bytes.Length / 4];
            int have = 0;
            try
            {
                while (!pipe.stop)
                {
                    int n = stream.Read(bytes, have, bytes.Length - have);
                    if (n <= 0) break;
                    have += n;
                    int frames = have / 8;
                    int usable = frames * 8;
                    if (frames > 0)
                    {
                        Buffer.BlockCopy(bytes, 0, floats, 0, usable);
                        Push(pipe, floats, frames);
                    }
                    int rest = have - usable;
                    if (rest > 0) Buffer.BlockCopy(bytes, usable, bytes, 0, rest);
                    have = rest;
                }
            }
            catch (Exception)
            {
                // process killed / pipe closed
            }
            finally
            {
                pipe.eof = true;
                MusicTools.Forget(pipe.process);
                try { pipe.process.Dispose(); } catch { /* ignore */ }
            }
        }

        void Push(Pipe pipe, float[] src, int frames)
        {
            lock (_lock)
            {
                for (int f = 0; f < frames; f++)
                {
                    if (pipe.stop) return;
                    pipe.framesIn++;
                    if (_pendingDrop > 0) { _pendingDrop--; _consumedFrames++; continue; }
                    while (_count + 2 > _ring.Length)
                    {
                        Monitor.Wait(_lock, 100); // buffer full: stop reading, which throttles ffmpeg + the download
                        if (pipe.stop) return;
                    }
                    int w = (_read + _count) % _ring.Length;
                    _ring[w] = src[f * 2];
                    _ring[w + 1] = src[f * 2 + 1];
                    _count += 2;
                }
            }
        }

        // ------------------------------------------------------------------ audio thread

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_playing)
            {
                Array.Clear(data, 0, data.Length);
                _gain = 0f;
                return;
            }
            if (_rampFromZero) { _rampFromZero = false; _gain = 0f; }
            float target = _volume, g = _gain, k = _gainCoef;
            int frames = data.Length / channels;
            lock (_lock)
            {
                for (int f = 0; f < frames; f++)
                {
                    // smoothed gain: volume changes, starts and skips glide instead of clicking
                    g += (target - g) * k;
                    float l = 0f, r = 0f;
                    if (_count >= 2)
                    {
                        l = Mathf.Clamp(_ring[_read] * g, -1f, 1f);
                        r = Mathf.Clamp(_ring[_read + 1] * g, -1f, 1f);
                        _read = (_read + 2) % _ring.Length;
                        _count -= 2;
                        _consumedFrames++;
                    }
                    int o = f * channels;
                    if (channels == 1) data[o] = (l + r) * 0.5f;
                    else
                    {
                        data[o] = l;
                        data[o + 1] = r;
                        for (int c = 2; c < channels; c++) data[o + c] = 0f;
                    }
                }
                Monitor.PulseAll(_lock);
            }
            _gain = g;
        }

        // ------------------------------------------------------------------ sync (main thread)

        void Update()
        {
            _volume = GameSettings.MusicGain;
            var pipe = _pipe;
            if (pipe == null || _playId == 0) return;

            double expected = _now() - _start; // where the song should be right now
            double next;
            int buffered;
            lock (_lock)
            {
                next = _seek + (_consumedFrames + _pendingDrop) / (double)_rate;
                buffered = _count / 2;
            }

            if (pipe.eof && buffered == 0)
            {
                if (pipe.framesIn == 0 && !pipe.stop) { Fail("Couldn't play this song" + (pipe.lastError.Length > 0 ? ": " + Short(pipe.lastError) : "")); return; }
                _playing = false;
                Status = "";
                return;
            }

            if (!_playing)
            {
                if (expected < next - 0.02) return; // before the start (or ahead of the clock): wait
                if (buffered < _rate * MinStartBuffer && !pipe.eof) return;
                Drop(expected - next);
                _rampFromZero = true;
                _playing = true;
                Status = "";
            }
            else
            {
                double drift = expected - next;
                if (drift > MaxDrift) Drop(drift);          // behind (stall / hitch): skip ahead
                else if (drift < -MaxDrift) _playing = false; // ahead (clock moved): hold until it catches up
            }
        }

        void Drop(double seconds)
        {
            long frames = (long)(seconds * _rate);
            if (frames <= 0) return;
            _rampFromZero = true; // jumping in the song: fade back in rather than pop
            lock (_lock)
            {
                long have = _count / 2;
                long n = Math.Min(frames, have);
                _read = (int)((_read + n * 2) % _ring.Length);
                _count -= (int)(n * 2);
                _consumedFrames += n;
                _pendingDrop += frames - n;
                Monitor.PulseAll(_lock);
            }
        }

        static string Short(string s) => s.Length > 60 ? s.Substring(0, 60) + "..." : s;
    }
}
