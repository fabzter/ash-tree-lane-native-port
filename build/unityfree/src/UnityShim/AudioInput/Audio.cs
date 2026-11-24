using System;
using System.IO;
using NVorbis;
using UnityShim.Backend;

namespace UnityEngine
{
    public class AudioClip : Object
    {
        public float length { get; private set; }
        public int channels { get; private set; }
        public int frequency { get; private set; }
        internal int BackendClipId { get; private set; }

        public static AudioClip FromOggFile(string name, string filePath)
        {
            float[] stereo;
            if (filePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                stereo = DecodeWavToStereo44k(filePath);
            }
            else
            {
                try
                {
                    using var reader = new VorbisReader(filePath);
                    int ch = reader.Channels;
                    int sr = reader.SampleRate;
                    var raw = new System.Collections.Generic.List<float>(Math.Max(1024, (int)reader.TotalSamples * ch));
                    float[] buf = new float[4096 * ch];
                    int n;
                    while ((n = reader.ReadSamples(buf, 0, buf.Length)) > 0)
                    {
                        for (int i = 0; i < n; i++) raw.Add(buf[i]);
                    }
                    stereo = ResampleToStereo44k(raw.ToArray(), raw.Count / ch, ch, sr);
                }
                catch
                {
                    stereo = Array.Empty<float>();
                }
            }

            const int targetSr = 44100;
            int outFrames = stereo.Length / 2;
            int backendId = EngineHost.Audio?.RegisterClip(name, stereo, targetSr) ?? 0;
            return new AudioClip
            {
                name = name,
                channels = 2,
                frequency = targetSr,
                length = (float)outFrames / targetSr,
                BackendClipId = backendId
            };
        }

        private static float[] ResampleToStereo44k(float[] raw, int inFrames, int ch, int sr)
        {
            if (inFrames <= 0 || ch <= 0 || sr <= 0) return Array.Empty<float>();
            const int targetSr = 44100;
            int outFrames = sr == targetSr ? inFrames : (int)((double)inFrames * targetSr / sr);
            float[] stereo = new float[outFrames * 2];
            for (int f = 0; f < outFrames; f++)
            {
                double srcPos = sr == targetSr ? f : f * ((double)sr / targetSr);
                int i0 = Math.Clamp((int)srcPos, 0, inFrames - 1);
                int i1 = Math.Min(i0 + 1, inFrames - 1);
                float frac = (float)(srcPos - i0);
                float l0 = raw[i0 * ch];
                float r0 = ch > 1 ? raw[i0 * ch + 1] : l0;
                float l1 = raw[i1 * ch];
                float r1 = ch > 1 ? raw[i1 * ch + 1] : l1;
                stereo[f * 2] = l0 + (l1 - l0) * frac;
                stereo[f * 2 + 1] = r0 + (r1 - r0) * frac;
            }
            return stereo;
        }

        private static float[] DecodeWavToStereo44k(string wavPath)
        {
            using var fs = File.OpenRead(wavPath);
            using var br = new BinaryReader(fs);
            br.ReadBytes(12); // RIFF + size + WAVE
            int ch = 2, sr = 44100, bits = 16, fmt = 1;
            byte[] data = Array.Empty<byte>();
            while (fs.Position + 8 <= fs.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(br.ReadBytes(4));
                int sz = br.ReadInt32();
                if (id == "fmt ")
                {
                    long next = fs.Position + sz;
                    fmt = br.ReadInt16();
                    ch = br.ReadInt16();
                    sr = br.ReadInt32();
                    br.ReadInt32();
                    br.ReadInt16();
                    bits = br.ReadInt16();
                    fs.Position = next;
                }
                else if (id == "data")
                {
                    data = br.ReadBytes(sz);
                }
                else
                {
                    fs.Position = Math.Min(fs.Length, fs.Position + sz);
                }
                if ((sz & 1) != 0 && fs.Position < fs.Length) fs.Position++;
            }
            int bytesPerSample = Math.Max(1, bits / 8);
            int totalSamples = data.Length / bytesPerSample;
            float[] raw = new float[totalSamples];
            if (fmt == 3 && bits == 32)
            {
                Buffer.BlockCopy(data, 0, raw, 0, totalSamples * 4);
            }
            else if (bits == 16)
            {
                for (int i = 0; i < totalSamples; i++) raw[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
            }
            else if (bits == 8)
            {
                for (int i = 0; i < totalSamples; i++) raw[i] = (data[i] - 128) / 128f;
            }
            return ResampleToStereo44k(raw, totalSamples / Math.Max(1, ch), ch, sr);
        }
    }

    public class AudioSource : Behaviour
    {
        private int _voiceId = -1;
        public AudioClip clip { get; set; }
        public float volume { get; set; } = 1f;
        public float pitch { get; set; } = 1f;
        public bool loop { get; set; }
        public bool playOnAwake { get; set; } = true;
        public float spatialBlend { get; set; }
        public float minDistance { get; set; } = 1f;
        public float maxDistance { get; set; } = 500f;

        public bool isPlaying => _voiceId > 0 && (EngineHost.Audio?.IsVoicePlaying(_voiceId) ?? false);

        public void Play() => Play(0);

        public void Play(ulong delay)
        {
            if (clip == null || !isActiveAndEnabled || EngineHost.Audio == null) return;
            if (_voiceId > 0) EngineHost.Audio.StopVoice(_voiceId);
            Vector3 pos = transform != null ? transform.position : Vector3.zero;
            _voiceId = EngineHost.Audio.PlayVoice(
                clip.BackendClipId, volume, pitch, loop, delay, spatialBlend, minDistance, maxDistance, pos.x, pos.y, pos.z);
        }

        public void Pause()
        {
            if (_voiceId > 0) EngineHost.Audio?.PauseVoice(_voiceId);
        }

        public void Stop()
        {
            if (_voiceId > 0) { EngineHost.Audio?.StopVoice(_voiceId); _voiceId = -1; }
        }

        internal void SyncVoice()
        {
            if (_voiceId > 0 && EngineHost.Audio != null)
            {
                Vector3 pos = transform != null ? transform.position : Vector3.zero;
                EngineHost.Audio.UpdateVoice(_voiceId, volume, pitch, loop, spatialBlend, minDistance, maxDistance, pos.x, pos.y, pos.z);
            }
        }
    }
}
