using System;

namespace YARG.Audio.BASS
{
    public class AudioDiagnosticsInfo
    {
        public string BackendName { get; set; } = "Unknown";
        public string BackendType { get; set; } = "Unknown";
        public string DeviceName { get; set; } = "Unknown";
        public int DeviceId { get; set; } = -1;
        public int SampleRate { get; set; } = 0;
        public int Channels { get; set; } = 0;
        public int BitDepth { get; set; } = 0;
        public double LatencyMs { get; set; } = 0.0;
        public int BufferSize { get; set; } = 0;
        public int Period { get; set; } = 0;
        public bool IsConnected { get; set; } = false;
        public bool HasErrors { get; set; } = false;
        public string LastError { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; } = DateTime.Now;
        
        public AudioDiagnosticsInfo()
        {
        }
        
        public AudioDiagnosticsInfo(string backendName, string backendType)
        {
            BackendName = backendName;
            BackendType = backendType;
        }
        
        public void Update()
        {
            LastUpdated = DateTime.Now;
        }
        
        public string GetDisplayString()
        {
            return $"{BackendName} | {DeviceName} | {SampleRate}Hz | {Channels}ch | {LatencyMs:F1}ms";
        }
        
        public string GetDetailedString()
        {
            return $"Backend: {BackendName} ({BackendType})\n" +
                   $"Device: {DeviceName} (ID: {DeviceId})\n" +
                   $"Format: {SampleRate}Hz, {Channels}ch, {BitDepth}-bit\n" +
                   $"Latency: {LatencyMs:F1}ms\n" +
                   $"Buffer: {BufferSize} samples, Period: {Period}\n" +
                   $"Status: {(IsConnected ? "Connected" : "Disconnected")}\n" +
                   $"Last Error: {(string.IsNullOrEmpty(LastError) ? "None" : LastError)}\n" +
                   $"Updated: {LastUpdated:HH:mm:ss}";
        }
    }
}