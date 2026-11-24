using System;
using System.Collections.Generic;
using SDL;
using UnityEngine;
using UnityShim.Backend;

namespace UnityShim.AudioInput
{
    public unsafe sealed class SdlAudioBackend : IAudioBackend, IDisposable
    {
        private sealed class Voice
        {
            public int Id;
            public float[] Pcm;
            public double FramePos;
            public ulong DelaySamplesRemaining;
            public float Volume, Pitch, SpatialBlend, MinDist, MaxDist;
            public float X, Y, Z;
            public bool Loop;
            public bool Paused;
        }

        private readonly Dictionary<int, float[]> _clips = new();
        private readonly Dictionary<int, Voice> _voices = new();
        private SDL_AudioStream* _stream;
        private int _nextClipId = 1;
        private int _nextVoiceId = 1;
        private float _lx, _ly, _lz, _lrx = 1f, _lry, _lrz;
        private readonly float[] _mixBuf = new float[2048 * 2];

        public SdlAudioBackend()
        {
            var spec = new SDL_AudioSpec
            {
                format = SDL_AudioFormat.SDL_AUDIO_F32LE,
                channels = 2,
                freq = 44100
            };
            _stream = SDL3.SDL_OpenAudioDeviceStream(SDL3.SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, null, nint.Zero);
            if (_stream != null) SDL3.SDL_ResumeAudioStreamDevice(_stream);
        }

        public int RegisterClip(string name, float[] interleavedStereoPcm, int sampleRate)
        {
            int id = _nextClipId++;
            _clips[id] = interleavedStereoPcm;
            return id;
        }

        public int PlayVoice(int clipId, float volume, float pitch, bool loop, ulong delaySamples,
                             float spatialBlend, float minDist, float maxDist, float x, float y, float z)
        {
            if (!_clips.TryGetValue(clipId, out var pcm) || pcm.Length == 0) return -1;
            int id = _nextVoiceId++;
            _voices[id] = new Voice
            {
                Id = id, Pcm = pcm, Volume = volume, Pitch = pitch, Loop = loop,
                DelaySamplesRemaining = delaySamples, SpatialBlend = spatialBlend,
                MinDist = minDist, MaxDist = maxDist, X = x, Y = y, Z = z
            };
            return id;
        }

        public void UpdateVoice(int voiceId, float volume, float pitch, bool loop,
                                float spatialBlend, float minDist, float maxDist, float x, float y, float z)
        {
            if (!_voices.TryGetValue(voiceId, out var v)) return;
            v.Volume = volume; v.Pitch = pitch; v.Loop = loop;
            v.SpatialBlend = spatialBlend; v.MinDist = minDist; v.MaxDist = maxDist;
            v.X = x; v.Y = y; v.Z = z;
        }

        public void PauseVoice(int voiceId)
        {
            if (_voices.TryGetValue(voiceId, out var v)) v.Paused = true;
        }

        public void StopVoice(int voiceId) => _voices.Remove(voiceId);

        public bool IsVoicePlaying(int voiceId) => _voices.TryGetValue(voiceId, out var v) && !v.Paused;

        public void SetListenerPose(float x, float y, float z, float rx, float ry, float rz)
        {
            _lx = x; _ly = y; _lz = z; _lrx = rx; _lry = ry; _lrz = rz;
        }

        public void StopAllVoices()
        {
            _voices.Clear();
            if (_stream != null) SDL3.SDL_ClearAudioStream(_stream);
        }

        public void Tick(float dt)
        {
            foreach (var src in SceneRegistry.GetAllComponents<AudioSource>()) src.SyncVoice();
            if (_stream == null)
            {
                // Advance/prune non-looping voices cleanly in headless mode
                var dead = new List<int>();
                double adv = Math.Max(0, dt) * 44100.0;
                foreach (var v in _voices.Values)
                {
                    if (v.Paused) continue;
                    int total = v.Pcm.Length / 2;
                    v.FramePos += adv * Math.Max(0.05f, v.Pitch);
                    if (v.FramePos >= total) { if (v.Loop) v.FramePos %= total; else dead.Add(v.Id); }
                }
                foreach (var id in dead) _voices.Remove(id);
                return;
            }

            const int targetQueuedBytes = 4096 * 2 * sizeof(float);
            int queued = SDL3.SDL_GetAudioStreamQueued(_stream);
            if (queued >= targetQueuedBytes) return;

            int framesToMix = 1024;
            Array.Clear(_mixBuf, 0, framesToMix * 2);

            if (_voices.Count > 0)
            {
                var finished = new List<int>();
                foreach (var v in _voices.Values)
                {
                    if (v.Paused) continue;
                    int totalFrames = v.Pcm.Length / 2;
                    if (totalFrames == 0) { finished.Add(v.Id); continue; }

                    float gainL = v.Volume, gainR = v.Volume;
                    if (v.SpatialBlend > 0f)
                    {
                        float dx = v.X - _lx, dy = v.Y - _ly, dz = v.Z - _lz;
                        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                        float minD = MathF.Max(0.1f, v.MinDist);
                        float atten = minD / MathF.Max(dist, minD);
                        if (dist > v.MaxDist) atten = 0f;
                        float pan = dist > 1e-4f ? Math.Clamp((dx * _lrx + dy * _lry + dz * _lrz) / dist, -1f, 1f) : 0f;
                        float attenBlended = 1f + (atten - 1f) * v.SpatialBlend;
                        gainL *= attenBlended * MathF.Min(1f, 1f - pan * 0.5f * v.SpatialBlend);
                        gainR *= attenBlended * MathF.Min(1f, 1f + pan * 0.5f * v.SpatialBlend);
                    }

                    double step = Math.Max(0.05, v.Pitch);
                    for (int f = 0; f < framesToMix; f++)
                    {
                        if (v.DelaySamplesRemaining > 0) { v.DelaySamplesRemaining--; continue; }
                        int idx = (int)v.FramePos;
                        if (idx >= totalFrames)
                        {
                            if (v.Loop) { v.FramePos %= totalFrames; idx = (int)v.FramePos; }
                            else { finished.Add(v.Id); break; }
                        }
                        _mixBuf[f * 2] += v.Pcm[idx * 2] * gainL;
                        _mixBuf[f * 2 + 1] += v.Pcm[idx * 2 + 1] * gainR;
                        v.FramePos += step;
                    }
                }
                foreach (var id in finished) _voices.Remove(id);
            }

            fixed (float* p = _mixBuf)
            {
                SDL3.SDL_PutAudioStreamData(_stream, (nint)p, framesToMix * 2 * sizeof(float));
            }
        }

        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Only tear down while the audio subsystem is still alive; destroying a stream after
            // SDL_Quit / device loss dereferences freed device state (SIGSEGV in pthread_mutex_lock).
            if (_stream != null && SDL3.SDL_WasInit(SDL_InitFlags.SDL_INIT_AUDIO) != 0)
            {
                SDL3.SDL_PauseAudioStreamDevice(_stream);
                SDL3.SDL_DestroyAudioStream(_stream);
            }
            _stream = null;
        }
    }
}
