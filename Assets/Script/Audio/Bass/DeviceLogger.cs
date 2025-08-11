using System;
using System.IO;
using System.Text;
using ManagedBass;
using YARG.Core.Logging;
using UnityEngine;

namespace YARG.Audio.BASS
{
    public static class DeviceLogger
    {
        public static void LogWasapiDevices()
        {
#if UNITY_STANDALONE_WIN && YARG_WASAPI
            for (int i = 0; ManagedBass.Wasapi.BassWasapi.GetDeviceInfo(i, out var info); i++)
            {
                if (!info.IsEnabled || info.IsLoopback) continue;
                YargLogger.LogFormatInfo("WASAPI[{0}] {1} (ch:{2}) share:{3}", i, info.Name, info.MixChannels, info.IsExclusive);
            }
#else
            YargLogger.LogInfo("WASAPI device listing unavailable (not Windows or module missing).");
#endif
        }

        public static void LogAsioDevices()
        {
#if UNITY_STANDALONE_WIN && YARG_ASIO
            int count = ManagedBass.Asio.BassAsio.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                if (ManagedBass.Asio.BassAsio.GetDeviceInfo(i, out var di))
                {
                    YargLogger.LogFormatInfo("ASIO[{0}] {1} driver:{2}", i, di.Name, di.Driver);
                }
            }
#else
            YargLogger.LogInfo("ASIO device listing unavailable (not Windows or module missing).");
#endif
        }

        public static void LogAudioDiagnostics()
        {
            try
            {
                YargLogger.LogInfo("=== Audio System Diagnostics ===");
                
                // Note: Direct access to BassAudioManager would require a global instance
                // For now, we'll log what we can from BASS directly
                YargLogger.LogInfo("Note: Full diagnostics require AudioDiagnosticsCollector instance");

                // Log BASS information
                if (Bass.CurrentDevice != -1)
                {
                    try
                    {
                        var bassInfo = Bass.Info;
                        YargLogger.LogInfo("BASS System Info:");
                        YargLogger.LogFormatInfo("  Version: {0}", Bass.Version);
                        YargLogger.LogFormatInfo("  Device: {0}", Bass.CurrentDevice);
                        YargLogger.LogFormatInfo("  Min Buffer: {0}ms", bassInfo.MinBufferLength);
                        YargLogger.LogFormatInfo("  Current Latency: {0}ms", bassInfo.Latency);
                    }
                    catch (Exception ex)
                    {
                        YargLogger.LogFormatError("Error getting BASS system info: {0}", ex);
                    }
                }

                YargLogger.LogInfo("=== End Audio Diagnostics ===");
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatError("Error logging audio diagnostics: {0}", ex);
            }
        }

        public static void SaveAudioDiagnostics()
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string fileName = $"YARG_AudioDiagnostics_{timestamp}.txt";
                string filePath = Path.Combine(Application.persistentDataPath, fileName);
                
                var sb = new StringBuilder();
                sb.AppendLine($"YARG Audio Diagnostics Report - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("=".PadRight(60, '='));
                sb.AppendLine();

                // Note: Full diagnostics would require AudioDiagnosticsCollector instance
                sb.AppendLine("Note: Full diagnostics require AudioDiagnosticsCollector instance");
                sb.AppendLine();

                // Add BASS system information
                if (Bass.CurrentDevice != -1)
                {
                    try
                    {
                        var bassInfo = Bass.Info;
                        sb.AppendLine("BASS System Information:");
                        sb.AppendLine($"  Version: {Bass.Version}");
                        sb.AppendLine($"  Device: {Bass.CurrentDevice}");
                        sb.AppendLine($"  Min Buffer: {bassInfo.MinBufferLength}ms");
                        sb.AppendLine($"  Current Latency: {bassInfo.Latency}ms");
                        sb.AppendLine();
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"Error getting BASS system information: {ex.Message}");
                        sb.AppendLine();
                    }
                }

                // Add device listings
                sb.AppendLine("Available WASAPI Devices:");
#if UNITY_STANDALONE_WIN && YARG_WASAPI
                for (int i = 0; ManagedBass.Wasapi.BassWasapi.GetDeviceInfo(i, out var info); i++)
                {
                    if (!info.IsEnabled || info.IsLoopback) continue;
                    sb.AppendLine($"  [{i}] {info.Name} (ch:{info.MixChannels}) exclusive:{info.IsExclusive}");
                }
#else
                sb.AppendLine("  WASAPI unavailable (not Windows or module missing)");
#endif
                sb.AppendLine();

                sb.AppendLine("Available ASIO Devices:");
#if UNITY_STANDALONE_WIN && YARG_ASIO
                int count = ManagedBass.Asio.BassAsio.DeviceCount;
                for (int i = 0; i < count; i++)
                {
                    if (ManagedBass.Asio.BassAsio.GetDeviceInfo(i, out var di))
                    {
                        sb.AppendLine($"  [{i}] {di.Name} driver:{di.Driver}");
                    }
                }
#else
                sb.AppendLine("  ASIO unavailable (not Windows or module missing)");
#endif
                
                sb.AppendLine();
                sb.AppendLine("System Information:");
                sb.AppendLine($"  Unity Version: {Application.unityVersion}");
                sb.AppendLine($"  Platform: {Application.platform}");
                sb.AppendLine($"  OS: {SystemInfo.operatingSystem}");
                sb.AppendLine($"  Processor: {SystemInfo.processorType}");
                sb.AppendLine($"  Memory: {SystemInfo.systemMemorySize}MB");

                File.WriteAllText(filePath, sb.ToString());
                
                YargLogger.LogFormatInfo("Audio diagnostics saved to: {0}", filePath);
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatError("Error saving audio diagnostics: {0}", ex);
            }
        }
    }
}
