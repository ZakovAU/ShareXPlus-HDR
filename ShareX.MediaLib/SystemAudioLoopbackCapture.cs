#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2025 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using NAudio.Wave;
using ShareX.HelpersLib;
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace ShareX.MediaLib
{
    /// <summary>
    /// Captures system audio (what is playing on the default output device) using
    /// WASAPI loopback, resamples it to 48 kHz 16-bit stereo PCM and streams it to a
    /// named pipe. FFmpeg reads the pipe as a raw PCM input, which works with any
    /// FFmpeg build (no WASAPI indev or virtual audio driver required) and keeps the
    /// process stdin free for the "q" stop command.
    /// </summary>
    public class SystemAudioLoopbackCapture : IDisposable
    {
        public const string PipeName = "ShareX_ScreenAudio";
        public const string PipePath = @"\\.\pipe\" + PipeName;

        // FFmpeg raw PCM input format: signed 16-bit little endian, 48 kHz, stereo.
        public const int SampleRate = 48000;
        public const int Channels = 2;

        private WasapiLoopbackCapture capture;
        private BufferedWaveProvider buffer;
        private MediaFoundationResampler resampler;
        private NamedPipeServerStream pipe;
        private Thread pumpThread;
        private volatile bool stopRequested;

        public WaveFormat CaptureWaveFormat => capture?.WaveFormat;

        public void Start()
        {
            if (pipe != null)
            {
                return;
            }

            capture = new WasapiLoopbackCapture();
            buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(10),
                DiscardOnBufferOverflow = false
            };
            capture.DataAvailable += (sender, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

            WaveFormat sourceFormat = capture.WaveFormat;
            bool needsResampler = sourceFormat.Encoding != WaveFormatEncoding.IeeeFloat ||
                sourceFormat.SampleRate != SampleRate || sourceFormat.Channels != Channels;

            if (needsResampler)
            {
                resampler = new MediaFoundationResampler(buffer, new WaveFormat(SampleRate, 16, Channels))
                {
                    ResamplerQuality = 60
                };
            }

            pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            stopRequested = false;

            pumpThread = new Thread(PumpAudio)
            {
                IsBackground = true,
                Name = "SystemAudioLoopbackCapture"
            };
            pumpThread.Start();

            capture.StartRecording();
        }

        private void PumpAudio()
        {
            try
            {
                pipe.WaitForConnection();
                DebugHelper.WriteLine("SystemAudioLoopbackCapture: FFmpeg connected to audio pipe.");

                // Fast path: the standard mix format (48 kHz float32 stereo) is
                // converted to s16 PCM directly, avoiding the resampler entirely.
                WaveFormat sourceFormat = capture.WaveFormat;
                bool directFloat = sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat &&
                    sourceFormat.SampleRate == SampleRate && sourceFormat.Channels == Channels;

                byte[] pcm = new byte[SampleRate * Channels * 2 / 10]; // 100 ms of output audio
                byte[] sourceBytes = directFloat ? new byte[pcm.Length * 2] : null;
                int inputBytesPer100Ms = directFloat ? sourceBytes.Length : Math.Max(1, sourceFormat.AverageBytesPerSecond / 10);

                int outByteRate = SampleRate * 2 * Channels;
                long writtenTotal = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();

                while (!stopRequested)
                {
                    if (buffer.BufferedBytes >= inputBytesPer100Ms)
                    {
                        int read;

                        if (directFloat)
                        {
                            int sourceRead = buffer.Read(sourceBytes, 0, sourceBytes.Length);
                            read = ConvertFloatToS16(sourceBytes, sourceRead, pcm);
                        }
                        else
                        {
                            read = resampler.Read(pcm, 0, pcm.Length);
                        }

                        if (read > 0)
                        {
                            pipe.Write(pcm, 0, read);
                            writtenTotal += read;

                            // Pace output to real time so FFmpeg sees a live audio stream
                            double aheadSeconds = writtenTotal / (double)outByteRate - clock.Elapsed.TotalSeconds;
                            if (aheadSeconds > 0.05)
                            {
                                Thread.Sleep((int)(aheadSeconds * 1000));
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }
            }
            catch (Exception e)
            {
                // Pipe closed or device removed; not fatal, video keeps recording.
                DebugHelper.WriteException(e, "SystemAudioLoopbackCapture: audio pump stopped.");
            }
        }

        private static int ConvertFloatToS16(byte[] source, int sourceCount, byte[] dest)
        {
            int sampleCount = sourceCount / 4;
            int outCount = Math.Min(sampleCount, dest.Length / 2);

            for (int i = 0; i < outCount; i++)
            {
                float f = BitConverter.ToSingle(source, i * 4);
                int s = (int)(f * 32768f);
                if (s > 32767) s = 32767;
                else if (s < -32768) s = -32768;
                dest[i * 2] = (byte)s;
                dest[i * 2 + 1] = (byte)(s >> 8);
            }

            return outCount * 2;
        }

        public void Dispose()
        {
            stopRequested = true;

            try
            {
                capture?.StopRecording();
            }
            catch { }

            try
            {
                pipe?.Dispose(); // EOF for FFmpeg
            }
            catch { }

            try
            {
                pumpThread?.Join(2000);
            }
            catch { }

            resampler?.Dispose();
            capture?.Dispose();

            pipe = null;
            capture = null;
            resampler = null;
        }
    }
}
